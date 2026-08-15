namespace RelayForge.Panel.Api;

public sealed class PeriodicFlowWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
    }
}
