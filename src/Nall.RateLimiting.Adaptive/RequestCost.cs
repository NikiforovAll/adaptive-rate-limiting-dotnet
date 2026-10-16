namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// What one request costs from the budget of a limiter.
/// </summary>
/// <param name="Units">
/// The units the request takes. Zero means the request does not use this budget: it is sent at
/// once, and its response is only recorded.
/// </param>
/// <param name="Category">
/// An optional low-cardinality label, such as <c>batch</c>. Metrics use it as the
/// <c>ratelimit.category</c> tag, and a throttle classifier can use it to read the response.
/// </param>
public readonly record struct RequestCost(int Units, string? Category = null);
