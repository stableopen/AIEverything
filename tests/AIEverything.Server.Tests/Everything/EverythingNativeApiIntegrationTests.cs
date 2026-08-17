using AIEverything.Core;
using AIEverything.Everything;

namespace AIEverything.Server.Tests.Everything;

public sealed class EverythingNativeApiIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void Running_everything_returns_real_results()
    {
        using var api = new EverythingNativeApi();
        var status = api.GetStatus();

        Assert.True(status.SdkLoaded);
        Assert.True(status.DatabaseLoaded);
        Assert.True(status.MajorVersion >= 1);

        var result = api.Query(new CompiledEverythingQuery(
            "Everything.exe", EverythingSort.NameAscending, 10, 0));

        Assert.True(result.TotalResults >= 1);
        Assert.Contains(result.Items, item =>
            item.Name.Equals("Everything.exe", StringComparison.OrdinalIgnoreCase));
    }
}
