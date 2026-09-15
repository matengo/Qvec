using Microsoft.Extensions.Diagnostics.HealthChecks;
using Qvec.Core;

internal sealed class QvecReadinessHealthCheck(QvecDatabase database) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(database.IsHealthy()
            ? HealthCheckResult.Healthy("Qvec database is healthy.")
            : HealthCheckResult.Unhealthy("Qvec database is not healthy."));
    }
}

internal sealed class QvecDatabaseStartupService(QvecDatabase database) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!database.IsHealthy())
        {
            throw new InvalidOperationException("Qvec database failed startup health validation.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}