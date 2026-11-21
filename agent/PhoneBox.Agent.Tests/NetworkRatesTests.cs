using PhoneBox.Agent;
using Xunit;

public class NetworkRatesTests
{
    [Fact]
    public void AnInterfaceThatAppearsOrDisappearsDoesNotChangeOtherTraffic()
    {
        var before = new Dictionary<string, (long, long)> { ["wifi"] = (100, 200), ["detached"] = (1000, 1000) };
        var after = new Dictionary<string, (long, long)> { ["wifi"] = (140, 220), ["attached"] = (9000, 9500) };
        Assert.Equal((20.0, 10.0), SystemMonitor.NetworkRates(before, after, seconds: 2));
    }

    [Fact]
    public void ResetCountersDoNotHideTrafficFromAnotherInterface()
    {
        var before = new Dictionary<string, (long, long)> { ["wifi"] = (100, 200), ["ethernet"] = (1000, 1000) };
        var after = new Dictionary<string, (long, long)> { ["wifi"] = (140, 220), ["ethernet"] = (30, 20) };
        Assert.Equal((20.0, 10.0), SystemMonitor.NetworkRates(before, after, seconds: 2));
    }
}
