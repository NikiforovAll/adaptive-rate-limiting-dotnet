using System.Globalization;
using NoisyNeighbor.QuotaApi;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<QuotaOptions>(builder.Configuration.GetSection("Quota"));
builder.Services.AddSingleton<Quota>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet(
    "/items/{id:int}",
    async (int id, HttpContext context, Quota quota) =>
    {
        if (!quota.TryTake(context.Request.Headers["X-Client"], out var retryAfter))
        {
            context.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(
                CultureInfo.InvariantCulture
            );
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        await Task.Delay(
            TimeSpan.FromMilliseconds(Random.Shared.Next(10, 40)),
            context.RequestAborted
        );
        return Results.Ok(new { id });
    }
);

app.Run();
