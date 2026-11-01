using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AstraMcp.Core;

namespace AstraMcp.Infrastructure;

/// <summary>Polls the durable queue without tying a calculation to a tool-call lifetime.</summary>
/// <param name="executor">Exclusive native execution coordinator.</param>
/// <param name="options">Polling configuration.</param>
/// <param name="logger">Operational logger.</param>
public sealed class QueueWorker(JobExecutor executor, AdapterOptions options, ILogger<QueueWorker> logger) : BackgroundService
{
    /// <summary>Processes waiting orders until the MCP host shuts down.</summary>
    /// <param name="stoppingToken">Host lifetime cancellation.</param>
    /// <returns>The background execution task.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await executor.ExecuteNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError(e, "Queue iteration failed; durable state and artifacts are retained."); }
            try { await Task.Delay(options.PollIntervalMilliseconds, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
