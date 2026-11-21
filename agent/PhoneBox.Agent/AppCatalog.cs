using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PhoneBox.Agent;

public sealed record AppEntry(string Id, string Name, string Path);

/// <summary>
/// The applications installed on the computer, for the All apps widget: the list, their icons and opening one.
/// The phone only ever sends an app's id; the agent looks it up in its own scan, so a request cannot name a path or a
/// command. Opening apps is allowed only while an All apps widget is on one of the pages. macOS for now.
/// </summary>
public sealed class AppCatalog(ConfigStore config, ILogger<AppCatalog> log)
{
    public const string WidgetType = "apps";
    private const long RescanMs = 5 * 60_000;

    private readonly Lock _lock = new();
    private IReadOnlyList<AppEntry> _apps = [];
    private Dictionary<string, AppEntry> _byId = [];
    private long _scannedAt = long.MinValue / 2;
    private int _scanning;
    private sealed record IconEntry(byte[]? Png, long At);
    private readonly ConcurrentDictionary<string, IconEntry> _icons = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _iconGates = new(); // one conversion per app at a time
    private readonly SemaphoreSlim _iconSlots = new(2); // icon conversion spawns sips / qlmanage: at most two at a time

    /// <summary>
    /// How long a failed conversion is remembered before it is tried again (a timeout on a busy computer). Shorter than
    /// the panel's one retry after 15 s (widgets/pc.js), so that retry gets a fresh attempt instead of the cached failure.
    /// </summary>
    internal long FailureRetryMs { get; init; } = 10_000;

    /// <summary>For tests: the conversion instead of sips / Quick Look.</summary>
    internal Func<AppEntry, byte[]?> Converter { get; init; } = MakeIcon;

    /// <summary>Folders scanned for .app bundles (top level and one folder deep, e.g. /Applications/Utilities).</summary>
    internal IReadOnlyList<string> Roots { get; init; } = OperatingSystem.IsMacOS()
        ? ["/Applications", "/System/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications")]
        : [];

    public static bool Supported => OperatingSystem.IsMacOS();

    /// <summary>Convert all icons in the background after a scan (off in tests, which scan fake bundles).</summary>
    internal bool WarmIcons { get; init; } = true;

    /// <summary>Only while an All apps widget is on a page that is shown (like AllowKill): hiding the page turns opening off.</summary>
    public bool OpenAllowed() => config.Get().Widgets(enabledPagesOnly: true).Any(w => w.Type == WidgetType);

    /// <summary>The installed apps by name. The first call scans; later calls return the last scan and rescan in the background.</summary>
    public IReadOnlyList<AppEntry> List()
    {
        bool first;
        lock (_lock) first = _byId.Count == 0 && _scannedAt < 0;
        if (first) { Rescan(); return _apps; }
        if (Environment.TickCount64 - Volatile.Read(ref _scannedAt) > RescanMs && Interlocked.CompareExchange(ref _scanning, 1, 0) == 0)
            _ = Task.Run(() => { try { Rescan(); } finally { Volatile.Write(ref _scanning, 0); } });
        return _apps;
    }

    public AppEntry? Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        List();
        lock (_lock) return _byId.GetValueOrDefault(id);
    }

    internal void Rescan()
    {
        var found = new Dictionary<string, AppEntry>(StringComparer.Ordinal);
        foreach (var root in Roots)
            foreach (var app in Bundles(root))
            {
                var entry = new AppEntry(IdOf(app), System.IO.Path.GetFileNameWithoutExtension(app), app);
                found.TryAdd(entry.Id, entry);
            }
        var list = found.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        lock (_lock)
        {
            _apps = list;
            _byId = found;
            _scannedAt = Environment.TickCount64;
        }
        if (Supported && WarmIcons && Interlocked.CompareExchange(ref _warming, 1, 0) == 0)
            _ = Task.Run(async () => { try { await WarmIconsAsync(list); } finally { Volatile.Write(ref _warming, 0); } });
    }

    private int _warming;

    /// <summary>
    /// Converts the icons one after another in the background, so the phone gets them from the cache: a first
    /// conversion (sips / Quick Look) can take longer than the phone waits for an image on a busy computer.
    /// </summary>
    private async Task WarmIconsAsync(IReadOnlyList<AppEntry> apps)
    {
        foreach (var a in apps)
        {
            if (TryCached(a.Id, out _)) continue;
            try { await IconAsync(a.Id, CancellationToken.None); } catch { }
        }
    }

    private static IEnumerable<string> Bundles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root).ToList(); } catch { yield break; }
        foreach (var d in dirs)
        {
            if (System.IO.Path.GetFileName(d).StartsWith('.')) continue; // hidden apps and folders are helpers, not apps to open
            if (d.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) { yield return d; continue; }
            IEnumerable<string> inner;
            try { inner = Directory.EnumerateDirectories(d, "*.app").Where(a => !System.IO.Path.GetFileName(a).StartsWith('.')).ToList(); } catch { continue; }
            foreach (var a in inner) yield return a;
        }
    }

    /// <summary>A short id that stays the same for the same bundle path.</summary>
    internal static string IdOf(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];

    /// <summary>Opens (or brings forward) an app from the last scan; returns an error message, or null when it worked.</summary>
    public string? Open(string id)
    {
        if (!Supported) return "Opening apps is supported on macOS only";
        if (!OpenAllowed()) return "Add an All apps widget to a page to open apps from the phone";
        if (Find(id) is not { } app) return "Unknown app";
        if (!Directory.Exists(app.Path)) return $"{app.Name} is no longer installed";
        // open(1) with the bundle path as a single argument: no shell, nothing from the request but the id.
        var ok = Run(10_000, "/usr/bin/open", app.Path) is not null;
        log.LogInformation("app {Name} opened from the phone: {Result}", app.Name, ok ? "ok" : "failed");
        return ok ? null : $"Could not open {app.Name}";
    }

    /// <summary>
    /// A 96 px PNG of the app's icon. Successes are kept; a failure is kept for <see cref="FailureRetryMs"/>, then the
    /// next request converts again.
    /// </summary>
    public async Task<byte[]?> IconAsync(string id, CancellationToken ct)
    {
        if (!Supported || Find(id) is not { } app) return null;
        if (TryCached(id, out var cached)) return cached;
        // background warming and a phone request can ask for the same app at once: the second waits for the first
        var gate = _iconGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryCached(id, out cached)) return cached;
            await _iconSlots.WaitAsync(ct);
            try
            {
                var png = Converter(app);
                _icons[id] = new IconEntry(png, Environment.TickCount64);
                return png;
            }
            finally { _iconSlots.Release(); }
        }
        finally { gate.Release(); }
    }

    private bool TryCached(string id, out byte[]? png)
    {
        png = null;
        if (!_icons.TryGetValue(id, out var e)) return false;
        if (e.Png is null && Environment.TickCount64 - e.At >= FailureRetryMs) return false; // failed a while ago: try again
        png = e.Png;
        return true;
    }

    private static byte[]? MakeIcon(AppEntry app)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phonebox-app-icons");
        var id = IdOf(app.Path);
        // converters write to names of their own; only a finished PNG is moved (renamed) to the cached name, so a reader
        // never sees a half-written file, now or after a restart
        var tmp = System.IO.Path.Combine(dir, $"{id}.{Guid.NewGuid():N}.tmp.png");
        var ql = System.IO.Path.Combine(dir, $"ql-{id}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var out1 = System.IO.Path.Combine(dir, id + ".png");
            if (File.Exists(out1)) return File.ReadAllBytes(out1);

            // 1) the icon file the bundle names in Info.plist (.icns)
            var plist = System.IO.Path.Combine(app.Path, "Contents", "Info.plist");
            var iconName = File.Exists(plist) ? Run(3000, "/usr/bin/plutil", "-extract", "CFBundleIconFile", "raw", "-o", "-", plist)?.Trim() : null;
            if (!string.IsNullOrEmpty(iconName))
            {
                if (!iconName.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)) iconName += ".icns";
                var icns = System.IO.Path.Combine(app.Path, "Contents", "Resources", System.IO.Path.GetFileName(iconName));
                if (File.Exists(icns) && Run(8000, "/usr/bin/sips", "-s", "format", "png", "--resampleWidth", "96", icns, "--out", tmp) is not null && File.Exists(tmp))
                    return Publish(tmp, out1);
            }

            // 2) apps whose icon lives only in an asset catalog: Quick Look renders the bundle's icon
            Directory.CreateDirectory(ql);
            Run(10_000, "/usr/bin/qlmanage", "-t", "-s", "96", "-o", ql, app.Path);
            if (Directory.EnumerateFiles(ql, "*.png").FirstOrDefault() is { } thumb) return Publish(thumb, out1);
        }
        catch { }
        finally
        {
            try { File.Delete(tmp); } catch { }
            try { if (Directory.Exists(ql)) Directory.Delete(ql, true); } catch { }
        }
        return null;
    }

    private static byte[] Publish(string finished, string target)
    {
        File.Move(finished, target, overwrite: true); // a rename within the folder: atomic
        return File.ReadAllBytes(target);
    }

    private static string? Run(int timeoutMs, string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch { return null; }
    }
}
