using Nall.RateLimiting.Adaptive;

namespace NoisyNeighbor.Worker;

/// <summary>
/// Keeps more requests in flight than the limiter lets through, so the limiter always has work
/// that waits. The AIMD controller raises the rate only after requests waited.
/// </summary>
public sealed class LoadWorker(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = configuration.GetValue("Load:Concurrency", 16);
        return Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => RunAsync(stoppingToken)));
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var client = httpClientFactory.CreateClient("quota-api");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync(
                    new Uri($"/items/{Random.Shared.Next(1000)}", UriKind.Relative),
                    stoppingToken
                );
            }
            catch (RateLimitRejectedException) { }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
