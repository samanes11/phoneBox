using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Watches recent OpenCode sessions through its background service's own local HTTP API (GET /api/session, Basic auth
/// with the password from service.json), on Windows and inside WSL distros alike — see <see cref="OpenCodeServices"/>.
/// Only the list endpoint is read: the metadata the sessions widget shows (directory, title, model, token counters,
/// times, outcome). Prompts, replies and tool arguments are never fetched, so the request stays small and no chat
/// content leaves the service. A service that is not running contributes nothing.
/// </summary>
internal static class OpenCodeSessions
{
    private const int Hours = 3;
    private const int MaxSessions = 50;
    private const int MaxPerService = 50;

    private static readonly object HttpLock = new();
    private static HttpMessageHandler? _handlerUsed;
    private static HttpClient? _http;

    /// <summary>For tests: a fake handler instead of the network, set before the first ReadAsync. Changing it swaps the client.</summary>
    internal static HttpMessageHandler? HttpHandler { get; set; }

    private static HttpClient Http
    {
        get
        {
            lock (HttpLock)
            {
                if (_http is null || !ReferenceEquals(_handlerUsed, HttpHandler))
                {
                    _http?.Dispose();
                    _handlerUsed = HttpHandler;
                    _http = NewHttpClient(_handlerUsed ?? new HttpClientHandler());
                }
                return _http;
            }
        }
    }

    private static HttpClient NewHttpClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        return http;
    }

    /// <summary>
    /// The recent sessions of every OpenCode service that is running right now, newest first. One hung service costs
    /// this round nothing but its own timeout: its sessions are skipped, the others still arrive.
    /// </summary>
    internal static async Task<List<AgentSession>> ReadAsync(ILogger? log = null, CancellationToken ct = default,
        IEnumerable<OpenCodeApprovals.ServiceInfo>? services = null, long? nowMs = null, bool wslEnabled = false,
        bool titles = false)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff = now - Hours * 3600_000L;
        var list = new List<AgentSession>();
        foreach (var service in services ?? Services(wslEnabled))
        {
            try
            {
                var sessions = await ReadServiceAsync(Http, service, cutoff, ct, titles: titles);
                if (sessions.Count > 0) list.AddRange(sessions.OrderByDescending(s => s.Since).Take(MaxPerService));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                // the client's own timeout (a service that hangs): this service contributes nothing this round
                log?.LogDebug("opencode sessions ({Url}): {Msg}", service.Url, ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException)
            {
                log?.LogDebug("opencode sessions ({Url}): {Msg}", service.Url, ex.Message); // unreachable: contributes nothing this round
            }
        }
        return list.OrderByDescending(s => s.Since).Take(MaxSessions).ToList();
    }

    /// <summary>Every running OpenCode service; tests hand their own list. Re-scanned at most every ServiceScanMs:
    /// a scan asks OpenCodeServices for the WSL services (whose running-check is fresh) and reads the native
    /// service.json; between scans the parsed list is reused, so the shares are not touched and the loop's own
    /// reads stay plain HTTP to the services, which never wakes a stopped distro.</summary>
    private static List<OpenCodeApprovals.ServiceInfo> Services(bool wslEnabled = false)
    {
        lock (ServicesLock)
        {
            // turning WSL watching on or off takes effect at once: the cache never outlives the switch it was built under
            if (Environment.TickCount64 >= _nextServiceScan || wslEnabled != _cachedWithWsl)
            {
                _nextServiceScan = Environment.TickCount64 + ServiceScanMs;
                _cachedWithWsl = wslEnabled;
                var list = new List<OpenCodeApprovals.ServiceInfo>();
                foreach (var path in OpenCodeServices.Discover(wslEnabled: false))
                    if (OpenCodeApprovals.ReadService(path) is { } service) list.Add(service);
                foreach (var (_, service) in OpenCodeServices.WslServices(wslEnabled)) list.Add(service);
                _cachedServices = list;
            }
            return [.. _cachedServices];
        }
    }

    private const int ServiceScanMs = 60_000;
    private static readonly object ServicesLock = new();
    private static long _nextServiceScan = -ServiceScanMs; // the first scan goes out at once
    private static List<OpenCodeApprovals.ServiceInfo> _cachedServices = [];
    private static bool _cachedWithWsl;

    internal static async Task<List<AgentSession>> ReadServiceAsync(HttpClient http,
        OpenCodeApprovals.ServiceInfo service, long cutoff, CancellationToken ct, long? nowMs = null, bool titles = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, service.Url + "/api/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{service.Password}")));
        using var res = await http.SendAsync(request, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized || !res.IsSuccessStatusCode) return [];
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];

        var list = new List<AgentSession>();
        foreach (var e in data.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = Str(e, "id");
            var updated = Obj(e, "time") is { } t ? Long(t, "updated") : null;
            if (id is null || updated is null || updated < cutoff) continue;

            var t0 = Obj(e, "time");
            var created = t0 is { } tt ? Long(tt, "created") ?? updated.Value : updated.Value;
            var busyAt = t0 is { } tb ? Long(tb, "busy") : null;
            var idleAt = t0 is { } ti ? Long(ti, "idle") : null;
            var compactingAt = t0 is { } tc ? Long(tc, "compacting") : null;
            // time_busy is stamped when a turn starts and time_idle when it ends; a compaction is work too, but only
            // while it is newer than the last idle
            var lastWork = compactingAt is long c && (busyAt is not long b || c > b) ? c : busyAt;
            var busy = lastWork is long w && (idleAt is not long i || w > i);

            var directory = Obj(e, "location") is { } loc ? Str(loc, "directory") : null;
            var project = Path.GetFileName((directory ?? "").TrimEnd('\\', '/'));
            if (project.Length == 0) project = "opencode";
            var outcome = busy ? null : Str(e, "outcome") switch
            {
                "succeeded" => "completed",
                "interrupted" => "aborted",
                "failed" => "failed",
                _ => null,
            };
            var tokens = Obj(e, "tokens");
            var input = tokens is { } tk ? Long(tk, "input") ?? 0 : 0;
            var output = tokens is { } tk2 ? Long(tk2, "output") ?? 0 : 0;
            var cacheRead = tokens is { } tk3 && Obj(tk3, "cache") is { } cache ? Long(cache, "read") ?? 0 : 0;
            var cacheWrite = tokens is { } tk4 && Obj(tk4, "cache") is { } cache2 ? Long(cache2, "write") ?? 0 : 0;
            var model = Obj(e, "model") is { } m ? Str(m, "id") : null;
            var title = Str(e, "title") is { Length: > 0 } raw && raw.Trim().Length > 0 ? raw.Trim() : null;
            if (title is { Length: > SessionMonitor.MaxTitleChars }) title = title[..SessionMonitor.MaxTitleChars];
            // the title is only carried when the user shares session titles (Settings › General, like Claude's);
            // without it the widget falls back to the project name, and the Name never carries the title either
            list.Add(new AgentSession(
                Id: "opencode:" + id,
                Tool: "opencode",
                Name: titles ? (title ?? ShortId(id)) : ShortId(id),
                Project: project,
                Status: busy ? "busy" : "idle",
                WaitingFor: null,
                Since: updated.Value,
                StartedAt: created,
                Model: model,
                TotalTokens: input + output + cacheRead + cacheWrite,
                InputTokens: input,
                OutputTokens: output,
                CachedInputTokens: cacheRead,
                ReasoningTokens: tokens is { } tk5 ? Long(tk5, "reasoning") ?? 0 : 0,
                TurnStartedAt: busy ? busyAt : null,
                Outcome: outcome,
                LastTool: null, // the last tool lives in the messages, which are never fetched
                Title: titles ? title : null)); // the widget shows the title as the label with the project folder under it
        }
        return list;
    }

    // session ids look like ses_<random>; the tail is enough to tell two chats with the same title apart
    internal static string ShortId(string id) => id.Length > 6 ? id[^6..] : id;

    private static JsonElement? Obj(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
