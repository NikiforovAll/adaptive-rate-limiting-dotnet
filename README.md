# Nall.RateLimiting.Adaptive

Adaptive client-side rate limiting for .NET `HttpClient`, with pluggable rate controllers (AIMD, CUBIC), `Retry-After` support and a shared budget across processes. Includes Microsoft Graph and SharePoint support.

Work in progress. Not published yet.

| Package | Use |
| --- | --- |
| `Nall.RateLimiting.Adaptive` | The limiter, the `HttpClient` handler, the AIMD and CUBIC controllers, DI registration. |
| `Nall.RateLimiting.Adaptive.MicrosoftGraph` | SharePoint resource unit (RU) costs for Graph requests and `$batch`, throttled inner `$batch` responses, CSOM, and the Graph handler pipeline. |

## How it works

The limiter is a token bucket of units per minute. Each request waits for its units before it is sent. A throttled response (429, or 503 with `Retry-After`) cuts the rate and pauses the bucket for the `Retry-After`. Successful responses raise the rate again, up to `MaxUnitsPerMinute`. The rate controller decides by how much.

## Usage with dependency injection

```csharp
builder.Services
    .AddAdaptiveRateLimiter("api")
    .WithCostEstimator(new FixedCostEstimator(1));

builder.Services
    .AddHttpClient("api", c => c.BaseAddress = new Uri("https://api.example.com"))
    .AddAdaptiveRateLimitHandler("api");
```

Put the limit handler after a retry handler, so that each retry waits for units too.

The settings bind from `AdaptiveRateLimiting:Limiters:<name>` and reload on change:

```json
{
  "AdaptiveRateLimiting": {
    "Limiters": {
      "api": {
        "InitialUnitsPerMinute": 600,
        "MinUnitsPerMinute": 60,
        "MaxUnitsPerMinute": 1200,
        "Controller": "Cubic"
      }
    }
  }
}
```

A limiter is on by default (`Enabled` is `true`). The default rates are 600, 60 and 1,200 units per minute: set the rates for each API. You can also pass a delegate (`AddAdaptiveRateLimiter("api", o => o.MaxUnitsPerMinute = 1200)`) or another `IConfiguration` section. `WithDefaults(o => ...)` sets defaults that the configuration and the delegate can change. Get the limiter with `GetRequiredKeyedService<AdaptiveRateLimiter>("api")`. The limiter logs warnings through the `ILoggerFactory` of the container.

## Usage without dependency injection

```csharp
using var limiter = new AdaptiveRateLimiter("api", new AdaptiveRateLimiterOptions
{
    InitialUnitsPerMinute = 600,
    MaxUnitsPerMinute = 1200,
});

using var client = new HttpClient(new AdaptiveRateLimitHandler(limiter)
{
    InnerHandler = new SocketsHttpHandler(),
});
```

You can also call the limiter directly:

```csharp
var cost = new RequestCost(Units: 2);
var sentAt = await limiter.WaitAsync(cost, cancellationToken);
using var response = await client.SendAsync(request, cancellationToken);
await limiter.OnResponseAsync(response, cost, sentAt, cancellationToken);
```

## Rate controllers

Select the controller with `Controller`, or give your own `IRateController` with `WithController<T>()`.

- **AIMD** (default): on a throttle, the rate is multiplied by `Aimd.DecreaseFactor` (0.7). At most once per `Aimd.IncreaseInterval` (10 s), a success raises it by `Aimd.IncreaseStep` (120 units per minute). Slow and predictable.
- **CUBIC**: the rate follows the CUBIC window curve ([RFC 8312](https://www.rfc-editor.org/rfc/rfc8312)), as in the [AWS SDK adaptive retry mode](https://github.com/boto/botocore/blob/develop/botocore/retries/throttling.py). On a throttle, the rate, or the measured send rate if it is lower, is multiplied by `Cubic.Beta` (0.7). After the `Retry-After` pause, the rate grows fast back to that rate, stays near it, and then grows fast again, up to two times the measured send rate. `Cubic.ScaleConstant` (0.4) sets how fast.

A controller raises the rate only after requests had to wait, so the rate does not grow while the client sends less than the limit.

## Many processes, one budget

When several processes share one server budget, each takes an equal share. Set `InstanceCount`, or give a live count with `WithInstanceCount<T>()`. For example, the number of processes that run Hangfire servers. One process can run several servers, and a server name has the form `host:pid:guid`, so count the distinct `host:pid`:

```csharp
public sealed class HangfireProcessCount(JobStorage storage) : IInstanceCountSource
{
    private static readonly TimeSpan s_period = TimeSpan.FromMinutes(1);

    public int Current =>
        Math.Max(
            1,
            storage
                .GetMonitoringApi()
                .Servers()
                .Select(s => string.Join(':', s.Name.Split(':').Take(2)))
                .Distinct()
                .Count()
        );

    public IDisposable? OnChange(Action<int> listener) =>
        new Timer(
            _ =>
            {
                try
                {
                    listener(Current);
                }
                catch (Exception)
                {
                    // The storage is not available. Keep the last count.
                }
            },
            null,
            s_period,
            s_period
        );
}

builder.Services
    .AddAdaptiveRateLimiter("api")
    .WithInstanceCount<HangfireProcessCount>();
```

An exception in a `Timer` callback stops the process, so catch the storage errors. After `SetInstanceCount` or a live count, `InstanceCount` in the configuration no longer applies.

## Microsoft Graph and SharePoint

SharePoint Online throttles an app by resource units per minute. A Graph request costs 1 unit to read one item, 2 to read a collection or to write, and 5 for permissions ([Microsoft docs](https://learn.microsoft.com/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online#application-throttling)). Requests that do not touch SharePoint cost 0 and are not limited.

```csharp
var limiter = SharePointRateLimiter.Create("sharepoint");
var handlers = GraphClientHandlers.CreateDefaultHandlers(limiter);
var httpClient = GraphClientFactory.Create(handlers);
var graph = new GraphServiceClient(httpClient, authenticationProvider);
```

With dependency injection:

```csharp
builder.Services.AddAdaptiveRateLimiter("sharepoint").ForSharePoint();
```

Then pass `GetRequiredKeyedService<AdaptiveRateLimiter>("sharepoint")` to `GraphClientHandlers.CreateDefaultHandlers`.

`ForSharePoint()` and `SharePointRateLimiter.CreateOptions()` set SharePoint defaults: 1,500 units per minute at the start, 200 at least and 5,625 at most (90% of the per-minute budget of the largest tenants). The configuration can change them. SharePoint gives each app 1,250 to 6,250 units per minute and 1,200,000 to 6,000,000 in 24 hours, by the license count of the tenant. At 5,625 units per minute, the largest 24-hour budget runs out in about 18 hours, so for a job that runs all day, set `MaxUnitsPerMinute` to the budget of your tenant.

- `SharePointResourceUnitEstimator` counts the units of each request, and of each inner request of a `$batch`. Change the costs with `SharePointResourceUnitCosts`.
- `GraphBatchThrottleClassifier` reads the response to a `POST` to `$batch`, because Graph answers 200 even when it throttles inner requests.
- `SharePointCsom.CreateHandler(limiter)` makes the handler pipeline for CSOM calls (2 units per call by default), so that CSOM and Graph share one budget.

## Metrics

The meter is `Nall.RateLimiting.Adaptive`. If the container has an `IMeterFactory`, the limiter uses it.

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Nall.RateLimiting.Adaptive"));
```

| Instrument | Type | Use |
| --- | --- | --- |
| `ratelimit.adaptive.units` | counter | Units sent |
| `ratelimit.adaptive.throttled` | counter | Throttled responses |
| `ratelimit.adaptive.rejected` | counter | Requests rejected by a full queue or a long wait |
| `ratelimit.adaptive.wait` | histogram | Time a request waited for units |
| `ratelimit.adaptive.rate` | gauge | Current units per minute |
| `ratelimit.adaptive.queue` | gauge | Requests that wait |
| `ratelimit.adaptive.instances` | gauge | Processes that share the budget |

## Build

```bash
dotnet tool restore
dotnet husky install
dotnet build adaptive-rate-limiting-dotnet.slnx
dotnet test --solution adaptive-rate-limiting-dotnet.slnx
```
