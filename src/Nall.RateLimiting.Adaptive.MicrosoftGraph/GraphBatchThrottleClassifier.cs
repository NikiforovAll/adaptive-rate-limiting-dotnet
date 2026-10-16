using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph;

/// <summary>
/// Tells if a Graph response is a throttle, and also reads the inner responses of a
/// <c>$batch</c>. Graph answers a <c>$batch</c> with 200 even when it throttles inner requests.
/// </summary>
/// <remarks>
/// It reads the body only for a successful response to a request of
/// <see cref="SharePointResourceUnitEstimator.BatchCategory"/>, as a byte array, so that the
/// caller can still read it. A throttled inner response is a 429, or a 503 with
/// <c>Retry-After</c>. The signal counts each of them and takes the longest <c>Retry-After</c>.
/// </remarks>
public sealed class GraphBatchThrottleClassifier : IThrottleClassifier
{
    private const string ResponsesProperty = "responses";
    private const string StatusProperty = "status";
    private const string HeadersProperty = "headers";
    private const string RetryAfterHeader = "Retry-After";

    private readonly TimeProvider _time;
    private readonly HttpThrottleClassifier _status;

    /// <summary>
    /// Creates the classifier.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock for a <c>Retry-After</c> date. <see cref="TimeProvider.System"/> if null.
    /// </param>
    public GraphBatchThrottleClassifier(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _status = new HttpThrottleClassifier(_time);
    }

    /// <inheritdoc />
    public async ValueTask<ThrottleSignal> ClassifyAsync(
        HttpResponseMessage response,
        RequestCost cost,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(response);

        var signal = _status.Classify(response);
        if (
            signal.IsThrottled
            || cost.Category != SharePointResourceUnitEstimator.BatchCategory
            || !response.IsSuccessStatusCode
            || response.Content is not { } content
        )
        {
            return signal;
        }

        var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return OfBatch(body) ?? signal;
    }

    private ThrottleSignal? OfBatch(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty(ResponsesProperty, out var responses))
            {
                return null;
            }

            var (status, retryAfter, count) = (0, (TimeSpan?)null, 0);
            using var items = responses.EnumerateArray();
            foreach (var item in items)
            {
                var itemStatus =
                    item.TryGetProperty(StatusProperty, out var s) && s.TryGetInt32(out var code)
                        ? code
                        : (int)HttpStatusCode.OK;
                var itemRetryAfter = RetryAfterOf(item);
                if (!HttpThrottleClassifier.IsThrottled(itemStatus, itemRetryAfter))
                {
                    continue;
                }

                status = itemStatus;
                count++;
                if (itemRetryAfter > (retryAfter ?? TimeSpan.Zero))
                {
                    retryAfter = itemRetryAfter;
                }
            }

            return count == 0 ? null : new ThrottleSignal(status, retryAfter, count);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private TimeSpan? RetryAfterOf(JsonElement response)
    {
        if (
            !response.TryGetProperty(HeadersProperty, out var headers)
            || headers.ValueKind != JsonValueKind.Object
        )
        {
            return null;
        }

        using var properties = headers.EnumerateObject();
        foreach (var header in properties)
        {
            if (
                header.Name.Equals(RetryAfterHeader, StringComparison.OrdinalIgnoreCase)
                && RetryConditionHeaderValue.TryParse(header.Value.ToString(), out var value)
            )
            {
                return value switch
                {
                    { Delta: { } delta } => delta,
                    { Date: { } date } => date - _time.GetUtcNow(),
                    _ => null,
                };
            }
        }

        return null;
    }
}
