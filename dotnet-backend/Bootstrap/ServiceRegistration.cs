using Microsoft.AspNetCore.Cors.Infrastructure;

namespace RelayForge.Panel.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddRelayForgeServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCors(options => options.AddPolicy("RelayForge", policy => ConfigureCors(policy, configuration)));
        services.AddSingleton<Db>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<PasswordService>();
        services.AddSingleton<TotpService>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<LoginRateLimiter>();
        services.AddSingleton(_ => new AesCrypto(
            configuration["INTEGRATION_ENCRYPTION_KEY"] ??
            throw new InvalidOperationException("INTEGRATION_ENCRYPTION_KEY is required")));
        services.AddHttpClient("telegram", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<TelegramNotifier>();
        services.AddSingleton<XuiClient>();
        services.AddSingleton<XuiIntegrationService>();
        services.AddSingleton<NodeGateway>();
        services.AddSingleton<PeriodicFlowWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<PeriodicFlowWorker>());
        return services;
    }

    private static void ConfigureCors(CorsPolicyBuilder policy, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length > 0) policy.WithOrigins(origins);

        policy.AllowAnyHeader().AllowAnyMethod();
    }
}
