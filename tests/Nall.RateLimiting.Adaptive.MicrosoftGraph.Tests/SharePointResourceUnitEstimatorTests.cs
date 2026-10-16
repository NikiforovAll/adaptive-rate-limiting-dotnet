using System.Text;

namespace Nall.RateLimiting.Adaptive.MicrosoftGraph.Tests;

public sealed class SharePointResourceUnitEstimatorTests
{
    [Test]
    [Arguments("GET", "/v1.0/drives/d/items/i", "", 1)]
    [Arguments("GET", "/v1.0/sites/s/lists/l/items/i", "", 1)]
    [Arguments("GET", "/v1.0/shares/u/driveItem", "", 1)]
    [Arguments("GET", "/v1.0/drives/d/items/i/children", "", 2)]
    [Arguments("GET", "/v1.0/sites/s/drives", "", 2)]
    [Arguments("GET", "/v1.0/drives/d/items/i/versions", "", 2)]
    [Arguments("GET", "/v1.0/drives/d/root/delta", "", 2)]
    [Arguments("GET", "/v1.0/drives/d/root/delta", "?token=abc", 1)]
    [Arguments("GET", "/v1.0/drives/d/root/delta(token='abc')", "", 1)]
    [Arguments("GET", "/v1.0/drives/d/items/i/permissions", "", 5)]
    [Arguments("GET", "/v1.0/drives/d/items/i", "?$expand=permissions", 5)]
    [Arguments("GET", "/v1.0/drives/d/items/i", "?%24expand=permissions", 5)]
    [Arguments("GET", "/v1.0/drives/d/items/i", "?$select=id,permissions", 5)]
    [Arguments("GET", "/v1.0/drives/d/items/i/children", "?$filter=name eq 'permissions'", 2)]
    [Arguments("GET", "/v1.0/sites/s/lists/l/items/delta", "?$skiptoken=abc", 2)]
    [Arguments("GET", "/v1.0/sites/s/lists/l/items/delta", "?$deltatoken=abc", 1)]
    [Arguments("POST", "/v1.0/drives/d/items/i/invite", "", 2)]
    [Arguments("PATCH", "/v1.0/drives/d/items/i", "", 2)]
    [Arguments("DELETE", "/v1.0/drives/d/items/i/permissions/p", "", 5)]
    [Arguments("GET", "/v1.0/users/u", "", 0)]
    [Arguments("GET", "/v1.0/groups/g/transitiveMembers", "", 0)]
    [Arguments("POST", "/v1.0/subscriptions", "", 0)]
    public void Of_MapsTheRequestToSharePointUnits(
        string method,
        string path,
        string query,
        int expected
    )
    {
        new SharePointResourceUnitEstimator()
            .Of(new HttpMethod(method), path, query)
            .ShouldBe(expected);
    }

    [Test]
    public void Of_CustomCosts_UsesThem()
    {
        var estimator = new SharePointResourceUnitEstimator(
            new SharePointResourceUnitCosts
            {
                Get = 3,
                List = 4,
                Permissions = 7,
            }
        );

        estimator.Of(HttpMethod.Get, "/drives/d/items/i", "").ShouldBe(3);
        estimator.Of(HttpMethod.Get, "/drives/d/items/i/children", "").ShouldBe(4);
        estimator.Of(HttpMethod.Get, "/drives/d/items/i/permissions", "").ShouldBe(7);
    }

    [Test]
    public void Create_NegativeCost_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new SharePointResourceUnitEstimator(new SharePointResourceUnitCosts { List = -1 })
        );
    }

    [Test]
    public async Task EstimateAsync_Batch_AddsUpTheInnerRequestsAndKeepsTheBody()
    {
        const string Body = """
            {"requests":[
              {"id":"1","method":"GET","url":"/drives/d/items/i"},
              {"id":"2","method":"GET","url":"/drives/d/items/i?$expand=permissions"},
              {"id":"3","method":"GET","url":"/users/u"}
            ]}
            """;
        using var request = BatchRequest(Body);

        var cost = await new SharePointResourceUnitEstimator().EstimateAsync(
            request,
            CancellationToken.None
        );

        cost.ShouldBe(new RequestCost(6, SharePointResourceUnitEstimator.BatchCategory));
        (await request.Content!.ReadAsStringAsync()).ShouldBe(Body);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""{"value":[]}""")]
    [Arguments("""{"requests":[{"id":"1","method":"","url":"/drives/d"}]}""")]
    public async Task EstimateAsync_BatchWithBadBody_CostsOneGet(string body)
    {
        using var request = BatchRequest(body);

        var cost = await new SharePointResourceUnitEstimator().EstimateAsync(
            request,
            CancellationToken.None
        );

        cost.ShouldBe(new RequestCost(1, SharePointResourceUnitEstimator.BatchCategory));
    }

    [Test]
    public async Task EstimateAsync_SingleRequest_UsesPathAndQuery()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/drives/d/root/delta?token=abc"
        );

        var cost = await new SharePointResourceUnitEstimator().EstimateAsync(
            request,
            CancellationToken.None
        );

        cost.ShouldBe(new RequestCost(1, SharePointResourceUnitEstimator.SharePointCategory));
    }

    [Test]
    public async Task EstimateAsync_NotSharePoint_CostsZeroInTheGraphCategory()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/users/u"
        );

        var cost = await new SharePointResourceUnitEstimator().EstimateAsync(
            request,
            CancellationToken.None
        );

        cost.ShouldBe(new RequestCost(0, SharePointResourceUnitEstimator.GraphCategory));
    }

    private static HttpRequestMessage BatchRequest(string body) =>
        new(HttpMethod.Post, "https://graph.microsoft.com/v1.0/$batch")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}
