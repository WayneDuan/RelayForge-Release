using System.Net;
using System.Text;
using System.Text.Json;

namespace RelayForge.Panel.Api;

public sealed record XuiInboundSnapshot(
    string ExternalId,
    string Name,
    string Tag,
    string Protocol,
    int Port,
    string Listen,
    bool Enabled);

public sealed class XuiClient(IConfiguration configuration)
{
    public async Task<IReadOnlyList<XuiInboundSnapshot>> ListInboundsAsync(XuiClientSettings settings, CancellationToken ct)
    {
        using var client = CreateClient(settings.VerifyTls);
        var panelUri = NormalizePanelUrl(settings.PanelUrl);
        var allowPrivateNetworks = configuration.GetValue("XUI_ALLOW_PRIVATE_NETWORKS", configuration.GetValue("Xui:AllowPrivateNetworks", false));
        await EndpointPolicy.ValidateAsync(panelUri, allowPrivateNetworks, ct);

        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiToken);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
                throw new InvalidOperationException("3x-ui API Token 或账号密码至少需要配置一项");

            var csrf = await GetJsonAsync(client, new Uri(panelUri, "csrf-token"), null, ct);
            var csrfToken = ReadString(csrf, "obj");
            if (string.IsNullOrWhiteSpace(csrfToken)) throw new InvalidOperationException("3x-ui 未返回 CSRF Token");

            using var login = new HttpRequestMessage(HttpMethod.Post, new Uri(panelUri, "login"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    username = settings.Username,
                    password = settings.Password,
                    twoFactorCode = settings.TwoFactorCode
                }), Encoding.UTF8, "application/json")
            };
            login.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);
            using var loginResponse = await client.SendAsync(login, ct);
            var loginBody = await ReadBodyAsync(loginResponse, ct);
            EnsureApiSuccess(loginResponse.StatusCode, loginBody, "3x-ui 登录失败");
        }

        JsonElement root;
        try
        {
            root = await GetJsonAsync(client, new Uri(panelUri, "panel/api/inbounds/options"), null, ct);
        }
        catch (XuiApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            root = await GetJsonAsync(client, new Uri(panelUri, "panel/api/inbounds/list"), null, ct);
        }

        var items = GetArray(root);
        var result = new List<XuiInboundSnapshot>();
        foreach (var item in items.EnumerateArray())
        {
            var id = ReadString(item, "id");
            var port = ReadInt(item, "port");
            if (string.IsNullOrWhiteSpace(id) || port is < 1 or > 65535) continue;
            var tag = ReadString(item, "tag");
            var name = FirstNonEmpty(ReadString(item, "remark"), tag, $"入站 {id}");
            result.Add(new XuiInboundSnapshot(
                id,
                name,
                tag,
                ReadString(item, "protocol", "unknown"),
                port,
                ReadString(item, "listen"),
                ReadBool(item, "enable", true)));
        }

        return result;
    }

    private static HttpClient CreateClient(bool verifyTls)
    {
        var handler = new HttpClientHandler();
        handler.AllowAutoRedirect = false;
        if (!verifyTls) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, Uri uri, string? cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request, ct);
        var body = await ReadBodyAsync(response, ct);
        EnsureApiSuccess(response.StatusCode, body, "读取 3x-ui 入站失败");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct) => await response.Content.ReadAsStringAsync(ct);

    private static void EnsureApiSuccess(HttpStatusCode statusCode, string body, string fallback)
    {
        if ((int)statusCode is < 200 or >= 300)
        {
            var message = TryReadMessage(body);
            throw new XuiApiException(statusCode, message ?? $"{fallback}（HTTP {(int)statusCode}）");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new XuiApiException(statusCode, TryReadMessage(body) ?? fallback);
        }
        catch (JsonException)
        {
            throw new XuiApiException(statusCode, fallback);
        }
    }

    private static string? TryReadMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return ReadString(root, "msg") is { Length: > 0 } msg ? msg : ReadString(root, "message");
        }
        catch (JsonException) { return null; }
    }

    private static JsonElement GetArray(JsonElement root)
    {
        if (root.TryGetProperty("obj", out var obj) && obj.ValueKind == JsonValueKind.Array) return obj;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) return data;
        using var empty = JsonDocument.Parse("[]");
        return empty.RootElement.Clone();
    }

    private static string ReadString(JsonElement item, string property, string fallback = "")
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
    }

    private static int ReadInt(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static bool ReadBool(JsonElement item, string property, bool fallback)
    {
        if (!item.TryGetProperty(property, out var value)) return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return value.ToString() switch { "1" or "true" or "True" => true, "0" or "false" or "False" => false, _ => fallback };
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static Uri NormalizePanelUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrWhiteSpace(uri.UserInfo) || !string.IsNullOrWhiteSpace(uri.Fragment))
            throw new InvalidOperationException("3x-ui 面板地址必须是有效的 http(s) 地址");
        return uri;
    }

    private static class EndpointPolicy
    {
        public static async Task ValidateAsync(Uri uri, bool allowPrivateNetworks, CancellationToken ct)
        {
            if (allowPrivateNetworks) return;
            if (IsBlockedHost(uri.Host)) throw new InvalidOperationException("出于安全原因，默认禁止访问本机、内网和云元数据地址；如确需使用，请显式开启 XUI_ALLOW_PRIVATE_NETWORKS");

            IPAddress[] addresses;
            if (IPAddress.TryParse(uri.DnsSafeHost, out var address)) addresses = [address];
            else
            {
                try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct); }
                catch (Exception ex) { throw new InvalidOperationException("无法解析 3x-ui 面板地址", ex); }
            }

            if (addresses.Any(IsPrivateAddress))
                throw new InvalidOperationException("出于安全原因，默认禁止访问解析到本机、内网和云元数据地址；如确需使用，请显式开启 XUI_ALLOW_PRIVATE_NETWORKS");
        }

        private static bool IsBlockedHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

        private static bool IsPrivateAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var first = bytes[0];
                var second = bytes[1];
                return first == 0 || first == 10 || first == 127
                    || (first == 169 && second == 254)
                    || (first == 172 && second is >= 16 and <= 31)
                    || (first == 192 && second == 168)
                    || (first == 100 && second is >= 64 and <= 127);
            }
            return bytes.Length > 0 && (bytes[0] & 0xfe) == 0xfc;
        }
    }

    private sealed class XuiApiException(HttpStatusCode statusCode, string message) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}

public sealed record XuiClientSettings(
    string PanelUrl,
    string ConnectHost,
    string ApiToken,
    string Username,
    string Password,
    string TwoFactorCode,
    bool VerifyTls);

public sealed class XuiIntegrationService(Db db, XuiClient client, AesCrypto crypto)
{
    public async Task<IResult> CreateAsync(XuiConnectionRequest request, AuthUser user, CancellationToken ct)
    {
        if (!TryBuildSettings(request, out var settings, out var error)) return Api.Error(error!);
        if (string.IsNullOrWhiteSpace(request.Name)) return Api.Error("3x-ui 集成名称不能为空");

        IReadOnlyList<XuiInboundSnapshot> inbounds;
        try { inbounds = await client.ListInboundsAsync(settings, ct); }
        catch (Exception ex) { return Api.Error(ex.Message); }

        var now = Domain.Now();
        var id = await db.InsertAndGetIdAsync("""
            INSERT INTO xui_connection (user_id,name,panel_url,connect_host,api_token_cipher,username_cipher,password_cipher,two_factor_code_cipher,verify_tls,status,last_sync_time,last_error,created_time,updated_time)
            VALUES (@user,@name,@panel,@host,@token,@username,@password,@twoFactor,@verify,1,@sync,NULL,@now,@now)
            """, Domain.Params(
                ("user", user.Id), ("name", request.Name.Trim()), ("panel", settings.PanelUrl.TrimEnd('/')),
                ("host", settings.ConnectHost), ("token", Encrypt(settings.ApiToken)), ("username", Encrypt(settings.Username)),
                ("password", Encrypt(settings.Password)), ("twoFactor", Encrypt(settings.TwoFactorCode)),
                ("verify", settings.VerifyTls ? 1 : 0), ("sync", now), ("now", now)), ct);
        await SaveInboundsAsync(id, settings.ConnectHost, inbounds, now, ct);
        return Api.Ok(new { id, inboundCount = inbounds.Count }, "3x-ui 已接入并同步");
    }

    public async Task<IResult> ListConnectionsAsync(AuthUser user, CancellationToken ct)
    {
        var rows = await db.QueryAsync(Domain.IsAdmin(user)
            ? "SELECT c.*,(SELECT COUNT(*) FROM xui_inbound i WHERE i.connection_id=c.id) inbound_count FROM xui_connection c ORDER BY c.id DESC"
            : "SELECT c.*,(SELECT COUNT(*) FROM xui_inbound i WHERE i.connection_id=c.id) inbound_count FROM xui_connection c WHERE c.user_id=@user ORDER BY c.id DESC",
            Domain.IsAdmin(user) ? null : Domain.Params(("user", user.Id)), ct);
        return Api.Ok(rows.Select(Domain.XuiConnection).ToList());
    }

    public async Task<IResult> ListInboundsAsync(AuthUser user, CancellationToken ct)
    {
        var rows = await db.QueryAsync(Domain.IsAdmin(user)
            ? "SELECT i.*,c.name connection_name FROM xui_inbound i JOIN xui_connection c ON c.id=i.connection_id ORDER BY c.id DESC,i.name"
            : "SELECT i.*,c.name connection_name FROM xui_inbound i JOIN xui_connection c ON c.id=i.connection_id WHERE c.user_id=@user ORDER BY c.id DESC,i.name",
            Domain.IsAdmin(user) ? null : Domain.Params(("user", user.Id)), ct);
        return Api.Ok(rows.Select(Domain.XuiInbound).ToList());
    }

    public async Task<IResult> SyncAsync(long id, AuthUser user, CancellationToken ct)
    {
        var rows = await GetConnectionAsync(id, user, ct);
        if (rows.Count == 0) return Api.Error("3x-ui 集成不存在");
        var settings = SettingsFromRow(rows[0]);
        try
        {
            var inbounds = await client.ListInboundsAsync(settings, ct);
            var now = Domain.Now();
            await SaveInboundsAsync(id, settings.ConnectHost, inbounds, now, ct);
            await db.ExecuteAsync("UPDATE xui_connection SET status=1,last_sync_time=@now,last_error=NULL,updated_time=@now WHERE id=@id", Domain.Params(("now", now), ("id", id)), ct);
            return Api.Ok(new { id, inboundCount = inbounds.Count }, "3x-ui 入站已同步");
        }
        catch (Exception ex)
        {
            await db.ExecuteAsync("UPDATE xui_connection SET status=0,last_error=@error,updated_time=@now WHERE id=@id", Domain.Params(("error", ex.Message[..Math.Min(500, ex.Message.Length)]), ("now", Domain.Now()), ("id", id)), CancellationToken.None);
            return Api.Error(ex.Message);
        }
    }

    public async Task<IResult> DeleteAsync(long id, AuthUser user, CancellationToken ct)
    {
        var rows = await GetConnectionAsync(id, user, ct);
        if (rows.Count == 0) return Api.Error("3x-ui 集成不存在");
        var used = Convert.ToInt64(await db.ScalarAsync("SELECT COUNT(*) FROM `forward` WHERE xui_inbound_id IN (SELECT id FROM xui_inbound WHERE connection_id=@id)", Domain.Params(("id", id)), ct));
        if (used > 0) return Api.Error($"该集成仍被 {used} 条转发使用，请先删除或迁移转发");
        await db.ExecuteAsync("DELETE FROM xui_inbound WHERE connection_id=@id", Domain.Params(("id", id)), ct);
        return await db.ExecuteAsync("DELETE FROM xui_connection WHERE id=@id", Domain.Params(("id", id)), ct) == 0 ? Api.Error("3x-ui 集成不存在") : Api.Ok(null, "3x-ui 集成已删除");
    }

    public async Task<IReadOnlyDictionary<string, object?>?> FindInboundAsync(long id, AuthUser user, CancellationToken ct)
    {
        var rows = await db.QueryAsync(Domain.IsAdmin(user)
            ? "SELECT i.*,c.user_id connection_user_id,c.status connection_status FROM xui_inbound i JOIN xui_connection c ON c.id=i.connection_id WHERE i.id=@id"
            : "SELECT i.*,c.user_id connection_user_id,c.status connection_status FROM xui_inbound i JOIN xui_connection c ON c.id=i.connection_id WHERE i.id=@id AND c.user_id=@user",
            Domain.IsAdmin(user) ? Domain.Params(("id", id)) : Domain.Params(("id", id), ("user", user.Id)), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<List<Dictionary<string, object?>>> GetConnectionAsync(long id, AuthUser user, CancellationToken ct) => await db.QueryAsync(
        Domain.IsAdmin(user) ? "SELECT * FROM xui_connection WHERE id=@id" : "SELECT * FROM xui_connection WHERE id=@id AND user_id=@user",
        Domain.IsAdmin(user) ? Domain.Params(("id", id)) : Domain.Params(("id", id), ("user", user.Id)), ct);

    private async Task SaveInboundsAsync(long connectionId, string connectHost, IReadOnlyList<XuiInboundSnapshot> inbounds, long now, CancellationToken ct)
    {
        await db.ExecuteAsync("UPDATE xui_inbound SET enabled=0,last_seen_time=0,updated_time=@now WHERE connection_id=@connection", Domain.Params(("connection", connectionId), ("now", now)), ct);
        foreach (var inbound in inbounds)
        {
            var remote = BuildRemoteAddress(connectHost, inbound.Port);
            await db.ExecuteAsync("""
                INSERT INTO xui_inbound (connection_id,external_id,name,tag,protocol,port,listen,remote_addr,enabled,last_seen_time,updated_time)
                VALUES (@connection,@external,@name,@tag,@protocol,@port,@listen,@remote,@enabled,@now,@now)
                ON DUPLICATE KEY UPDATE name=@name,tag=@tag,protocol=@protocol,port=@port,listen=@listen,remote_addr=@remote,enabled=@enabled,last_seen_time=@now,updated_time=@now
                """, Domain.Params(
                    ("connection", connectionId), ("external", inbound.ExternalId), ("name", inbound.Name), ("tag", inbound.Tag),
                    ("protocol", inbound.Protocol), ("port", inbound.Port), ("listen", inbound.Listen), ("remote", remote),
                    ("enabled", inbound.Enabled ? 1 : 0), ("now", now)), ct);
        }
    }

    private XuiClientSettings SettingsFromRow(IReadOnlyDictionary<string, object?> row) => new(
        DbValue.String(row, "panel_url"),
        DbValue.String(row, "connect_host"),
        Decrypt(DbValue.String(row, "api_token_cipher")),
        Decrypt(DbValue.String(row, "username_cipher")),
        Decrypt(DbValue.String(row, "password_cipher")),
        Decrypt(DbValue.String(row, "two_factor_code_cipher")),
        DbValue.Int(row, "verify_tls") != 0);

    private bool TryBuildSettings(XuiConnectionRequest request, out XuiClientSettings settings, out string? error)
    {
        settings = default!;
        error = null;
        if (string.IsNullOrWhiteSpace(request.PanelUrl) || !Uri.TryCreate(request.PanelUrl.Trim(), UriKind.Absolute, out var panelUri) || panelUri.Scheme is not ("http" or "https"))
        { error = "3x-ui 面板地址必须是有效的 http(s) 地址"; return false; }
        var host = string.IsNullOrWhiteSpace(request.ConnectHost) ? panelUri.Host : request.ConnectHost.Trim();
        if (!IsSafeHost(host)) { error = "3x-ui 连接地址无效，请填写域名、IPv4 或 IPv6 地址"; return false; }
        if (string.IsNullOrWhiteSpace(request.ApiToken) && (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password)))
        { error = "请填写 3x-ui API Token，或填写账号密码"; return false; }
        settings = new XuiClientSettings(request.PanelUrl.Trim().TrimEnd('/') + "/", host, request.ApiToken?.Trim() ?? "", request.Username?.Trim() ?? "", request.Password ?? "", request.TwoFactorCode?.Trim() ?? "", request.VerifyTls);
        return true;
    }

    private static bool IsSafeHost(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && !value.Any(char.IsWhiteSpace) && !value.Contains('/') && !value.Contains('\\');

    private static string BuildRemoteAddress(string host, int port)
    {
        var normalized = host.Trim();
        if (normalized.StartsWith('[') && normalized.EndsWith(']')) normalized = normalized[1..^1];
        return normalized.Contains(':') ? $"[{normalized}]:{port}" : $"{normalized}:{port}";
    }

    private string Encrypt(string value) => string.IsNullOrEmpty(value) ? "" : crypto.Encrypt(value);
    private string Decrypt(string value) => string.IsNullOrEmpty(value) ? "" : crypto.Decrypt(value);
}
