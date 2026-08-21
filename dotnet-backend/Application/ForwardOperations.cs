namespace RelayForge.Panel.Api;

public static class ForwardOperations
{
    public static async Task<IResult> CreateAsync(ForwardRequest request, AuthUser user, Db db, NodeGateway gateway, XuiIntegrationService xui, CancellationToken ct)
    {
        var remoteAddr = request.RemoteAddr?.Trim() ?? "";
        var name = request.Name?.Trim() ?? "";
        if (request.XuiInboundId is > 0)
        {
            var inbound = await xui.FindInboundAsync(request.XuiInboundId.Value, user, ct);
            if (inbound is null) return Api.Error("3x-ui 入站不存在或无权使用");
            if (DbValue.Int(inbound, "enabled") == 0) return Api.Error("3x-ui 入站已停用");
            remoteAddr = DbValue.String(inbound, "remote_addr");
            if (string.IsNullOrWhiteSpace(name)) name = DbValue.String(inbound, "name");
        }
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(remoteAddr)) return Api.Error("name and remote address are required");
        var tunnel = await db.QueryAsync("SELECT t.*,n.ip in_ip,n.server_ip entry_ip,n.port_range,n.port_sta,n.port_end,n.id in_node_id,o.server_ip out_ip,o.port_range out_port_range,o.port_sta out_port_sta,o.port_end out_port_end FROM tunnel t LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id WHERE t.id=@id", Domain.Params(("id", request.TunnelId)), ct);
        if (tunnel.Count == 0) return Api.Error("tunnel not found");
        var t = tunnel[0];
        if (DbValue.Int(t, "status") != 1) return Api.Error("tunnel disabled");
        var tunnelLimit = FlowOperations.ToBytes(DbValue.Long(t, "flow_limit_gb"));
        if (tunnelLimit > 0 && await FlowOperations.TunnelUsageAsync(request.TunnelId, db, ct) >= tunnelLimit) return Api.Error("隧道流量额度已用尽");
        var owner = user.Id;
        if (Domain.IsAdmin(user) && request.UserId is > 0) owner = request.UserId.Value;
        var relation = await db.QueryAsync("SELECT * FROM user_tunnel WHERE user_id=@user AND tunnel_id=@tunnel AND status=1 LIMIT 1", Domain.Params(("user", owner), ("tunnel", request.TunnelId)), ct);
        if (!Domain.IsAdmin(user) && relation.Count == 0) return Api.Error("tunnel permission missing");
        if (request.InPort is null)
        {
            var sameConfig = await db.QueryAsync("SELECT id FROM `forward` WHERE user_id=@user AND tunnel_id=@tunnel AND name=@name AND remote_addr=@remote LIMIT 1", Domain.Params(("user", owner), ("tunnel", request.TunnelId), ("name", name), ("remote", remoteAddr)), ct);
            if (sameConfig.Count > 0)
                return Api.Ok(new { id = DbValue.Long(sameConfig[0], "id"), existing = true }, "forward already exists");
        }
        var inNodeId = DbValue.Long(t, "in_node_id");
        var outNodeId = DbValue.Long(t, "out_node_id");
        var tunnelType = DbValue.Int(t, "type");
        if (request.InPort is < 1 or > 65535) return Api.Error("入口端口必须在 1 到 65535 之间");
        if (request.OutPort is < 1 or > 65535) return Api.Error("中继内部端口必须在 1 到 65535 之间");
        var localServiceNodeId = inNodeId;
        var port = request.InPort ?? await NextPort(db, localServiceNodeId, DbValue.String(t, "port_range"), DbValue.Int(t, "port_sta"), DbValue.Int(t, "port_end"), null, ct);
        if (port <= 0) return Api.Error(tunnelType == 3 ? "公网入口节点未配置可自动分配的端口范围，请填写公网映射端口" : "入口节点未配置可自动分配的端口范围，请填写入口端口");

        if (await IsPortInUse(db, localServiceNodeId, port, ct))
            return Api.Error(tunnelType == 3 ? "公网映射端口已被该公网入口节点上的其他转发占用" : "入口端口已被该入口节点上的其他隧道占用");

        int? outPort = tunnelType switch
        {
            2 => request.OutPort ?? (request.InPort ?? await NextPort(db, outNodeId, DbValue.String(t, "out_port_range"), DbValue.Int(t, "out_port_sta"), DbValue.Int(t, "out_port_end"), inNodeId == outNodeId ? port : null, ct)),
            3 => request.OutPort ?? await NextPort(db, inNodeId, DbValue.String(t, "port_range"), DbValue.Int(t, "port_sta"), DbValue.Int(t, "port_end"), port, ct),
            _ => null
        };
        if (outPort is 0) return Api.Error("中继节点未配置可自动分配的端口范围，请填写中继内部端口");
        var relayNodeId = tunnelType == 2 ? outNodeId : inNodeId;
        if (outPort == port && relayNodeId == inNodeId) return Api.Error("入口和中继位于同一节点时，中继内部端口必须与入口端口不同");
        if (outPort is > 0 && await IsPortInUse(db, relayNodeId, outPort.Value, ct)) return Api.Error("中继内部端口已被该节点上的其他转发占用");
        var now = Domain.Now();
        var relaySecret = tunnelType == 3 ? Domain.NewSecret() : null;
        if (request.Flow < 0) return Api.Error("流量上限不能为负数");
        var id = await db.InsertAndGetIdAsync("INSERT INTO `forward` (user_id,user_name,name,tunnel_id,xui_inbound_id,in_port,out_port,remote_addr,strategy,interface_name,relay_secret,flow,in_flow,out_flow,created_time,updated_time,status,inx) VALUES (@user,@username,@name,@tunnel,@xui,@in,@out,@remote,@strategy,@iface,@relaySecret,@flow,0,0,@now,@now,1,0)", Domain.Params(("user", owner), ("username", user.Name), ("name", name), ("tunnel", request.TunnelId), ("xui", request.XuiInboundId is > 0 ? request.XuiInboundId : null), ("in", port), ("out", outPort), ("remote", remoteAddr), ("strategy", string.IsNullOrWhiteSpace(request.Strategy) ? "fifo" : request.Strategy), ("iface", request.InterfaceName), ("relaySecret", relaySecret), ("flow", request.Flow), ("now", now)), ct);
        if (id <= 0) return Api.Error("转发记录创建失败");
        var relationId = relation.Count > 0 ? DbValue.Int(relation[0], "id") : 0;
        var serviceName = $"{id}_{owner}_{relationId}";
        var limiter = await EnsureTunnelLimiterAsync(t, gateway, ct);
        if (limiter.Error is not null) { await db.ExecuteAsync("DELETE FROM `forward` WHERE id=@id", Domain.Params(("id", id)), CancellationToken.None); return Api.Error(limiter.Error); }
        var command = tunnelType == 3
            ? new[] { GostProtocol.ReverseRelayService(serviceName, outPort!.Value, DbValue.String(t, "protocol"), limiter.Name, request.InterfaceName, DbValue.String(t, "anytls_password"), RelayUsername(serviceName), relaySecret!) }
            : BuildServices(serviceName, t, remoteAddr, port, limiter.Name, request.Strategy, request.InterfaceName);
        var response = await gateway.SendAsync(inNodeId, "AddService", command, ct);
        if (!response.Success) { await RollbackNodeConfigAsync(t, serviceName, gateway, CancellationToken.None); await db.ExecuteAsync("DELETE FROM `forward` WHERE id=@id", Domain.Params(("id", id)), CancellationToken.None); return Api.Error(response.Message); }
        if (tunnelType == 2)
        {
            var tunnelProtocol = DbValue.String(t, "protocol");
            var anyTlsPassword = DbValue.String(t, "anytls_password");
            var chain = GostProtocol.Chain(serviceName, $"{DbValue.String(t, "out_ip")}:{outPort}", tunnelProtocol, request.InterfaceName, anyTlsPassword);
            var chainResponse = await gateway.SendAsync(DbValue.Long(t, "in_node_id"), "AddChains", chain, ct);
            var remote = GostProtocol.RemoteService(serviceName, outPort!.Value, remoteAddr, tunnelProtocol, request.Strategy ?? "fifo", limiter.Name, request.InterfaceName, anyTlsPassword);
            var remoteResponse = await gateway.SendAsync(DbValue.Long(t, "out_node_id"), "AddService", new[] { remote }, ct);
            if (!chainResponse.Success || !remoteResponse.Success)
            {
                await RollbackNodeConfigAsync(t, serviceName, gateway, CancellationToken.None);
                await db.ExecuteAsync("DELETE FROM `forward` WHERE id=@id", Domain.Params(("id", id)), CancellationToken.None);
                return Api.Error("forward creation failed and was rolled back");
            }
        }
        else if (tunnelType == 3)
        {
            var reverse = GostProtocol.ReverseServices(serviceName, port, remoteAddr, request.Strategy ?? "fifo", DbValue.String(t, "protocol"), limiter.Name, request.InterfaceName);
            var reverseResponse = await gateway.SendAsync(outNodeId, "AddService", reverse, ct);
            var chain = GostProtocol.Chain(serviceName, $"{DbValue.String(t, "entry_ip")}:{outPort}", DbValue.String(t, "protocol"), request.InterfaceName, DbValue.String(t, "anytls_password"), RelayUsername(serviceName), relaySecret!);
            var chainResponse = await gateway.SendAsync(outNodeId, "AddChains", chain, ct);
            if (!reverseResponse.Success || !chainResponse.Success)
            {
                await RollbackNodeConfigAsync(t, serviceName, gateway, CancellationToken.None);
                await db.ExecuteAsync("DELETE FROM `forward` WHERE id=@id", Domain.Params(("id", id)), CancellationToken.None);
                return Api.Error("reverse forward creation failed and was rolled back");
            }
        }
        return Api.Ok(new { id, existing = false }, "forward created");
    }

    public static async Task<IResult> UpdateAsync(ForwardUpdateRequest request, AuthUser user, Db db, NodeGateway gateway, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT f.*,t.*,n.ip in_ip,n.server_ip entry_ip,n.port_sta,n.port_end,o.server_ip out_ip FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id LEFT JOIN node n ON n.id=t.in_node_id LEFT JOIN node o ON o.id=t.out_node_id WHERE f.id=@id", Domain.Params(("id", request.Id)), ct);
        if (rows.Count == 0 || !Domain.IsAdmin(user) && DbValue.Long(rows[0], "user_id") != user.Id) return Api.Error("forward not found");
        if (request.Flow < 0) return Api.Error("流量上限不能为负数");

        var row = rows[0];
        var inPort = request.InPort ?? DbValue.Int(row, "in_port");
        if (inPort is < 1 or > 65535) return Api.Error("入口端口必须在 1 到 65535 之间");
        var reverseRelay = DbValue.Int(row, "type") == 3;
        var outPort = request.OutPort ?? DbValue.NullableInt(row, "out_port");
        if (request.OutPort is < 1 or > 65535) return Api.Error(reverseRelay ? "公网入口端口必须在 1 到 65535 之间" : "中继内部端口必须在 1 到 65535 之间");
        var localServiceNodeId = DbValue.Long(row, "in_node_id");
        if (await IsPortInUse(db, localServiceNodeId, inPort, ct, request.Id)) return Api.Error(reverseRelay ? "公网映射端口已被该公网入口节点上的其他转发占用" : "入口端口已被该入口节点上的其他隧道占用");
        var relayNodeId = reverseRelay ? DbValue.Long(row, "in_node_id") : DbValue.Int(row, "type") == 2 ? DbValue.Long(row, "out_node_id") : 0;
        if (outPort is > 0 && relayNodeId > 0 && await IsPortInUse(db, relayNodeId, outPort.Value, ct, request.Id)) return Api.Error(reverseRelay ? "公网入口端口已被该公网入口节点上的其他映射占用" : "中继内部端口已被该节点上的其他转发占用");

        await db.ExecuteAsync("UPDATE `forward` SET name=@name,remote_addr=@remote,in_port=@in,out_port=@out,strategy=@strategy,interface_name=@iface,flow=@flow,updated_time=@now WHERE id=@id", Domain.Params(("name", request.Name), ("remote", request.RemoteAddr), ("in", inPort), ("out", outPort), ("strategy", request.Strategy ?? "fifo"), ("iface", request.InterfaceName), ("flow", request.Flow), ("now", Domain.Now()), ("id", request.Id)), ct);
        await FlowOperations.ReconcileTunnelAsync(DbValue.Long(rows[0], "tunnel_id"), db, gateway, ct);
        return Api.Ok(null, "forward updated");
    }

    public static async Task<IResult> DeleteAsync(long id, AuthUser user, Db db, NodeGateway? gateway, bool force, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT f.*,t.in_node_id,t.out_node_id,t.type,t.protocol,ut.id relation_id FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id LEFT JOIN user_tunnel ut ON ut.user_id=f.user_id AND ut.tunnel_id=f.tunnel_id WHERE f.id=@id", Domain.Params(("id", id)), ct);
        if (rows.Count == 0 || !Domain.IsAdmin(user) && DbValue.Long(rows[0], "user_id") != user.Id) return Api.Error("forward not found");
        if (!force && gateway is not null)
        {
            var row = rows[0];
            var serviceName = $"{id}_{DbValue.Int(row, "user_id")}_{DbValue.Int(row, "relation_id")}";
            await RollbackNodeConfigAsync(row, serviceName, gateway, ct);
            if (serviceName != id.ToString()) await RollbackNodeConfigAsync(row, id.ToString(), gateway, ct);
        }
        return await db.ExecuteAsync("DELETE FROM `forward` WHERE id=@id", Domain.Params(("id", id)), ct) == 0 ? Api.Error("forward not found") : Api.Ok(null, "forward deleted");
    }

    private static async Task RollbackNodeConfigAsync(IReadOnlyDictionary<string, object?> tunnel, string serviceName, NodeGateway gateway, CancellationToken ct)
    {
        var type = DbValue.Int(tunnel, "type");
        var protocol = DbValue.String(tunnel, "protocol");
        var inServices = protocol == "anytls" ? new[] { $"{serviceName}_tcp" } : new[] { $"{serviceName}_tcp", $"{serviceName}_udp" };
        if (type == 3)
        {
            var reverseServices = protocol == "anytls" ? new[] { $"{serviceName}_tcp" } : new[] { $"{serviceName}_tcp", $"{serviceName}_udp" };
            await gateway.SendAsync(DbValue.Long(tunnel, "out_node_id"), "DeleteService", new { services = reverseServices }, ct);
            await gateway.SendAsync(DbValue.Long(tunnel, "out_node_id"), "DeleteChains", new { chain = $"{serviceName}_chains" }, ct);
            await gateway.SendAsync(DbValue.Long(tunnel, "in_node_id"), "DeleteService", new { services = new[] { $"{serviceName}_relay" } }, ct);
        }
        else
        {
            await gateway.SendAsync(DbValue.Long(tunnel, "in_node_id"), "DeleteService", new { services = inServices }, ct);
            if (type == 2)
            {
                await gateway.SendAsync(DbValue.Long(tunnel, "in_node_id"), "DeleteChains", new { chain = $"{serviceName}_chains" }, ct);
                await gateway.SendAsync(DbValue.Long(tunnel, "out_node_id"), "DeleteService", new { services = new[] { $"{serviceName}_tls" } }, ct);
            }
        }
    }

    public static async Task<IResult> ChangeStatusAsync(long id, HttpContext context, Db db, NodeGateway gateway, int status, string command, CancellationToken ct)
    {
        if (!Auth.TryUser(context, out var user, out var error)) return error!;
        if (!Domain.IsAdmin(user!)) return Api.Error("forbidden", 403);
        var rows = await db.QueryAsync("SELECT f.*,t.flow tunnel_flow,t.flow_limit_gb tunnel_limit_gb,t.in_node_id,t.out_node_id,t.type,t.protocol,ut.id relation_id FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id LEFT JOIN user_tunnel ut ON ut.user_id=f.user_id AND ut.tunnel_id=f.tunnel_id WHERE f.id=@id", Domain.Params(("id", id)), ct);
        if (rows.Count == 0) return Api.Error("forward not found");
        var row = rows[0];
        var total = FlowOperations.Usage(row, "tunnel_flow");
        var forwardLimit = FlowOperations.ToBytes(DbValue.Long(row, "flow"));
        var tunnelLimit = FlowOperations.ToBytes(DbValue.Long(row, "tunnel_limit_gb"));
        if (status == 1 && (forwardLimit > 0 && total >= forwardLimit || tunnelLimit > 0 && await FlowOperations.TunnelUsageAsync(DbValue.Long(row, "tunnel_id"), db, ct) >= tunnelLimit))
            return Api.Error("流量额度已用尽，请提高上限后再恢复");
        var nodeRows = await db.QueryAsync("SELECT in_node_id FROM tunnel WHERE id=@id", Domain.Params(("id", DbValue.Long(rows[0], "tunnel_id"))), ct);
        if (nodeRows.Count == 0) return Api.Error("tunnel not found");
        var serviceName = $"{id}_{DbValue.Int(row, "user_id")}_{DbValue.Int(row, "relation_id")}";
        var reverseTunnel = DbValue.Int(row, "type") == 3;
        var reverseServices = DbValue.String(row, "protocol") == "anytls" ? new[] { $"{serviceName}_tcp" } : new[] { $"{serviceName}_tcp", $"{serviceName}_udp" };
        var nodeId = reverseTunnel ? DbValue.Long(row, "out_node_id") : DbValue.Long(nodeRows[0], "in_node_id");
        var response = await gateway.SendAsync(nodeId, command, new { services = reverseTunnel ? reverseServices : new[] { $"{serviceName}_tcp", $"{serviceName}_udp", $"{serviceName}_tls" } }, ct);
        if (!response.Success) return Api.Error(response.Message);
        await db.ExecuteAsync("UPDATE `forward` SET status=@status,updated_time=@now WHERE id=@id", Domain.Params(("status", status), ("now", Domain.Now()), ("id", id)), ct);
        return Api.Ok(null, status == 1 ? "forward resumed" : "forward paused");
    }

    private static async Task<int> NextPort(Db db, long nodeId, string? configuredRange, int fallbackStart, int fallbackEnd, int? reservedPort, CancellationToken ct)
    {
        if (!PortRangeRules.TryParse(configuredRange, fallbackStart, fallbackEnd, out _, out var ranges, out _)) return 0;
        var used = await UsedPortsAsync(db, nodeId, ct);
        if (reservedPort is > 0) used.Add(reservedPort.Value);
        foreach (var (start, end) in ranges)
            for (var port = start; port <= end; port++) if (!used.Contains(port)) return port;
        return 0;
    }

    private static async Task<bool> IsPortInUse(Db db, long nodeId, int port, CancellationToken ct, long? excludedForwardId = null) => (await UsedPortsAsync(db, nodeId, ct, excludedForwardId)).Contains(port);

    private static async Task<HashSet<int>> UsedPortsAsync(Db db, long nodeId, CancellationToken ct, long? excludedForwardId = null)
    {
        var rows = await db.QueryAsync("SELECT f.in_port AS port FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id WHERE (@exclude IS NULL OR f.id<>@exclude) AND t.in_node_id=@node UNION SELECT f.out_port AS port FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id WHERE (@exclude IS NULL OR f.id<>@exclude) AND f.out_port IS NOT NULL AND (t.type=2 AND t.out_node_id=@node OR t.type=3 AND t.in_node_id=@node)", Domain.Params(("node", nodeId), ("exclude", excludedForwardId)), ct);
        return rows.Select(row => DbValue.Int(row, "port")).ToHashSet();
    }

    public static async Task<string?> SyncTunnelAsync(IReadOnlyDictionary<string, object?> tunnel, Db db, NodeGateway gateway, CancellationToken ct)
    {
        var limiter = await EnsureTunnelLimiterAsync(tunnel, gateway, ct);
        if (limiter.Error is not null) return limiter.Error;
        var forwards = await db.QueryAsync("SELECT f.*,ut.id relation_id FROM `forward` f LEFT JOIN user_tunnel ut ON ut.user_id=f.user_id AND ut.tunnel_id=f.tunnel_id WHERE f.tunnel_id=@tunnel AND f.status=1 ORDER BY f.id", Domain.Params(("tunnel", DbValue.Long(tunnel, "id"))), ct);
        foreach (var forward in forwards)
        {
            var serviceName = $"{DbValue.Long(forward, "id")}_{DbValue.Int(forward, "user_id")}_{DbValue.Int(forward, "relation_id")}";
            var type = DbValue.Int(tunnel, "type");
            var command = type == 3
                ? new[] { GostProtocol.ReverseRelayService(serviceName, DbValue.NullableInt(forward, "out_port") ?? 0, DbValue.String(tunnel, "protocol"), limiter.Name, DbValue.String(forward, "interface_name"), DbValue.String(tunnel, "anytls_password"), RelayUsername(serviceName), DbValue.String(forward, "relay_secret")) }
                : BuildServices(serviceName, tunnel, DbValue.String(forward, "remote_addr"), DbValue.Int(forward, "in_port"), limiter.Name, DbValue.String(forward, "strategy"), DbValue.String(forward, "interface_name"));
            var response = await gateway.SendAsync(DbValue.Long(tunnel, "in_node_id"), "SyncService", command, ct);
            if (!response.Success) return response.Message;
            if (type == 2)
            {
                var outPort = DbValue.NullableInt(forward, "out_port") ?? 0;
                var remote = GostProtocol.RemoteService(serviceName, outPort, DbValue.String(forward, "remote_addr"), DbValue.String(tunnel, "protocol"), DbValue.String(forward, "strategy"), limiter.Name, DbValue.String(forward, "interface_name"), DbValue.String(tunnel, "anytls_password"));
                var remoteResponse = await gateway.SendAsync(DbValue.Long(tunnel, "out_node_id"), "SyncService", new[] { remote }, ct);
                if (!remoteResponse.Success) return remoteResponse.Message;
                var chain = GostProtocol.Chain(serviceName, $"{DbValue.String(tunnel, "out_ip")}:{outPort}", DbValue.String(tunnel, "protocol"), DbValue.String(forward, "interface_name"), DbValue.String(tunnel, "anytls_password"));
                var chainResponse = await SyncChainAsync(gateway, DbValue.Long(tunnel, "in_node_id"), chain, ct);
                if (!chainResponse.Success) return chainResponse.Message;
            }
            else if (type == 3)
            {
                if (DbValue.String(tunnel, "protocol") != "quic")
                    await gateway.SendAsync(DbValue.Long(tunnel, "out_node_id"), "DeleteService", new { services = new[] { $"{serviceName}_udp" } }, ct);
                var chain = GostProtocol.Chain(serviceName, $"{DbValue.String(tunnel, "entry_ip")}:{DbValue.NullableInt(forward, "out_port") ?? 0}", DbValue.String(tunnel, "protocol"), DbValue.String(forward, "interface_name"), DbValue.String(tunnel, "anytls_password"), RelayUsername(serviceName), DbValue.String(forward, "relay_secret"));
                var chainResponse = await SyncChainAsync(gateway, DbValue.Long(tunnel, "out_node_id"), chain, ct);
                if (!chainResponse.Success) return chainResponse.Message;
                var reverse = GostProtocol.ReverseServices(serviceName, DbValue.Int(forward, "in_port"), DbValue.String(forward, "remote_addr"), DbValue.String(forward, "strategy"), DbValue.String(tunnel, "protocol"), limiter.Name, DbValue.String(forward, "interface_name"));
                var reverseResponse = await gateway.SendAsync(DbValue.Long(tunnel, "out_node_id"), "SyncService", reverse, ct);
                if (!reverseResponse.Success) return reverseResponse.Message;
            }
        }
        return null;
    }

    private static async Task<NodeResponse> SyncChainAsync(NodeGateway gateway, long nodeId, object chain, CancellationToken ct)
    {
        var response = await gateway.SendAsync(nodeId, "UpdateChains", chain, ct);
        return !response.Success && response.Message.Contains("chain", StringComparison.OrdinalIgnoreCase) && response.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? await gateway.SendAsync(nodeId, "AddChains", chain, ct)
            : response;
    }

    private static object[] BuildServices(string name, IReadOnlyDictionary<string, object?> tunnel, string remoteAddr, int inPort, string? limiter, string? strategy = null, string? interfaceName = null)
    {
        var type = DbValue.Int(tunnel, "type");
        return GostProtocol.Services(name, inPort, type, remoteAddr, DbValue.String(tunnel, "tcp_listen_addr"), DbValue.String(tunnel, "udp_listen_addr"), strategy ?? "fifo", limiter, interfaceName, DbValue.String(tunnel, "protocol"), DbValue.String(tunnel, "anytls_password"));
    }

    private static string RelayUsername(string serviceName) => $"relay-{serviceName}";

    private static async Task<(string? Name, string? Error)> EnsureTunnelLimiterAsync(IReadOnlyDictionary<string, object?> tunnel, NodeGateway gateway, CancellationToken ct)
    {
        var speed = DbValue.Int(tunnel, "speed_limit_kbps");
        if (speed <= 0) return (null, null);
        var name = $"relayforge_tunnel_{DbValue.Long(tunnel, "id")}";
        var bytes = speed * 1024L;
        var data = new { name, limits = new[] { $"$ {bytes} {bytes}" } };
        var response = await gateway.SendAsync(DbValue.Long(tunnel, "in_node_id"), "AddLimiters", data, ct);
        if (!response.Success) response = await gateway.SendAsync(DbValue.Long(tunnel, "in_node_id"), "UpdateLimiters", new { limiter = name, data }, ct);
        if (DbValue.Int(tunnel, "type") is 2 or 3)
        {
            var remoteResponse = await gateway.SendAsync(DbValue.Long(tunnel, "out_node_id"), "AddLimiters", data, ct);
            if (!remoteResponse.Success) remoteResponse = await gateway.SendAsync(DbValue.Long(tunnel, "out_node_id"), "UpdateLimiters", new { limiter = name, data }, ct);
            if (!remoteResponse.Success) return (null, remoteResponse.Message);
        }
        return response.Success ? (name, null) : (null, response.Message);
    }
}
