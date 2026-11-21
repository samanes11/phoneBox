using System.Net;
using System.Text;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// OpenCode sessions through the background service's local API: a fake handler answers /api/session and the mapping
/// to the widget's sessions (name, project, busy/idle, outcome, tokens) is checked, on the native service and a WSL
/// one alike, with nothing running on the machine.
/// </summary>
public sealed class OpenCodeSessionsTests : IDisposable
{
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-opencode-sessions-").FullName;
    private readonly FakeService _fake = new();

    public OpenCodeSessionsTests() => OpenCodeSessions.HttpHandler = _fake;

    public void Dispose()
    {
        OpenCodeSessions.HttpHandler = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The part of OpenCode the reader talks to: one session list per service url.</summary>
    private sealed class FakeService : HttpMessageHandler
    {
        public string SessionsJson = """{"data":[]}""";
        public string SessionsJsonB = """{"data":[]}""";
        /// <summary>When set, the "b" service hangs: the reader's own timeout arrives as a cancellation.</summary>
        public bool HangB;
        public readonly List<string> Calls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            lock (Calls) Calls.Add(req.Method.Method + " " + req.RequestUri!.PathAndQuery);
            if (HangB && req.RequestUri.Authority.Contains(":28999")) return Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true));
            var body = req.RequestUri.PathAndQuery.Contains("/api/session")
                ? req.RequestUri.Authority switch
                {
                    var a when a.Contains(":28999") => SessionsJsonB,
                    var a when a.Contains(":404") => null, // nothing listens there
                    _ => SessionsJson,
                }
                : null;
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                { Content = new StringContent(body ?? "", Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>One /api/session item with the shape OpenCode 2.x sends; the token numbers are fixed.</summary>
    private string Session(string id, string directory, string title, long updated, long? busy = null, long? idle = null,
        string? outcome = "succeeded")
    {
        var time = $"{{\"created\":{_now - 600_000},\"updated\":{updated}"
            + (busy is { } b ? $",\"busy\":{b}" : "") + (idle is { } i ? $",\"idle\":{i}" : "") + "}";
        var oc = outcome is null ? "null" : $"\"{outcome}\"";
        return "{\"id\":\"" + id + "\",\"title\":\"" + title + "\",\"agent\":\"build\","
            + "\"model\":{\"id\":\"z-ai/glm-5.3-flash\",\"providerID\":\"openrouter\"},"
            + "\"outcome\":" + oc + ",\"location\":{\"directory\":\"" + directory + "\"},"
            + "\"time\":" + time + ",\"tokens\":{\"input\":100,\"output\":20,\"reasoning\":5,"
            + "\"cache\":{\"read\":400,\"write\":10}}}";
    }

    private Task<List<AgentSession>> Read(string url = "http://127.0.0.1:28894", bool titles = true) =>
        OpenCodeSessions.ReadAsync(services: [new OpenCodeApprovals.ServiceInfo(url, "pw", "2.3.2")], nowMs: _now, titles: titles);

    [Fact]
    public async Task SessionsAreMappedForTheWidget()
    {
        _fake.SessionsJson = """{"data":["""
            + Session("ses_a", "/home/haruki/GitHub/wolfi-os", "Always allow test", _now - 300_000, busy: _now - 320_000, idle: _now - 300_000)
            + "," + Session("ses_b", "C:/proj", "Busy right now", _now - 5_000, busy: _now - 10_000)
            + """]}""";

        var list = await Read();
        Assert.Equal(2, list.Count);

        var idle = Assert.Single(list, s => s.Id == "opencode:ses_a");
        Assert.Equal("opencode", idle.Tool);
        Assert.Equal("Always allow test", idle.Name);
        Assert.Equal("Always allow test", idle.Title);          // the widget shows it as the label, project under it
        Assert.Equal("wolfi-os", idle.Project);
        Assert.Equal("idle", idle.Status);
        Assert.Equal("completed", idle.Outcome);                // succeeded, and no turn since
        Assert.Equal("z-ai/glm-5.3-flash", idle.Model);
        Assert.Equal(530, idle.TotalTokens);                    // in + out + cache read + cache write
        Assert.Equal(100, idle.InputTokens);
        Assert.Equal(20, idle.OutputTokens);
        Assert.Equal(400, idle.CachedInputTokens);
        Assert.Equal(5, idle.ReasoningTokens);
        Assert.Null(idle.TurnStartedAt);
        Assert.Null(idle.LastTool);                             // tool names live in the messages, never fetched
        Assert.Equal(_now - 300_000, idle.Since);
        Assert.Equal(_now - 600_000, idle.StartedAt);

        var busy = Assert.Single(list, s => s.Id == "opencode:ses_b");
        Assert.Equal("busy", busy.Status);
        Assert.Null(busy.Outcome);                              // a busy session has not finished yet
        Assert.Equal(_now - 10_000, busy.TurnStartedAt);        // the turn's start is shown as "working for …"
        Assert.Equal("proj", busy.Project);
    }

    [Fact]
    public async Task SessionsOlderThanThreeHoursAreDropped()
    {
        _fake.SessionsJson = """{"data":["""
            + Session("ses_old", "/home/haruki/x", "Old", _now - 4 * 3600_000)
            + "," + Session("ses_new", "/home/haruki/y", "New", _now - 60_000)
            + """]}""";
        var list = await Read();
        Assert.Single(list);
        Assert.Equal("opencode:ses_new", list[0].Id);
    }

    [Fact]
    public async Task TheNativeServiceAndTheWslOneMergeIntoOneList()
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", "Native", _now - 120_000) + """]}""";
        _fake.SessionsJsonB = """{"data":[""" + Session("ses_b", "/home/haruki/w", "In WSL", _now - 60_000) + """]}""";
        var list = await OpenCodeSessions.ReadAsync(services:
        [
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:28894", "pw", "2.3.2"),
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:28999", "pw2", "2.3.2"),
        ], nowMs: _now);
        Assert.Equal(2, list.Count);
        Assert.Equal("opencode:ses_b", list[0].Id); // newest first across services
        Assert.Equal("opencode:ses_a", list[1].Id);
    }

    [Fact]
    public async Task AServiceThatIsNotRunningContributesNothing()
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", "Native", _now - 60_000) + """]}""";
        var list = await OpenCodeSessions.ReadAsync(services:
        [
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:28894", "pw", "2.3.2"),
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:404", "pw", "2.3.2"), // nothing listens there
        ], nowMs: _now, titles: true);
        Assert.Single(list);
    }

    /// <summary>A service that hangs (its own timeout arrives as a cancellation) costs the round nothing: the other
    /// services' sessions still arrive.</summary>
    [Fact]
    public async Task AServiceThatTimesOutDoesNotHideTheOthers()
    {
        _fake.HangB = true;
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", "Native", _now - 60_000) + """]}""";
        var list = await OpenCodeSessions.ReadAsync(log: null, services:
        [
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:28894", "pw", "2.3.2"),
            new OpenCodeApprovals.ServiceInfo("http://127.0.0.1:28999", "pw2", "2.3.2"), // hangs until the timeout
        ], nowMs: _now, titles: true);
        Assert.Single(list);
        Assert.Equal("opencode:ses_a", list[0].Id);
    }

    /// <summary>The titles are opt-in: without the switch the row label falls back to the short id and no title is
    /// carried anywhere.</summary>
    [Fact]
    public async Task TitlesStayOffThePhoneUnlessTheyAreShared()
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/wolfi-os", "Private title", _now - 60_000) + """]}""";
        var off = Assert.Single(await Read(titles: false));
        Assert.Null(off.Title);
        Assert.Equal("ses_a", off.Name); // the short id, never the title

        var on = Assert.Single(await Read(titles: true));
        Assert.Equal("Private title", on.Title);
        Assert.Equal("Private title", on.Name);
    }

    /// <summary>The privacy contract: the reader speaks to the session list and to nothing else.</summary>
    [Fact]
    public async Task OnlyTheSessionListIsEverFetched()
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", "Native", _now - 60_000) + """]}""";
        await Read();
        Assert.All(_fake.Calls, c => Assert.Equal("GET /api/session", c));
    }

    [Fact]
    public async Task ALongTitleIsCappedLikeClaudes()
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", new string('x', 500), _now - 60_000) + """]}""";
        var s = Assert.Single(await Read());
        Assert.Equal(SessionMonitor.MaxTitleChars, s.Title!.Length);
        Assert.Equal(s.Title, s.Name);
    }

    [Theory]
    [InlineData("Always allow test", "Always allow test", "Always allow test")]
    [InlineData("   Padded title   ", "Padded title", "Padded title")]   // surrounding whitespace goes, the words stay human
    [InlineData("   ", null, "ses_a")]                                   // no title: the short id is the name
    public void TitlesAreTrimmedAndKeptHuman(string? title, string? expectedTitle, string expectedName)
    {
        _fake.SessionsJson = """{"data":[""" + Session("ses_a", "/home/haruki/w", title ?? "", _now - 60_000) + """]}""";
        var s = Assert.Single(Read().Result);
        Assert.Equal(expectedTitle, s.Title);
        Assert.Equal(expectedName, s.Name);
    }

    [Fact]
    public void DiscoveryCombinesTheNativePathWithRunningWslDistros()
    {
        var native = Path.Combine(_dir, ".local", "state", "opencode", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, """{"url":"http://127.0.0.1:28894","password":"pw","version":"2.3.2"}""");
        var wslRoot = Path.Combine(_dir, "wsl.localhost", "Ubuntu"); // a \\wsl.localhost stand-in
        var wslService = Path.Combine(wslRoot, "home", "haruki", ".local", "state", "opencode", "service.json");
        Directory.CreateDirectory(Path.GetDirectoryName(wslService)!);
        File.WriteAllText(wslService, """{"url":"http://127.0.0.1:49374","password":"pw","version":"2.3.2"}""");
        Directory.CreateDirectory(Path.Combine(wslRoot, "home", "emptyuser")); // a user without OpenCode

        var paths = OpenCodeServices.Discover(homeDir: _dir, wslDistroRoots: [wslRoot]);
        Assert.Equal(2, paths.Count);
        Assert.Equal(native, paths[0]);                     // the native one first
        Assert.Equal(wslService, paths[1]);                 // then the distro's user's

        var nativeOnly = OpenCodeServices.Discover(homeDir: _dir, wslDistroRoots: [wslRoot], wslEnabled: false);
        Assert.Equal([native], nativeOnly);
    }

    [Fact]
    public void RunningDistroNamesComeOutOfTheWslListOutput()
    {
        // wsl.exe pipes UTF-16: NULs between the characters, a header line with the colon
        var utf16 = "W\0i\0n\0d\0o\0w\0s\0 \0S\0u\0b\0s\0y\0s\0t\0e\0m\0 \0f\0o\0r\0 \0L\0i\0n\0u\0x\0 \0D\0i\0s\0t\0r\0i\0b\0u\0t\0i\0o\0n\0s\0:\0\r\0\n\0"
            + "U\0b\0u\0n\0t\0u\0-\02\02\0.\00\04\0\r\0\n\0";
        Assert.Equal(["Ubuntu-22.04"], OpenCodeServices.ParseRunningDistros(utf16));

        Assert.Equal(["Ubuntu", "Debian"], OpenCodeServices.ParseRunningDistros(
            "Windows Subsystem for Linux Distributions:\r\nUbuntu (Default)\r\nDebian\r\n"));
        Assert.Empty(OpenCodeServices.ParseRunningDistros(null));
        Assert.Empty(OpenCodeServices.ParseRunningDistros(""));
    }
}
