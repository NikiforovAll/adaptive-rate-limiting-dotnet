using Nall.RateLimiting.Adaptive;
using NoisyNeighbor.Worker;
using OpenTelemetry.Metrics;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient("redis");

builder.Services.AddAdaptiveRateLimiter("quota-api").WithInstanceCount<RedisInstanceCount>();
builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
    metrics.AddMeter(AdaptiveRateLimiterMetrics.MeterName)
);

builder
    .Services.AddHttpClient(
        "quota-api",
        client =>
        {
            client.BaseAddress = new Uri("https+http://quota-api");
            client.DefaultRequestHeaders.Add("X-Client", "worker");
        }
    )
    .AddAdaptiveRateLimitHandler("quota-api");

builder.Services.AddHostedService<LoadWorker>();

builder.Build().Run();
