using PhoneBox.Agent;
using Xunit;

/// <summary>Naming the apps behind microphone recordings (#282).</summary>
public class PrivacyMonitorTests
{
    [Fact]
    public void A_session_is_named_after_its_own_name_then_its_process()
    {
        Assert.Equal("Teams call", PrivacyMonitor.SessionName(1234, "  Teams call "));
        var self = Environment.ProcessId;
        var name = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        // An empty name, or a resource reference ("@%SystemRoot%\..."), falls back to the process.
        Assert.Equal(name, PrivacyMonitor.SessionName(self, null));
        Assert.Equal(name, PrivacyMonitor.SessionName(self, @"@%SystemRoot%\System32\AudioSrv.Dll,-202"));
        Assert.Equal("Windows", PrivacyMonitor.SessionName(0, ""));
    }

    [Fact]
    public void A_session_of_a_process_that_has_ended_still_counts_as_an_app()
    {
        // A process id that can't exist: the recording is still reported, just without a name.
        Assert.Equal("an app", PrivacyMonitor.SessionName(int.MaxValue, null));
        Assert.Null(PrivacyMonitor.ProcessName(int.MaxValue));
    }

    [Fact]
    public void An_app_with_several_sessions_is_listed_once()
    {
        Assert.Equal(["Zoom", "obs64"], PrivacyMonitor.Distinct(["Zoom", "zoom", " ", "obs64", "ZOOM"]));
    }
}
