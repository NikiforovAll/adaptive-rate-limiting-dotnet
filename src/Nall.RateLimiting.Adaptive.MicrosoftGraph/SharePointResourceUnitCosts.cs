namespace Nall.RateLimiting.Adaptive.MicrosoftGraph;

/// <summary>
/// The SharePoint resource units (RU) of each kind of Graph request.
/// </summary>
/// <remarks>
/// The defaults are the costs that Microsoft gives in
/// <see href="https://learn.microsoft.com/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online#application-throttling">
/// application throttling</see>.
/// </remarks>
public sealed class SharePointResourceUnitCosts
{
    /// <summary>
    /// A request that reads one resource, such as one drive item. The default is 1.
    /// </summary>
    public int Get { get; init; } = 1;

    /// <summary>
    /// A request that reads a collection, such as <c>children</c> or a delta without a token,
    /// and each write. The default is 2.
    /// </summary>
    public int List { get; init; } = 2;

    /// <summary>
    /// A request that reads or changes permissions. The default is 5.
    /// </summary>
    public int Permissions { get; init; } = 5;
}
