namespace RelayForge.Panel.Api;

public static class SpeedLimitOperations
{
    public static async Task<IResult> SaveAsync(SpeedLimitRequest request, long? id, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct)
    {
        if (!RequireAdmin(context, out var error)) return error!;
        var now = Domain.Now();
        if (id is null)
            await db.ExecuteAsync("INSERT INTO speed_limit (name,speed,tunnel_id,tunnel_name,created_time,updated_time,status) VALUES (@name,@speed,@tunnel,@tunnelName,@now,@now,1)", Domain.Params(("name", request.Name), ("speed", request.Speed), ("tunnel", request.TunnelId), ("tunnelName", request.TunnelName), ("now", now)), ct);
        else
            await db.ExecuteAsync("UPDATE speed_limit SET name=@name,speed=@speed,tunnel_id=@tunnel,tunnel_name=@tunnelName,updated_time=@now WHERE id=@id", Domain.Params(("name", request.Name), ("speed", request.Speed), ("tunnel", request.TunnelId), ("tunnelName", request.TunnelName), ("now", now), ("id", id.Value)), ct);
        return Api.Ok(null, id is null ? "speed limit created" : "speed limit updated");
    }

    public static async Task<IResult> DeleteAsync(long id, HttpContext context, Db db, NodeGateway gateway, CancellationToken ct)
    {
        if (!RequireAdmin(context, out var error)) return error!;
        return await db.ExecuteAsync("DELETE FROM speed_limit WHERE id=@id", Domain.Params(("id", id)), ct) == 0
            ? Api.Error("speed limit not found")
            : Api.Ok(null, "speed limit deleted");
    }

    private static bool RequireAdmin(HttpContext context, out IResult? error)
    {
        error = null;
        if (!Auth.TryUser(context, out var user, out error)) return false;
        if (!Domain.IsAdmin(user!))
        {
            error = Api.Error("forbidden", 403);
            return false;
        }

        return true;
    }
}
