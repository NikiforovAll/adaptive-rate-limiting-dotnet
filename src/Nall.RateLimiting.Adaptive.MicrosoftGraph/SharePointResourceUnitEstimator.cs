using System.Text.Json;
using System.Web;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph;

/// <summary>
/// Tells the SharePoint resource units (RU) of a Graph request, and of each inner request of a
/// <c>$batch</c>.
/// </summary>
/// <remarks>
/// A request that does not touch SharePoint (such as <c>/users</c>) costs zero units, because it
/// uses another Graph budget: the limiter sends it at once and only records its throttles.
/// </remarks>
public sealed class SharePointResourceUnitEstimator : IRequestCostEstimator
{
    /// <summary>
    /// The category of a single SharePoint request.
    /// </summary>
    public const string SharePointCategory = "sharepoint";

    /// <summary>
    /// The category of a <c>$batch</c> request.
    /// <see cref="GraphBatchThrottleClassifier"/> reads the inner responses of this category.
    /// </summary>
    public const string BatchCategory = "sharepoint.batch";

    /// <summary>
    /// The category of a Graph request that does not touch SharePoint.
    /// </summary>
    public const string GraphCategory = "graph";

    private const string BatchPathSuffix = "/$batch";
    private const string PermissionsName = "permissions";
    private const string ExpandParameter = "expand";
    private const string SelectParameter = "select";
    private const string TokenParameter = "token";
    private const string DeltaTokenParameter = "deltatoken";
    private const string ODataParameterPrefix = "$";
    private const string DeltaSegment = "delta";
    private const string QualifiedDeltaSegment = "microsoft.graph.delta";
    private const string DeltaTokenFunction = "delta(token=";
    private const string BatchRequestsProperty = "requests";
    private const string BatchMethodProperty = "method";
    private const string BatchUrlProperty = "url";

    private static readonly HashSet<string> s_sharePointSegments = new(
        ["sites", "drives", "drive", "shares"],
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly HashSet<string> s_listSegments = new(
        ["children", "items", "lists", "drives", "sites", "versions", "columns", "contentTypes"],
        StringComparer.OrdinalIgnoreCase
    );

    private readonly SharePointResourceUnitCosts _costs;

    /// <summary>
    /// Creates the estimator.
    /// </summary>
    /// <param name="costs">The units of each kind of request. The defaults if null.</param>
    public SharePointResourceUnitEstimator(SharePointResourceUnitCosts? costs = null)
    {
        _costs = costs ?? new SharePointResourceUnitCosts();
        ArgumentOutOfRangeException.ThrowIfNegative(_costs.Get, nameof(costs));
        ArgumentOutOfRangeException.ThrowIfNegative(_costs.List, nameof(costs));
        ArgumentOutOfRangeException.ThrowIfNegative(_costs.Permissions, nameof(costs));
    }

    /// <inheritdoc />
    /// <remarks>
    /// For a <c>$batch</c>, the estimator reads the body as a byte array. It does not read it as
    /// a stream, because the content would then cache the stream and a retry handler could not
    /// send the body again. A body that is not a valid batch costs <see cref="SharePointResourceUnitCosts.Get"/>.
    /// </remarks>
    public async ValueTask<RequestCost> EstimateAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestUri is not { } uri)
        {
            return new RequestCost(0, GraphCategory);
        }

        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        if (
            request.Method == HttpMethod.Post
            && IsBatchPath(path)
            && request.Content is { } content
        )
        {
            var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new RequestCost(OfBatch(body), BatchCategory);
        }

        var units = Of(request.Method, path, uri.IsAbsoluteUri ? uri.Query : string.Empty);
        return new RequestCost(units, units == 0 ? GraphCategory : SharePointCategory);
    }

    internal int Of(HttpMethod method, string path, string query)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!segments.Any(static s => s_sharePointSegments.Contains(s)))
        {
            return 0;
        }

        var parameters = HttpUtility.ParseQueryString(query);
        // Kiota sends %24expand, and Graph also accepts expand without the $.
        string? Parameter(string name) =>
            parameters[name] ?? parameters[ODataParameterPrefix + name];
        bool HasPermissions(string? value) =>
            value?.Contains(PermissionsName, StringComparison.OrdinalIgnoreCase) == true;

        if (
            segments.Any(static s => s.Equals(PermissionsName, StringComparison.OrdinalIgnoreCase))
            || HasPermissions(Parameter(ExpandParameter))
            || HasPermissions(Parameter(SelectParameter))
        )
        {
            return _costs.Permissions;
        }

        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            return _costs.List;
        }

        var last = segments[^1];
        if (IsDelta(last))
        {
            var hasToken =
                Parameter(TokenParameter) is not null
                || Parameter(DeltaTokenParameter) is not null
                || last.Contains(DeltaTokenFunction, StringComparison.OrdinalIgnoreCase);
            return hasToken ? _costs.Get : _costs.List;
        }

        return s_listSegments.Contains(last) ? _costs.List : _costs.Get;
    }

    private static bool IsBatchPath(string path) =>
        path.TrimEnd('/').EndsWith(BatchPathSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool IsDelta(string segment) =>
        segment.StartsWith(DeltaSegment, StringComparison.OrdinalIgnoreCase)
        || segment.StartsWith(QualifiedDeltaSegment, StringComparison.OrdinalIgnoreCase);

    private int OfBatch(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty(BatchRequestsProperty, out var requests))
            {
                return _costs.Get;
            }

            var total = 0;
            using var items = requests.EnumerateArray();
            foreach (var request in items)
            {
                var method =
                    request.TryGetProperty(BatchMethodProperty, out var m)
                    && m.GetString() is { } name
                        ? new HttpMethod(name)
                        : HttpMethod.Get;
                var url = request.TryGetProperty(BatchUrlProperty, out var u)
                    ? u.GetString() ?? string.Empty
                    : string.Empty;
                var queryStart = url.IndexOf('?', StringComparison.Ordinal);
                total +=
                    queryStart < 0
                        ? Of(method, url, string.Empty)
                        : Of(method, url[..queryStart], url[queryStart..]);
            }

            return total;
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or InvalidOperationException
                        or ArgumentException
                        or FormatException
            )
        {
            return _costs.Get;
        }
    }
}
