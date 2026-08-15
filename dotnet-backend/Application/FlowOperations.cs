namespace RelayForge.Panel.Api;

public static class FlowOperations
{
    private const long Gigabyte = 1024L * 1024L * 1024L;

    public static async Task ApplyAsync(FlowReport report, long nodeId, Db db, NodeGateway gateway, TelegramNotifier notifier, CancellationToken ct)
    {
        var parts = report.N?.Split('_') ?? [];
        if (parts.Length < 3 || !long.TryParse(parts[0], out var forwardId)) return;
        var rows = await db.QueryAsync("SELECT f.*,t.name tunnel_name,t.traffic_ratio,t.flow tunnel_flow,t.flow_limit_gb tunnel_limit_gb,t.in_node_id,t.out_node_id,t.type,t.protocol,u.`user` owner_name,u.flow owner_flow,u.in_flow owner_in_flow,u.out_flow owner_out_flow,ut.id relation_id,ut.flow relation_flow,ut.in_flow relation_in_flow,ut.out_flow relation_out_flow FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id JOIN `user` u ON u.id=f.user_id LEFT JOIN user_tunnel ut ON ut.user_id=f.user_id AND ut.tunnel_id=f.tunnel_id WHERE f.id=@id", Domain.Params(("id", forwardId)), ct);
        if (rows.Count == 0) return;
        var row = rows[0];
        var reportingNodeId = DbValue.Int(row, "type") == 3 ? DbValue.Long(row, "out_node_id") : DbValue.Long(row, "in_node_id");
        if (nodeId != reportingNodeId) return;
        var ratio = Convert.ToDecimal(row["traffic_ratio"] ?? 1m);
        var up = Math.Max(0, (long)Math.Round(report.U * ratio, MidpointRounding.AwayFromZero));
        var down = Math.Max(0, (long)Math.Round(report.D * ratio, MidpointRounding.AwayFromZero));
        if (up == 0 && down == 0) return;
        await db.ExecuteAsync("UPDATE `forward` SET in_flow=in_flow+@up,out_flow=out_flow+@down,updated_time=@now WHERE id=@id", Domain.Params(("up", up), ("down", down), ("now", Domain.Now()), ("id", forwardId)), ct);
        await db.ExecuteAsync("UPDATE `user` u JOIN `forward` f ON f.user_id=u.id SET u.in_flow=u.in_flow+@up,u.out_flow=u.out_flow+@down WHERE f.id=@id", Domain.Params(("up", up), ("down", down), ("id", forwardId)), ct);
        await db.ExecuteAsync("UPDATE user_tunnel ut JOIN `forward` f ON f.user_id=ut.user_id AND f.tunnel_id=ut.tunnel_id SET ut.in_flow=ut.in_flow+@up,ut.out_flow=ut.out_flow+@down WHERE f.id=@id", Domain.Params(("up", up), ("down", down), ("id", forwardId)), ct);

        var current = await db.QueryAsync("SELECT f.*,t.name tunnel_name,t.flow tunnel_flow,t.flow_limit_gb tunnel_limit_gb,ut.id relation_id,ut.flow relation_flow,ut.in_flow relation_in_flow,ut.out_flow relation_out_flow,u.`user` owner_name,u.flow owner_flow,u.in_flow owner_in_flow,u.out_flow owner_out_flow FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id JOIN `user` u ON u.id=f.user_id LEFT JOIN user_tunnel ut ON ut.user_id=f.user_id AND ut.tunnel_id=f.tunnel_id WHERE f.id=@id", Domain.Params(("id", forwardId)), ct);
        if (current.Count == 0) return;
        var currentRow = current[0];
        var forwardLimit = ToBytes(DbValue.Long(currentRow, "flow"));
        var tunnelLimit = ToBytes(DbValue.Long(currentRow, "tunnel_limit_gb"));
        var forwardUsage = Usage(currentRow, "tunnel_flow");
        var tunnelUsage = await TunnelUsageAsync(DbValue.Long(currentRow, "tunnel_id"), db, ct);
        var forwardExceeded = forwardLimit > 0 && forwardUsage >= forwardLimit;
        var tunnelExceeded = tunnelLimit > 0 && tunnelUsage >= tunnelLimit;
        _ = notifier.NotifyFlowThresholdsAsync(currentRow, tunnelUsage, CancellationToken.None);
        if (DbValue.Int(currentRow, "status") == 1 && (forwardExceeded || tunnelExceeded))
        {
            if (tunnelExceeded) await PauseTunnelForwardsAsync(DbValue.Long(currentRow, "tunnel_id"), nodeId, db, gateway, ct);
            else await PauseForwardAsync(currentRow, nodeId, db, gateway, ct);
        }
    }

    public static async Task ReconcileTunnelAsync(long tunnelId, Db db, NodeGateway gateway, CancellationToken ct)
    {
        var rows = await db.QueryAsync("SELECT f.*,t.flow tunnel_flow,t.flow_limit_gb tunnel_limit_gb,t.in_node_id,t.out_node_id,t.type,t.protocol,ut.id relation_id FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id LEFT JOIN user_tunnel ut ON ut.user_id=f.user_id AND ut.tunnel_id=f.tunnel_id WHERE f.tunnel_id=@tunnel AND f.status IN (1,2)", Domain.Params(("tunnel", tunnelId)), ct);
        var usage = await TunnelUsageAsync(tunnelId, db, ct);
        var tunnelLimit = rows.Count > 0 ? ToBytes(DbValue.Long(rows[0], "tunnel_limit_gb")) : 0;
        foreach (var row in rows)
        {
            var forwardUsage = Usage(row, "tunnel_flow");
            var forwardLimit = ToBytes(DbValue.Long(row, "flow"));
            var serviceName = $"{DbValue.Long(row, "id")}_{DbValue.Int(row, "user_id")}_{DbValue.Int(row, "relation_id")}";
            var exceeded = forwardLimit > 0 && forwardUsage >= forwardLimit || tunnelLimit > 0 && usage >= tunnelLimit;
            var reverseTunnel = DbValue.Int(row, "type") == 3;
            var reverseServices = DbValue.String(row, "protocol") == "anytls" ? new[] { $"{serviceName}_tcp" } : new[] { $"{serviceName}_tcp", $"{serviceName}_udp" };
            if (exceeded && DbValue.Int(row, "status") == 1)
            {
                var pause = await gateway.SendAsync(reverseTunnel ? DbValue.Long(row, "out_node_id") : DbValue.Long(row, "in_node_id"), "PauseService", new { services = reverseTunnel ? reverseServices : new[] { $"{serviceName}_tcp", $"{serviceName}_udp", $"{serviceName}_tls" } }, ct);
                if (pause.Success) await db.ExecuteAsync("UPDATE `forward` SET status=2,updated_time=@now WHERE id=@id AND status=1", Domain.Params(("now", Domain.Now()), ("id", DbValue.Long(row, "id"))), ct);
            }
            else if (!exceeded && DbValue.Int(row, "status") == 2)
            {
                var resume = await gateway.SendAsync(reverseTunnel ? DbValue.Long(row, "out_node_id") : DbValue.Long(row, "in_node_id"), "ResumeService", new { services = reverseTunnel ? reverseServices : new[] { $"{serviceName}_tcp", $"{serviceName}_udp", $"{serviceName}_tls" } }, ct);
                if (resume.Success) await db.ExecuteAsync("UPDATE `forward` SET status=1,updated_time=@now WHERE id=@id AND status=2", Domain.Params(("now", Domain.Now()), ("id", DbValue.Long(row, "id"))), ct);
            }
        }
    }

    public static long ToBytes(long gigabytes) => gigabytes <= 0 ? 0 : gigabytes > long.MaxValue / Gigabyte ? long.MaxValue : gigabytes * Gigabyte;

    public static long Usage(IReadOnlyDictionary<string, object?> row, string flowTypeKey) => DbValue.Int(row, flowTypeKey) == 1
        ? DbValue.Long(row, "in_flow")
        : DbValue.Long(row, "in_flow") + DbValue.Long(row, "out_flow");

    public static async Task<long> TunnelUsageAsync(long tunnelId, Db db, CancellationToken ct)
    {
        var value = await db.ScalarAsync("SELECT COALESCE(SUM(CASE WHEN t.flow=1 THEN f.in_flow ELSE f.in_flow + f.out_flow END),0) FROM `forward` f JOIN tunnel t ON t.id=f.tunnel_id WHERE f.tunnel_id=@tunnel", Domain.Params(("tunnel", tunnelId)), ct);
        return value is null ? 0 : Convert.ToInt64(value);
    }

    private static async Task PauseTunnelForwardsAsync(long tunnelId, long nodeId, Db db, NodeGateway gateway, CancellationToken ct)
    {
        var forwards = await db.QueryAsync("SELECT f.id,f.user_id,ut.id relation_id FROM `forward` f LEFT JOIN user_tunnel ut ON ut.user_id=f.user_id AND ut.tunnel_id=f.tunnel_id WHERE f.tunnel_id=@tunnel AND f.status=1", Domain.Params(("tunnel", tunnelId)), ct);
        foreach (var forward in forwards)
        {
            var serviceName = $"{DbValue.Long(forward, "id")}_{DbValue.Int(forward, "user_id")}_{DbValue.Int(forward, "relation_id")}";
            await gateway.SendAsync(nodeId, "PauseService", new { services = new[] { $"{serviceName}_tcp", $"{serviceName}_udp", $"{serviceName}_tls" } }, ct);
            await db.ExecuteAsync("UPDATE `forward` SET status=2,updated_time=@now WHERE id=@id AND status=1", Domain.Params(("now", Domain.Now()), ("id", DbValue.Long(forward, "id"))), ct);
        }
    }

    private static async Task PauseForwardAsync(IReadOnlyDictionary<string, object?> forward, long nodeId, Db db, NodeGateway gateway, CancellationToken ct)
    {
        var serviceName = $"{DbValue.Long(forward, "id")}_{DbValue.Int(forward, "user_id")}_{DbValue.Int(forward, "relation_id")}";
        await gateway.SendAsync(nodeId, "PauseService", new { services = new[] { $"{serviceName}_tcp", $"{serviceName}_udp", $"{serviceName}_tls" } }, ct);
        await db.ExecuteAsync("UPDATE `forward` SET status=2,updated_time=@now WHERE id=@id AND status=1", Domain.Params(("now", Domain.Now()), ("id", DbValue.Long(forward, "id"))), ct);
    }
}
