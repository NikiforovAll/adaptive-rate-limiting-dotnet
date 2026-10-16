using Shouldly;

namespace Nall.RateLimiting.Adaptive.Tests;

public class AssemblyTests
{
    [Test]
    public void CoreAssemblyLoads()
    {
        var assembly = System.Reflection.Assembly.Load("Nall.RateLimiting.Adaptive");

        assembly.GetName().Name.ShouldBe("Nall.RateLimiting.Adaptive");
    }
}
