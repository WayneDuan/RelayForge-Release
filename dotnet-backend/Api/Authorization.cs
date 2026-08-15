namespace RelayForge.Panel.Api;

public static class Auth
{
    public static bool TryUser(HttpContext context, out AuthUser? user, out IResult? error)
    {
        user = null;
        error = null;
        var tokens = context.RequestServices.GetRequiredService<TokenService>();
        if (!tokens.TryValidate(context.Request.Headers.Authorization.ToString(), out user))
        {
            error = Api.Error("unauthorized", 401);
            return false;
        }

        return true;
    }
}

public static class NodeAuth
{
    public const string HeaderName = "X-RelayForge-Node-Secret";

    public static string? ReadSecret(HttpContext context, IConfiguration configuration)
    {
        var header = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(header)) return header.Trim();
        if (configuration.GetValue("Security:AllowLegacyQuerySecrets", false))
            return context.Request.Query["secret"].ToString();
        return null;
    }
}
