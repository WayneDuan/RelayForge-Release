using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;

namespace RelayForge.Panel.Api;

public static class RelayForgeApi
{
    public static async Task<WebApplication> CreateAsync(string[] args)
    {
var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.AddRelayForgeServices(builder.Configuration);

var app = builder.Build();
var maxRequestBodyBytes = builder.Configuration.GetValue<long?>("Security:MaxRequestBodyBytes") ?? 1_048_576;
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength is long length && length > maxRequestBodyBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }
    await next();
});
if (builder.Configuration.GetValue("Panel:RequireHttps", true))
{
    app.Use(async (context, next) =>
    {
        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        var trustedHttps = context.Request.IsHttps || string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase);
        if (!trustedHttps && !context.Request.Path.StartsWithSegments("/health"))
        {
            var target = $"https://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            context.Response.Redirect(target, permanent: true);
            return;
        }
        await next();
    });
    app.UseHsts();
}
app.UseCors("RelayForge");
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/", () => Results.Ok(new { service = "relayforge", product = "RelayForge", runtime = ".NET 10" }));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/flow/test", () => Results.Text("test"));
app.Map("/system-info", async (HttpContext context, NodeGateway gateway) => await gateway.AcceptAsync(context));

app.MapPost("/api/v1/user/login", async (LoginRequest request, HttpContext context, Db db, PasswordService passwords, TotpService totp, AesCrypto crypto, TokenService tokens, LoginRateLimiter limiter, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Api.Error("username and password are required");
    if (request.Username.Length > 100 || request.Password.Length > 512)
        return Api.Error("invalid username or password");
    var username = request.Username.Trim();
    var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var rateKey = $"{remote}|{username.ToUpperInvariant()}";
    if (!limiter.TryAcquire(rateKey, out var retryAfter))
    {
        context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return Api.Error("登录尝试过于频繁，请稍后再试", 429);
    }
    var rows = await db.QueryAsync("SELECT * FROM `user` WHERE `user` = @user LIMIT 1", Domain.Params(("user", username)), ct);
    if (rows.Count == 0 || !passwords.Verify(request.Password, DbValue.String(rows[0], "pwd")))
    {
        limiter.RegisterFailure(rateKey);
        return Api.Error("invalid username or password");
    }
    if (DbValue.Int(rows[0], "status") == 0)
    {
        limiter.RegisterFailure(rateKey);
        return Api.Error("account disabled");
    }
    if (DbValue.Int(rows[0], "totp_enabled") != 0)
    {
        var encryptedSecret = DbValue.String(rows[0], "totp_secret_cipher");
        string secret;
        try { secret = crypto.Decrypt(encryptedSecret); }
        catch { return Api.TotpRequired("2FA 配置无效，请联系管理员"); }
        if (!totp.Verify(request.TotpCode, secret))
        {
            limiter.RegisterFailure(rateKey);
            return Api.TotpRequired();
        }
    }
    var user = new AuthUser(DbValue.Long(rows[0], "id"), DbValue.Int(rows[0], "role_id"), DbValue.String(rows[0], "user"));
    limiter.RegisterSuccess(rateKey);
    return Api.Ok(new { token = tokens.Create(user), role_id = user.RoleId, name = user.Name, requirePasswordChange = user.Name == "admin_user" && request.Password == "admin_user" });
});

app.MapPost("/api/v1/user/ws-ticket", (HttpContext context, NodeGateway gateway) =>
{
    if (!Auth.TryUser(context, out var user, out var error)) return error!;
    if (!Domain.IsAdmin(user!)) return Api.Error("forbidden", 403);
    return Api.Ok(new { ticket = gateway.IssueAdminTicket(user!) });
});

app.MapPost("/api/v1/user/list", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error) || !Domain.IsAdmin(user!)) return error ?? Api.Error("forbidden", 403);
    var rows = await db.QueryAsync("SELECT * FROM `user` WHERE role_id <> 0 ORDER BY id DESC", cancellationToken: ct);
    return Api.Ok(rows.Select(Domain.User).ToList());
});

app.MapPost("/api/v1/user/create", async (UserRequest request, HttpContext context, Db db, PasswordService passwords, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    if (string.IsNullOrWhiteSpace(request.User) || string.IsNullOrWhiteSpace(request.Pwd)) return Api.Error("user and password are required");
    if (Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM `user` WHERE `user` = @user", Domain.Params(("user", request.User.Trim())), ct)) > 0) return Api.Error("username already exists");
    var now = Domain.Now();
    await db.ExecuteAsync("INSERT INTO `user` (`user`,pwd,role_id,exp_time,flow,in_flow,out_flow,flow_reset_time,num,created_time,updated_time,status) VALUES (@user,@pwd,1,@exp,@flow,0,0,@reset,@num,@now,@now,@status)", Domain.Params(("user", request.User.Trim()), ("pwd", passwords.Hash(request.Pwd)), ("exp", request.ExpTime), ("flow", request.Flow), ("reset", request.FlowResetTime), ("num", request.Num), ("now", now), ("status", request.Status ?? 1)), ct);
    return Api.Ok(null, "user created");
});

app.MapPost("/api/v1/user/update", async (UserUpdateRequest request, HttpContext context, Db db, PasswordService passwords, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    if (request.Id <= 0 || string.IsNullOrWhiteSpace(request.User)) return Api.Error("invalid user");
    var passwordPart = string.IsNullOrWhiteSpace(request.Pwd) ? "" : ", pwd=@pwd";
    var sql = $"UPDATE `user` SET `user`=@user, flow=@flow, num=@num, exp_time=@exp, flow_reset_time=@reset, status=@status, updated_time=@now{passwordPart} WHERE id=@id AND role_id <> 0";
    var parameters = Domain.Params(("user", request.User.Trim()), ("flow", request.Flow), ("num", request.Num), ("exp", request.ExpTime), ("reset", request.FlowResetTime), ("status", request.Status ?? 1), ("now", Domain.Now()), ("id", request.Id));
    if (!string.IsNullOrWhiteSpace(request.Pwd)) parameters["pwd"] = passwords.Hash(request.Pwd);
    return await db.ExecuteAsync(sql, parameters, ct) == 0 ? Api.Error("user not found") : Api.Ok(null, "user updated");
});

app.MapPost("/api/v1/user/delete", async (IdRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    await db.ExecuteAsync("DELETE FROM `forward` WHERE user_id=@id", Domain.Params(("id", request.Id)), ct);
    await db.ExecuteAsync("DELETE FROM `user_tunnel` WHERE user_id=@id", Domain.Params(("id", request.Id)), ct);
    await db.ExecuteAsync("DELETE FROM xui_inbound WHERE connection_id IN (SELECT id FROM xui_connection WHERE user_id=@id)", Domain.Params(("id", request.Id)), ct);
    await db.ExecuteAsync("DELETE FROM xui_connection WHERE user_id=@id", Domain.Params(("id", request.Id)), ct);
    return await db.ExecuteAsync("DELETE FROM `user` WHERE id=@id AND role_id <> 0", Domain.Params(("id", request.Id)), ct) == 0 ? Api.Error("user not found") : Api.Ok(null, "user deleted");
});

app.MapPost("/api/v1/user/updatePassword", async (ChangePasswordRequest request, HttpContext context, Db db, PasswordService passwords, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT pwd FROM `user` WHERE id=@id", Domain.Params(("id", user!.Id)), ct);
    if (rows.Count == 0 || string.IsNullOrWhiteSpace(request.NewPassword) || !passwords.Verify(request.CurrentPassword ?? "", DbValue.String(rows[0], "pwd"))) return Api.Error("current password is incorrect");
    if (request.NewPassword != request.ConfirmPassword) return Api.Error("passwords do not match");
    var username = string.IsNullOrWhiteSpace(request.NewUsername) ? user.Name : request.NewUsername.Trim();
    await db.ExecuteAsync("UPDATE `user` SET `user`=@user,pwd=@pwd,updated_time=@now WHERE id=@id", Domain.Params(("user", username), ("pwd", passwords.Hash(request.NewPassword)), ("now", Domain.Now()), ("id", user.Id)), ct);
    return Api.Ok(null, "password updated");
});

app.MapPost("/api/v1/user/2fa/status", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT totp_enabled FROM `user` WHERE id=@id", Domain.Params(("id", user!.Id)), ct);
    return rows.Count == 0 ? Api.Error("user not found") : Api.Ok(new { enabled = DbValue.Int(rows[0], "totp_enabled") != 0 });
});

app.MapPost("/api/v1/user/2fa/setup", (HttpContext context, TotpService totp) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var secret = totp.GenerateSecret();
    return Api.Ok(new { secret, otpauthUri = totp.BuildUri(secret, user!.Name) });
});

app.MapPost("/api/v1/user/2fa/enable", async (TotpEnableRequest request, HttpContext context, Db db, PasswordService passwords, TotpService totp, AesCrypto crypto, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT pwd FROM `user` WHERE id=@id", Domain.Params(("id", user!.Id)), ct);
    if (rows.Count == 0 || !passwords.Verify(request.CurrentPassword ?? "", DbValue.String(rows[0], "pwd"))) return Api.Error("当前密码不正确");
    if (!totp.IsValidSecret(request.Secret) || !totp.Verify(request.Code, request.Secret)) return Api.Error("2FA 验证码无效，请检查认证器时间和密钥");
    var secret = request.Secret!.Trim().Replace(" ", "", StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
    await db.ExecuteAsync("UPDATE `user` SET totp_enabled=1,totp_secret_cipher=@secret,updated_time=@now WHERE id=@id", Domain.Params(("secret", crypto.Encrypt(secret)), ("now", Domain.Now()), ("id", user.Id)), ct);
    return Api.Ok(new { enabled = true }, "2FA 已启用");
});

app.MapPost("/api/v1/user/2fa/disable", async (TotpDisableRequest request, HttpContext context, Db db, PasswordService passwords, TotpService totp, AesCrypto crypto, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT pwd,totp_enabled,totp_secret_cipher FROM `user` WHERE id=@id", Domain.Params(("id", user!.Id)), ct);
    if (rows.Count == 0 || !passwords.Verify(request.CurrentPassword ?? "", DbValue.String(rows[0], "pwd"))) return Api.Error("当前密码不正确");
    if (DbValue.Int(rows[0], "totp_enabled") != 0)
    {
        string secret;
        try { secret = crypto.Decrypt(DbValue.String(rows[0], "totp_secret_cipher")); }
        catch { return Api.Error("2FA 配置无效，请联系管理员"); }
        if (!totp.Verify(request.Code, secret)) return Api.Error("2FA 验证码无效");
    }
    await db.ExecuteAsync("UPDATE `user` SET totp_enabled=0,totp_secret_cipher=NULL,updated_time=@now WHERE id=@id", Domain.Params(("now", Domain.Now()), ("id", user.Id)), ct);
    return Api.Ok(new { enabled = false }, "2FA 已关闭");
});

app.MapPost("/api/v1/user/reset", async (ResetRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var table = request.Type == 1 ? "`user`" : "user_tunnel";
    return Api.Ok(await db.ExecuteAsync($"UPDATE {table} SET in_flow=0,out_flow=0 WHERE id=@id", Domain.Params(("id", request.Id)), ct), "flow reset");
});

app.MapPost("/api/v1/user/package", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var userRows = await db.QueryAsync("SELECT * FROM `user` WHERE id=@id LIMIT 1", Domain.Params(("id", user!.Id)), ct);
    if (userRows.Count == 0) return Api.Error("user not found");
    var tunnelRows = await db.QueryAsync("SELECT ut.*, t.name tunnel_name, t.flow tunnel_flow, sl.name speed_name, sl.speed FROM user_tunnel ut LEFT JOIN tunnel t ON t.id=ut.tunnel_id LEFT JOIN speed_limit sl ON sl.id=ut.speed_id WHERE ut.user_id=@id ORDER BY ut.id", Domain.Params(("id", user.Id)), ct);
    var forwardRows = await db.QueryAsync("SELECT f.*, t.name tunnel_name, t.type tunnel_type, t.in_ip, t.out_ip FROM `forward` f LEFT JOIN tunnel t ON t.id=f.tunnel_id WHERE f.user_id=@id ORDER BY f.created_time DESC", Domain.Params(("id", user.Id)), ct);
    var flowRows = await db.QueryAsync("SELECT * FROM statistics_flow WHERE user_id=@id ORDER BY id DESC LIMIT 24", Domain.Params(("id", user.Id)), ct);
    return Api.Ok(new { userInfo = Domain.User(userRows[0]), tunnelPermissions = tunnelRows.Select(Domain.UserTunnel).ToList(), forwards = forwardRows.Select(Domain.Forward).ToList(), statisticsFlows = flowRows });
});

app.MapPost("/api/v1/node/list", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT * FROM `node` ORDER BY id DESC", cancellationToken: ct);
    return Api.Ok(rows.Select(row => Domain.Node(row)).ToList());
});

app.MapPost("/api/v1/node/create", async (NodeRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    if (string.IsNullOrWhiteSpace(request.Name)) return Api.Error("invalid node settings");
    if (!PortRangeRules.TryParseOptional(request.PortRange, request.PortSta, request.PortEnd, out var normalizedRange, out var ranges, out var rangeError)) return Api.Error(rangeError ?? "invalid node settings");
    var now = Domain.Now();
    var rangeStart = ranges.Count > 0 ? ranges[0].Start : 0;
    var rangeEnd = ranges.Count > 0 ? ranges[0].End : 0;
    await db.ExecuteAsync("INSERT INTO `node` (name,secret,ip,server_ip,port_sta,port_end,port_range,http,tls,socks,created_time,updated_time,status) VALUES (@name,@secret,@ip,@server,@sta,@end,@range,@http,@tls,@socks,@now,@now,0)", Domain.Params(("name", request.Name.Trim()), ("secret", Domain.NewSecret()), ("ip", request.Ip ?? ""), ("server", request.ServerIp?.Trim() ?? ""), ("sta", rangeStart), ("end", rangeEnd), ("range", normalizedRange), ("http", request.Http ?? 0), ("tls", request.Tls ?? 0), ("socks", request.Socks ?? 0), ("now", now)), ct);
    return Api.Ok(null, "node created");
});

app.MapPost("/api/v1/node/update", async (NodeUpdateRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    if (request.Id <= 0) return Api.Error("node not found");
    if (string.IsNullOrWhiteSpace(request.Name)) return Api.Error("invalid node settings");
    if (!PortRangeRules.TryParseOptional(request.PortRange, request.PortSta, request.PortEnd, out var normalizedRange, out var ranges, out var rangeError)) return Api.Error(rangeError ?? "invalid node settings");
    var exists = Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM `node` WHERE id=@id", Domain.Params(("id", request.Id)), ct)) > 0;
    if (!exists) return Api.Error("node not found");
    var rangeStart = ranges.Count > 0 ? ranges[0].Start : 0;
    var rangeEnd = ranges.Count > 0 ? ranges[0].End : 0;
    await db.ExecuteAsync("UPDATE `node` SET name=@name,ip=@ip,server_ip=@server,port_sta=@sta,port_end=@end,port_range=@range,http=@http,tls=@tls,socks=@socks,updated_time=@now WHERE id=@id", Domain.Params(("name", request.Name.Trim()), ("ip", request.Ip ?? ""), ("server", request.ServerIp?.Trim() ?? ""), ("sta", rangeStart), ("end", rangeEnd), ("range", normalizedRange), ("http", request.Http ?? 0), ("tls", request.Tls ?? 0), ("socks", request.Socks ?? 0), ("now", Domain.Now()), ("id", request.Id)), ct);
    return Api.Ok(null, "node updated");
});

app.MapPost("/api/v1/node/delete", async (IdRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var used = Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM tunnel WHERE in_node_id=@id OR out_node_id=@id", Domain.Params(("id", request.Id)), ct));
    if (used > 0) return Api.Error("node is used by tunnels");
    return await db.ExecuteAsync("DELETE FROM `node` WHERE id=@id", Domain.Params(("id", request.Id)), ct) == 0 ? Api.Error("node not found") : Api.Ok(null, "node deleted");
});

app.MapPost("/api/v1/node/install", async (NodeInstallRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT * FROM `node` WHERE id=@id", Domain.Params(("id", request.Id)), ct);
    if (rows.Count == 0) return Api.Error("node not found");
    var script = app.Configuration["Panel:InstallScriptUrl"] ?? "https://github.com/WayneDuan/RelayForge-Release/releases/latest/download/install.sh";
    var releaseBase = app.Configuration["Panel:ReleaseBaseUrl"] ?? "https://github.com/WayneDuan/RelayForge-Release/releases/latest/download";
    var checksums = app.Configuration["Panel:ChecksumsUrl"] ?? "https://github.com/WayneDuan/RelayForge-Release/releases/latest/download/checksums.txt";
    var configuredHost = Convert.ToString(await db.ScalarAsync("SELECT value FROM vite_config WHERE name=@name LIMIT 1", Domain.Params(("name", "panel_host")), ct))?.Trim();
    var configuredPort = Convert.ToString(await db.ScalarAsync("SELECT value FROM vite_config WHERE name=@name LIMIT 1", Domain.Params(("name", "backend_port")), ct))?.Trim();
    var configuredSecurePort = Convert.ToString(await db.ScalarAsync("SELECT value FROM vite_config WHERE name=@name LIMIT 1", Domain.Params(("name", "secure_port")), ct))?.Trim();
    var configuredSecure = Convert.ToString(await db.ScalarAsync("SELECT value FROM vite_config WHERE name=@name LIMIT 1", Domain.Params(("name", "panel_secure")), ct))?.Trim();
    var panelAddress = BuildPanelAddress(context, configuredHost, configuredPort, configuredSecurePort, configuredSecure, builder.Configuration.GetValue("Panel:RequireHttps", true));
    if (panelAddress is null) return Api.Error("panel host or backend port is invalid");
    if (string.Equals(request.Platform?.Trim(), "windows", StringComparison.OrdinalIgnoreCase))
    {
        var windowsScript = app.Configuration["Panel:WindowsInstallScriptUrl"] ?? "https://github.com/WayneDuan/RelayForge-Release/releases/latest/download/install-windows.ps1";
        var scriptPath = "$scriptPath = Join-Path $env:TEMP 'relayforge-agent-install.ps1'";
        var checksumsPath = "$checksumsPath = Join-Path $env:TEMP 'relayforge-agent-checksums.txt'";
        return Api.Ok($"{scriptPath}; {checksumsPath}; Invoke-WebRequest -UseBasicParsing -Uri {PowerShellQuote(windowsScript)} -OutFile $scriptPath; Invoke-WebRequest -UseBasicParsing -Uri {PowerShellQuote(checksums)} -OutFile $checksumsPath; $checksumLine = (Select-String -LiteralPath $checksumsPath -Pattern '  install-windows\\.ps1$' | Select-Object -First 1).Line; if ([string]::IsNullOrWhiteSpace($checksumLine)) {{ throw 'The install script checksum is missing.' }}; $expectedHash = ($checksumLine -split '\\s+')[0].ToUpperInvariant(); $actualHash = (Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash.ToUpperInvariant(); if ($actualHash -ne $expectedHash) {{ throw 'The install script checksum does not match.' }}; & $scriptPath -PanelAddress {PowerShellQuote(panelAddress)} -Secret {PowerShellQuote(DbValue.String(rows[0], "secret"))} -ReleaseBaseUrl {PowerShellQuote(releaseBase)}");
    }
    return Api.Ok($"curl --fail --location --proto '=https' --tlsv1.2 {ShellQuote(script)} -o ./install.sh && curl --fail --location --proto '=https' --tlsv1.2 {ShellQuote(checksums)} -o ./checksums.txt && grep '  install.sh$' ./checksums.txt | sha256sum -c - && chmod +x ./install.sh && RELAYFORGE_RELEASE_BASE_URL={ShellQuote(releaseBase)} ./install.sh -a {ShellQuote(panelAddress)} -s {ShellQuote(DbValue.String(rows[0], "secret"))}");
});
app.MapPost("/api/v1/node/check-status", async (JsonElement request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var hasId = request.TryGetProperty("nodeId", out var nodeId);
    var rows = await db.QueryAsync(hasId ? "SELECT * FROM `node` WHERE id=@id" : "SELECT * FROM `node` ORDER BY id DESC", hasId ? Domain.Params(("id", nodeId.GetInt64())) : null, ct);
    return Api.Ok(rows.Select(row => Domain.Node(row)).ToList());
});

app.MapPost("/api/v1/xui/list", async (HttpContext context, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out var user, out var error)) return error!;
    return await app.Services.GetRequiredService<XuiIntegrationService>().ListConnectionsAsync(user!, ct);
});
app.MapPost("/api/v1/xui/inbounds", async (HttpContext context, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out var user, out var error)) return error!;
    return await app.Services.GetRequiredService<XuiIntegrationService>().ListInboundsAsync(user!, ct);
});
app.MapPost("/api/v1/xui/create", async (XuiConnectionRequest request, HttpContext context, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out var user, out var error)) return error!;
    return await app.Services.GetRequiredService<XuiIntegrationService>().CreateAsync(request, user!, ct);
});
app.MapPost("/api/v1/xui/sync", async (XuiSyncRequest request, HttpContext context, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out var user, out var error)) return error!;
    return await app.Services.GetRequiredService<XuiIntegrationService>().SyncAsync(request.Id, user!, ct);
});
app.MapPost("/api/v1/xui/delete", async (IdRequest request, HttpContext context, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out var user, out var error)) return error!;
    return await app.Services.GetRequiredService<XuiIntegrationService>().DeleteAsync(request.Id, user!, ct);
});

app.MapPost("/api/v1/tunnel/list", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var flowJoin = " LEFT JOIN (SELECT tunnel_id,SUM(in_flow) tunnel_in_flow,SUM(out_flow) tunnel_out_flow FROM `forward` GROUP BY tunnel_id) tf ON tf.tunnel_id=t.id";
    var sql = Domain.IsAdmin(user!) ? $"SELECT t.*, n.ip in_ip, n.port_sta, n.port_end, o.ip out_ip, tf.tunnel_in_flow, tf.tunnel_out_flow FROM tunnel t LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id{flowJoin} ORDER BY t.id DESC" : $"SELECT t.*, n.ip in_ip, n.port_sta, n.port_end, o.ip out_ip, tf.tunnel_in_flow, tf.tunnel_out_flow FROM tunnel t JOIN user_tunnel ut ON ut.tunnel_id=t.id LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id{flowJoin} WHERE ut.user_id=@user AND ut.status=1 AND t.status=1 ORDER BY t.id DESC";
    var rows = await db.QueryAsync(sql, Domain.IsAdmin(user!) ? null : Domain.Params(("user", user!.Id)), ct);
    return Api.Ok(rows.Select(Domain.Tunnel).ToList());
});

app.MapPost("/api/v1/tunnel/create", async (TunnelRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    if (request.Type is not (1 or 2 or 3) || request.FlowType is not (1 or 2) || request.FlowLimitGb < 0) return Api.Error("invalid tunnel settings");
    var inRows = await db.QueryAsync("SELECT * FROM node WHERE id=@id", Domain.Params(("id", request.InNodeId)), ct);
    if (inRows.Count == 0) return Api.Error("input node not found");
    var outId = request.Type == 1 ? request.InNodeId : request.OutNodeId;
    if (outId is null || request.Type == 2 && outId == request.InNodeId) return Api.Error("invalid output node");
    var outRows = await db.QueryAsync("SELECT * FROM node WHERE id=@id", Domain.Params(("id", outId)), ct);
    if (outRows.Count == 0) return Api.Error("output node not found");
    if (request.Type == 3 && string.IsNullOrWhiteSpace(DbValue.String(inRows[0], "server_ip"))) return Api.Error("反向中继的入口节点必须配置公网地址");
    var protocol = request.Type is 2 or 3 ? (request.Protocol ?? "tls").Trim().ToLowerInvariant() : "tls";
    if (protocol is not ("tls" or "tcp" or "anytls" or "quic")) return Api.Error("unsupported tunnel protocol");
    if (protocol == "anytls" && string.IsNullOrWhiteSpace(request.AnyTlsPassword)) return Api.Error("anytls password is required");
    var now = Domain.Now();
    await db.ExecuteAsync("INSERT INTO tunnel (name,traffic_ratio,speed_limit_kbps,in_node_id,in_ip,out_node_id,out_ip,type,protocol,anytls_password,flow,flow_limit_gb,tcp_listen_addr,udp_listen_addr,interface_name,created_time,updated_time,status) VALUES (@name,@ratio,@speed,@in,@inip,@out,@outip,@type,@protocol,@anytlsPassword,@flowType,@flowLimitGb,@tcp,@udp,@iface,@now,@now,1)", Domain.Params(("name", request.Name?.Trim()), ("ratio", request.TrafficRatio ?? 1m), ("speed", Math.Max(0, request.SpeedLimitKbps ?? 0)), ("in", request.InNodeId), ("inip", DbValue.String(inRows[0], "ip")), ("out", outId), ("outip", DbValue.String(outRows[0], "server_ip")), ("type", request.Type), ("protocol", protocol), ("anytlsPassword", protocol == "anytls" ? request.AnyTlsPassword!.Trim() : null), ("flowType", request.FlowType), ("flowLimitGb", request.FlowLimitGb), ("tcp", request.TcpListenAddr ?? "[::]"), ("udp", request.UdpListenAddr ?? "[::]"), ("iface", request.InterfaceName), ("now", now)), ct);
    return Api.Ok(null, "tunnel created");
});

app.MapPost("/api/v1/tunnel/update", async (TunnelUpdateRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    if (request.FlowType is not (1 or 2) || request.FlowLimitGb < 0) return Api.Error("invalid tunnel settings");
    var currentRows = await db.QueryAsync("SELECT type,anytls_password FROM tunnel WHERE id=@id", Domain.Params(("id", request.Id)), ct);
    if (currentRows.Count == 0) return Api.Error("tunnel not found");
    var tunnelType = DbValue.Int(currentRows[0], "type");
    var protocol = tunnelType is 2 or 3 ? (request.Protocol ?? "tls").Trim().ToLowerInvariant() : "tls";
    if (protocol is not ("tls" or "tcp" or "anytls" or "quic")) return Api.Error("unsupported tunnel protocol");
    var anyTlsPassword = protocol == "anytls"
        ? (string.IsNullOrWhiteSpace(request.AnyTlsPassword) ? DbValue.String(currentRows[0], "anytls_password") : request.AnyTlsPassword.Trim())
        : null;
    if (protocol == "anytls" && string.IsNullOrWhiteSpace(anyTlsPassword)) return Api.Error("anytls password is required");
    var changed = await db.ExecuteAsync("UPDATE tunnel SET name=@name,flow=@flowType,flow_limit_gb=@flowLimitGb,traffic_ratio=@ratio,speed_limit_kbps=@speed,protocol=@protocol,anytls_password=@anytlsPassword,tcp_listen_addr=@tcp,udp_listen_addr=@udp,interface_name=@iface,updated_time=@now WHERE id=@id", Domain.Params(("name", request.Name?.Trim()), ("flowType", request.FlowType), ("flowLimitGb", request.FlowLimitGb), ("ratio", request.TrafficRatio ?? 1m), ("speed", Math.Max(0, request.SpeedLimitKbps ?? 0)), ("protocol", protocol), ("anytlsPassword", anyTlsPassword), ("tcp", request.TcpListenAddr ?? "[::]"), ("udp", request.UdpListenAddr ?? "[::]"), ("iface", request.InterfaceName), ("now", Domain.Now()), ("id", request.Id)), ct);
    if (changed == 0) return Api.Error("tunnel not found");
    var tunnelRows = await db.QueryAsync("SELECT t.*,n.ip in_ip,n.server_ip entry_ip,n.port_sta,n.port_end,n.id in_node_id,o.server_ip out_ip FROM tunnel t LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id WHERE t.id=@id", Domain.Params(("id", request.Id)), ct);
    if (tunnelRows.Count > 0)
    {
        var syncError = await ForwardOperations.SyncTunnelAsync(tunnelRows[0], db, gateway, ct);
        if (syncError is not null) return Api.Error($"隧道已保存，但节点同步失败：{syncError}");
        await FlowOperations.ReconcileTunnelAsync(request.Id, db, gateway, ct);
    }
    return Api.Ok(null, "tunnel updated");
});
app.MapPost("/api/v1/tunnel/get", async (IdRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT t.*,n.ip in_ip,n.port_sta,n.port_end,o.ip out_ip,tf.tunnel_in_flow,tf.tunnel_out_flow FROM tunnel t LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id LEFT JOIN (SELECT tunnel_id,SUM(in_flow) tunnel_in_flow,SUM(out_flow) tunnel_out_flow FROM `forward` GROUP BY tunnel_id) tf ON tf.tunnel_id=t.id WHERE t.id=@id AND (@admin=1 OR EXISTS (SELECT 1 FROM user_tunnel ut WHERE ut.tunnel_id=t.id AND ut.user_id=@user AND ut.status=1))", Domain.Params(("id", request.Id), ("admin", Domain.IsAdmin(user!) ? 1 : 0), ("user", user!.Id)), ct);
    return rows.Count == 0 ? Api.Error("tunnel not found") : Api.Ok(Domain.Tunnel(rows[0]));
});

app.MapPost("/api/v1/tunnel/delete", async (IdRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var used = Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM `forward` WHERE tunnel_id=@id", Domain.Params(("id", request.Id)), ct)) + Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM user_tunnel WHERE tunnel_id=@id", Domain.Params(("id", request.Id)), ct));
    if (used > 0) return Api.Error("tunnel is still in use");
    return await db.ExecuteAsync("DELETE FROM tunnel WHERE id=@id", Domain.Params(("id", request.Id)), ct) == 0 ? Api.Error("tunnel not found") : Api.Ok(null, "tunnel deleted");
});

app.MapPost("/api/v1/tunnel/user/assign", async (UserTunnelRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var exists = Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM user_tunnel WHERE user_id=@user AND tunnel_id=@tunnel", Domain.Params(("user", request.UserId), ("tunnel", request.TunnelId)), ct));
    if (exists > 0) return Api.Error("permission already exists");
    await db.ExecuteAsync("INSERT INTO user_tunnel (user_id,tunnel_id,speed_id,num,flow,in_flow,out_flow,flow_reset_time,exp_time,status) VALUES (@user,@tunnel,@speed,@num,@flow,0,0,@reset,@exp,1)", Domain.Params(("user", request.UserId), ("tunnel", request.TunnelId), ("speed", request.SpeedId), ("num", request.Num), ("flow", request.Flow), ("reset", request.FlowResetTime), ("exp", request.ExpTime)), ct);
    return Api.Ok(null, "permission assigned");
});

app.MapPost("/api/v1/tunnel/user/list", async (JsonElement request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var userId = request.TryGetProperty("userId", out var id) ? id.GetInt32() : 0;
    var rows = await db.QueryAsync("SELECT ut.*,t.name tunnel_name,t.flow tunnel_flow,sl.name speed_name,sl.speed FROM user_tunnel ut LEFT JOIN tunnel t ON t.id=ut.tunnel_id LEFT JOIN speed_limit sl ON sl.id=ut.speed_id WHERE ut.user_id=@user ORDER BY ut.id", Domain.Params(("user", userId)), ct);
    return Api.Ok(rows.Select(Domain.UserTunnel).ToList());
});

app.MapPost("/api/v1/tunnel/user/update", async (UserTunnelUpdateRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var changed = await db.ExecuteAsync("UPDATE user_tunnel SET flow=@flow,num=@num,flow_reset_time=@reset,exp_time=@exp,status=@status,speed_id=@speed WHERE id=@id", Domain.Params(("flow", request.Flow), ("num", request.Num), ("reset", request.FlowResetTime), ("exp", request.ExpTime), ("status", request.Status), ("speed", request.SpeedId), ("id", request.Id)), ct);
    return changed == 0 ? Api.Error("permission not found") : Api.Ok(null, "permission updated");
});

app.MapPost("/api/v1/tunnel/user/remove", async (IdRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var forwardCount = Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM `forward` f JOIN user_tunnel ut ON ut.user_id=f.user_id AND ut.tunnel_id=f.tunnel_id WHERE ut.id=@id", Domain.Params(("id", request.Id)), ct));
    if (forwardCount > 0) return Api.Error("permission still has forwards");
    return await db.ExecuteAsync("DELETE FROM user_tunnel WHERE id=@id", Domain.Params(("id", request.Id)), ct) == 0 ? Api.Error("permission not found") : Api.Ok(null, "permission removed");
});

app.MapPost("/api/v1/tunnel/user/tunnel", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var rows = await db.QueryAsync(Domain.IsAdmin(user!) ? "SELECT t.*,n.ip in_ip,n.port_sta,n.port_end,o.ip out_ip FROM tunnel t LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id WHERE t.status=1" : "SELECT t.*,n.ip in_ip,n.port_sta,n.port_end,o.ip out_ip FROM tunnel t JOIN user_tunnel ut ON ut.tunnel_id=t.id LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id WHERE ut.user_id=@user AND ut.status=1 AND t.status=1", Domain.IsAdmin(user!) ? null : Domain.Params(("user", user!.Id)), ct);
    return Api.Ok(rows.Select(Domain.TunnelList).ToList());
});

app.MapPost("/api/v1/forward/list", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    var sql = Domain.IsAdmin(user!) ? "SELECT f.*,t.name tunnel_name,t.type tunnel_type,t.in_ip,t.out_ip,t.flow tunnel_flow,t.flow_limit_gb tunnel_limit_gb,xi.name xui_inbound_name,COALESCE(NULLIF(n.server_ip,''),NULLIF(n.ip,''),'0.0.0.0') entry_ip FROM `forward` f LEFT JOIN tunnel t ON t.id=f.tunnel_id LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN xui_inbound xi ON xi.id=f.xui_inbound_id ORDER BY f.created_time DESC" : "SELECT f.*,t.name tunnel_name,t.type tunnel_type,t.in_ip,t.out_ip,t.flow tunnel_flow,t.flow_limit_gb tunnel_limit_gb,xi.name xui_inbound_name,COALESCE(NULLIF(n.server_ip,''),NULLIF(n.ip,''),'0.0.0.0') entry_ip FROM `forward` f LEFT JOIN tunnel t ON t.id=f.tunnel_id LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN xui_inbound xi ON xi.id=f.xui_inbound_id WHERE f.user_id=@user ORDER BY f.created_time DESC";
    var rows = await db.QueryAsync(sql, Domain.IsAdmin(user!) ? null : Domain.Params(("user", user!.Id)), ct);
    return Api.Ok(rows.Select(Domain.Forward).ToList());
});

app.MapPost("/api/v1/forward/create", async (ForwardRequest request, HttpContext context, Db db, NodeGateway gateway, XuiIntegrationService xui, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out var user, out var error)) return error!;
    var result = await ForwardOperations.CreateAsync(request, user!, db, gateway, xui, ct);
    return result;
});
app.MapPost("/api/v1/forward/update", async (ForwardUpdateRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out var user, out var error)) return error!;
    return await ForwardOperations.UpdateAsync(request, user!, db, gateway, ct);
});
app.MapPost("/api/v1/forward/delete", async (IdRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out var user, out var error)) return error!;
    return await ForwardOperations.DeleteAsync(request.Id, user!, db, gateway, false, ct);
});
app.MapPost("/api/v1/forward/force-delete", async (IdRequest request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryUser(context, app.Services, out var user, out var error)) return error!;
    if (!Domain.IsAdmin(user!)) return Api.Error("forbidden", 403);
    return await ForwardOperations.DeleteAsync(request.Id, user!, db, null, true, ct);
});
app.MapPost("/api/v1/forward/pause", async (IdRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) => await ForwardOperations.ChangeStatusAsync(request.Id, context, db, gateway, 0, "PauseService", ct));
app.MapPost("/api/v1/forward/resume", async (IdRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) => await ForwardOperations.ChangeStatusAsync(request.Id, context, db, gateway, 1, "ResumeService", ct));
app.MapPost("/api/v1/forward/update-order", async (JsonElement request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!TryAdmin(context, app.Services, out _, out var error)) return error!;
    if (!request.TryGetProperty("forwards", out var forwards)) return Api.Error("forwards is required");
    foreach (var item in forwards.EnumerateArray()) await db.ExecuteAsync("UPDATE `forward` SET inx=@inx WHERE id=@id", Domain.Params(("inx", item.GetProperty("inx").GetInt32()), ("id", item.GetProperty("id").GetInt64())), ct);
    return Api.Ok(null, "order updated");
});
app.MapPost("/api/v1/forward/diagnose", async (DiagnoseRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT f.*,t.type,t.in_node_id,t.out_node_id FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id WHERE f.id=@id", Domain.Params(("id", request.ForwardId)), ct);
    if (rows.Count == 0) return Api.Error("forward not found");
    var row = rows[0];
    var useExitNode = DbValue.Int(row, "type") is 2 or 3;
    var probeNodeId = DbValue.Long(row, useExitNode ? "out_node_id" : "in_node_id");
    var results = new List<object>();
    foreach (var target in DbValue.String(row, "remote_addr").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Domain.TrySplitAddress(target, out var host, out var port))
        {
            results.Add(new { success = false, targetIp = target, targetPort = 0, message = "invalid target address" });
            continue;
        }
        var response = await gateway.SendAsync(probeNodeId, "TcpPing", new { ip = host, port, count = 2, timeout = 3000 }, ct);
        results.Add(response.Data.HasValue ? JsonSerializer.Deserialize<JsonElement>(response.Data.Value.GetRawText()) : new { success = response.Success, message = response.Message, ip = host, port });
    }
    var probeNodeRole = DbValue.Int(row, "type") == 3 ? "windows" : useExitNode ? "exit" : "entry";
    return Api.Ok(new { forwardId = request.ForwardId, results, probeNodeId, probeNodeRole, timestamp = Domain.Now() });
});
app.MapPost("/api/v1/tunnel/diagnose", async (DiagnoseRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT t.*,n.ip in_ip,n.server_ip in_server_ip,n.version in_version,n.status in_status,o.ip out_ip,o.server_ip out_server_ip,o.version out_version,o.status out_status,o.port_sta out_port_sta,o.port_end out_port_end,o.port_range out_port_range FROM tunnel t LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id WHERE t.id=@id", Domain.Params(("id", request.TunnelId)), ct);
    if (rows.Count == 0) return Api.Error("tunnel not found");
    var row = rows[0];
    var entryNodeAddress = string.IsNullOrWhiteSpace(DbValue.String(row, "in_server_ip")) ? DbValue.String(row, "in_ip") : DbValue.String(row, "in_server_ip");
    var exitNodeAddress = string.IsNullOrWhiteSpace(DbValue.String(row, "out_server_ip")) ? DbValue.String(row, "out_ip") : DbValue.String(row, "out_server_ip");
    var entryOnline = DbValue.Int(row, "in_status") == 1;
    var exitOnline = DbValue.Int(row, "out_status") == 1;
    var results = new List<object>
    {
        new { label = "入口节点连接", target = entryNodeAddress, success = entryOnline, error = entryOnline ? (string?)null : "入口节点不在线" },
        new { label = "出口节点连接", target = exitNodeAddress, success = exitOnline, error = exitOnline ? (string?)null : "出口节点不在线" }
    };
    var overallSuccess = entryOnline && exitOnline;

    if (DbValue.Int(row, "type") == 2)
    {
        var forwardRows = await db.QueryAsync("SELECT out_port FROM `forward` WHERE tunnel_id=@id AND out_port IS NOT NULL LIMIT 1", Domain.Params(("id", request.TunnelId)), ct);
        var relayPort = forwardRows.Count > 0 ? DbValue.Int(forwardRows[0], "out_port") : 0;
        var temporaryListenerStarted = false;
        var usingActualRelayPort = relayPort > 0;

        if (!usingActualRelayPort)
        {
            if (!PortRangeRules.TryParse(DbValue.String(row, "out_port_range"), DbValue.Int(row, "out_port_sta"), DbValue.Int(row, "out_port_end"), out _, out var ranges, out var rangeError))
            {
                results.Add(new { label = "入口到出口节点链路", target = exitNodeAddress, success = false, error = rangeError ?? "出口节点未配置可用端口范围" });
                overallSuccess = false;
            }
            else
            {
                var usedPortRows = await db.QueryAsync("SELECT f.in_port AS port FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id WHERE t.in_node_id=@node UNION SELECT f.out_port AS port FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id WHERE t.out_node_id=@node AND f.out_port IS NOT NULL", Domain.Params(("node", DbValue.Long(row, "out_node_id"))), ct);
                var usedPorts = usedPortRows.Select(portRow => DbValue.Int(portRow, "port")).ToHashSet();
                foreach (var (start, end) in ranges)
                {
                    for (var port = start; port <= end; port++)
                    {
                        if (usedPorts.Contains(port)) continue;
                        relayPort = port;
                        break;
                    }
                    if (relayPort > 0) break;
                }

                if (relayPort == 0)
                {
                    results.Add(new { label = "入口到出口节点链路", target = exitNodeAddress, success = false, error = "出口节点端口范围没有可用于诊断的端口" });
                    overallSuccess = false;
                }
                else
                {
                    var listener = await gateway.SendAsync(DbValue.Long(row, "out_node_id"), "StartTcpProbeListener", new { port = relayPort }, ct);
                    temporaryListenerStarted = listener.Success;
                    if (!temporaryListenerStarted)
                    {
                        results.Add(new { label = "入口到出口节点链路", target = $"{exitNodeAddress}:{relayPort}", success = false, error = $"无法在出口节点启动诊断监听：{listener.Message}" });
                        overallSuccess = false;
                    }
                }
            }
        }

        if (relayPort > 0 && (usingActualRelayPort || temporaryListenerStarted))
        {
            NodeResponse response;
            try
            {
                response = await gateway.SendAsync(DbValue.Long(row, "in_node_id"), "TcpPing", new { ip = exitNodeAddress, port = relayPort, count = 4, timeout = 2000 }, ct);
            }
            finally
            {
                if (temporaryListenerStarted)
                    await gateway.SendAsync(DbValue.Long(row, "out_node_id"), "StopTcpProbeListener", new { port = relayPort }, CancellationToken.None);
            }
            var data = response.Data ?? JsonSerializer.SerializeToElement(new { ip = exitNodeAddress, port = relayPort, success = false, errorMessage = response.Message });
            var success = response.Success;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("success", out var status) && (status.ValueKind == JsonValueKind.True || status.ValueKind == JsonValueKind.False)) success = status.GetBoolean();
            var errorMessage = response.Message;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("errorMessage", out var message) && message.ValueKind == JsonValueKind.String) errorMessage = message.GetString() ?? errorMessage;
            results.Add(new { label = "入口到出口节点链路", target = $"{exitNodeAddress}:{relayPort}", success, data, probeMode = usingActualRelayPort ? "relay-port" : "temporary-listener", error = success ? (string?)null : errorMessage });
            overallSuccess &= success;
        }
    }
    else results.Add(new { label = "入口到出口节点链路", target = "直连隧道", success = true, skipped = true, error = "直连隧道无需中继检测" });

    var cloudflareResponse = await gateway.SendAsync(DbValue.Long(row, "out_node_id"), "TcpPing", new { ip = "www.cloudflare.com", port = 443, count = 2, timeout = 5000 }, ct);
    var cloudflareData = cloudflareResponse.Data ?? JsonSerializer.SerializeToElement(new { ip = "www.cloudflare.com", port = 443, success = false, errorMessage = cloudflareResponse.Message });
    var cloudflareSuccess = cloudflareResponse.Success;
    if (cloudflareData.ValueKind == JsonValueKind.Object && cloudflareData.TryGetProperty("success", out var cloudflareStatus) && (cloudflareStatus.ValueKind == JsonValueKind.True || cloudflareStatus.ValueKind == JsonValueKind.False)) cloudflareSuccess = cloudflareStatus.GetBoolean();
    var cloudflareError = cloudflareResponse.Message;
    if (cloudflareData.ValueKind == JsonValueKind.Object && cloudflareData.TryGetProperty("errorMessage", out var cloudflareMessage) && cloudflareMessage.ValueKind == JsonValueKind.String) cloudflareError = cloudflareMessage.GetString() ?? cloudflareError;
    results.Add(new { label = "出口节点到 Cloudflare", target = "www.cloudflare.com:443", success = cloudflareSuccess, data = cloudflareData, error = cloudflareSuccess ? (string?)null : cloudflareError });
    overallSuccess &= cloudflareSuccess;
    return Api.Ok(new { tunnelId = request.TunnelId, success = overallSuccess, entryNodeAddress, exitNodeAddress, entryNode = new { id = DbValue.Long(row, "in_node_id"), address = entryNodeAddress, online = entryOnline }, exitNode = new { id = DbValue.Long(row, "out_node_id"), address = exitNodeAddress, online = exitOnline }, results, timestamp = Domain.Now() });

});

app.MapPost("/api/v1/speed-limit/list", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT * FROM speed_limit ORDER BY id DESC", cancellationToken: ct);
    return Api.Ok(rows.Select(Domain.SpeedLimit).ToList());
});
app.MapPost("/api/v1/speed-limit/create", async (SpeedLimitRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) => await SpeedLimitOperations.SaveAsync(request, null, context, db, gateway, ct));
app.MapPost("/api/v1/speed-limit/update", async (SpeedLimitUpdateRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) => await SpeedLimitOperations.SaveAsync(request, request.Id, context, db, gateway, ct));
app.MapPost("/api/v1/speed-limit/delete", async (IdRequest request, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct) => await SpeedLimitOperations.DeleteAsync(request.Id, context, db, gateway, ct));
app.MapPost("/api/v1/speed-limit/tunnels", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT * FROM tunnel ORDER BY id DESC", cancellationToken: ct);
    return Api.Ok(rows.Select(Domain.TunnelList).ToList());
});

app.MapPost("/api/v1/config/list", async (HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var rows = await db.QueryAsync("SELECT name,value FROM vite_config", cancellationToken: ct);
    return Api.Ok(rows
        .Where(row => !string.Equals(DbValue.String(row, "name"), "telegram_bot_token", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(row => DbValue.String(row, "name"), row => DbValue.String(row, "value")));
});
app.MapPost("/api/v1/config/get", async (JsonElement request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var name = request.TryGetProperty("name", out var item) ? item.GetString() : null;
    var rows = await db.QueryAsync("SELECT * FROM vite_config WHERE name=@name", Domain.Params(("name", name)), ct);
    return rows.Count == 0 ? Api.Error("config not found") : Api.Ok(new { name, value = DbValue.String(rows[0], "value") });
});
app.MapPost("/api/v1/config/update", async (JsonElement request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    foreach (var property in request.EnumerateObject())
    {
        if (string.Equals(property.Name, "telegram_bot_token", StringComparison.OrdinalIgnoreCase)) return Api.Error("请使用 Telegram 通知设置保存 Bot Token");
        await UpsertConfig(db, property.Name, property.Value.ToString(), ct);
    }
    return Api.Ok(null, "config updated");
});
app.MapPost("/api/v1/config/update-single", async (JsonElement request, HttpContext context, Db db, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    if (!request.TryGetProperty("name", out var name) || !request.TryGetProperty("value", out var value)) return Api.Error("name and value are required");
    if (string.Equals(name.GetString(), "telegram_bot_token", StringComparison.OrdinalIgnoreCase)) return Api.Error("请使用 Telegram 通知设置保存 Bot Token");
    await UpsertConfig(db, name.GetString()!, value.ToString(), ct);
    return Api.Ok(null, "config updated");
});

app.MapPost("/api/v1/notification/telegram/status", async (HttpContext context, TelegramNotifier notifier, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    return Api.Ok(await notifier.GetPublicSettingsAsync(ct));
});
app.MapPost("/api/v1/notification/telegram/save", async (TelegramSettingsRequest request, HttpContext context, TelegramNotifier notifier, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var result = await notifier.SaveAsync(request, ct);
    return result.IsSuccess ? Api.Ok(null, result.Message) : Api.Error(result.Message);
});
app.MapPost("/api/v1/notification/telegram/test", async (HttpContext context, TelegramNotifier notifier, CancellationToken ct) =>
{
    if (!RequireAdmin(context, app.Services, out var error)) return error!;
    var result = await notifier.SendTestAsync(ct);
    return result.IsSuccess ? Api.Ok(null, result.Message) : Api.Error(result.Message);
});

app.MapPost("/flow/upload", async (HttpContext context, Db db, CancellationToken ct) =>
{
    var secret = NodeAuth.ReadSecret(context, app.Configuration);
    if (string.IsNullOrWhiteSpace(secret)) return Results.StatusCode(StatusCodes.Status401Unauthorized);
    var node = await db.QueryAsync("SELECT id FROM node WHERE secret=@secret", Domain.Params(("secret", secret)), ct);
    if (node.Count == 0) return Results.Text("ok");
    var raw = await new StreamReader(context.Request.Body).ReadToEndAsync(ct);
    try
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("encrypted", out var encrypted) && encrypted.GetBoolean()) raw = new AesCrypto(secret).Decrypt(root.GetProperty("data").GetString()!);
        var report = JsonSerializer.Deserialize<FlowReport>(raw);
        if (report is not null && report.N != "web_api") await FlowOperations.ApplyAsync(report, DbValue.Long(node[0], "id"), db, app.Services.GetRequiredService<NodeGateway>(), app.Services.GetRequiredService<TelegramNotifier>(), ct);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to process flow upload for node {NodeId}", DbValue.Long(node[0], "id"));
    }
    return Results.Text("ok");
});
app.MapPost("/flow/config", async (HttpContext context, Db db, CancellationToken ct) =>
{
    var secret = NodeAuth.ReadSecret(context, app.Configuration);
    if (string.IsNullOrWhiteSpace(secret)) return Results.StatusCode(StatusCodes.Status401Unauthorized);
    var node = await db.QueryAsync("SELECT id FROM node WHERE secret=@secret", Domain.Params(("secret", secret)), ct);
    if (node.Count == 0) return Results.Text("ok");
    return Results.Text("ok");
});
app.MapPost("/api/v1/captcha/check", () => Api.Error("captcha is disabled; login rate limiting is enabled", 501));
app.MapPost("/api/v1/captcha/generate", () => Api.Error("captcha is disabled; login rate limiting is enabled", 501));
app.MapPost("/api/v1/captcha/verify", () => Api.Error("captcha is disabled; login rate limiting is enabled", 501));

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(builder.Configuration);
return app;

static bool TryUser(HttpContext context, IServiceProvider services, out AuthUser? user, out IResult? error)
{
    user = null;
    error = null;
    var tokens = services.GetRequiredService<TokenService>();
    if (!tokens.TryValidate(context.Request.Headers.Authorization.ToString(), out user)) { error = Api.Error("unauthorized", 401); return false; }
    return true;
}

static bool RequireAdmin(HttpContext context, IServiceProvider services, out IResult? error)
{
    error = null;
    if (!TryUser(context, services, out var user, out error)) return false;
    if (!Domain.IsAdmin(user!)) { error = Api.Error("forbidden", 403); return false; }
    return true;
}

static bool TryAdmin(HttpContext context, IServiceProvider services, out AuthUser? user, out IResult? error)
{
    user = null;
    error = null;
    if (!TryUser(context, services, out user, out error)) return false;
    if (!Domain.IsAdmin(user!)) { error = Api.Error("forbidden", 403); return false; }
    return true;
}

static async Task UpsertConfig(Db db, string name, string value, CancellationToken ct)
{
    await db.ExecuteAsync("INSERT INTO vite_config (name,value,time) VALUES (@name,@value,@time) ON DUPLICATE KEY UPDATE value=@value,time=@time", Domain.Params(("name", name), ("value", value), ("time", Domain.Now())), ct);
}

static string? BuildPanelAddress(HttpContext context, string? configuredHost, string? configuredPort, string? configuredSecurePort, string? configuredSecure, bool requireHttps)
{
    var host = string.IsNullOrWhiteSpace(configuredHost) ? context.Request.Host.Host : configuredHost.Trim();
    var secure = requireHttps || configuredSecure is "1" or "true" or "on";
    var portText = secure ? configuredSecurePort : configuredPort;
    var port = string.IsNullOrWhiteSpace(portText) ? (secure ? 443 : 6315) : int.TryParse(portText, out var parsedPort) ? parsedPort : 0;
    if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535 || host.Any(char.IsWhiteSpace) || host.Any("'\"`;$&|<>/()".Contains)) return null;
    var address = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";
    return $"{(secure ? "wss" : "ws")}://{address}";
}

static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'")}'";
static string PowerShellQuote(string value) => $"'{value.Replace("'", "''")}'";
    }
}

// ForwardOperations lives in Application/ForwardOperations.cs.
