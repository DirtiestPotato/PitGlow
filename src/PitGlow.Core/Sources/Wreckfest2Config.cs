using System.Text.Json;
using System.Text.Json.Nodes;
using PitGlow.Engine;

namespace PitGlow.Sources;

/// <summary>
/// Keeps PitGlow's output in Wreckfest 2's telemetry config. The game sends to every entry in
/// the "udp" list, so PitGlow and a motion rig can both get the data on separate ports, but
/// motion software (Sim Racing Studio, for one) rewrites the file with only its own entry each
/// time it starts. While PitGlow runs it puts its entry back, leaving every other entry alone.
/// </summary>
/// <param name="files">Fixed list of config files to check, without watching them (tests).</param>
/// <param name="watch">Config files to check and also watch for rewrites (default: every Wreckfest 2 profile).</param>
public sealed class Wreckfest2Config(int port, Func<IEnumerable<string>>? files = null, Func<IEnumerable<string>>? watch = null)
{
    private readonly bool _watchEnabled = files is null;
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinRewrite = TimeSpan.FromSeconds(10);   // never fight another app in a tight loop

    private readonly Func<IEnumerable<string>> _files = files ?? watch ?? DefaultFiles;
    private readonly Dictionary<string, (DateTime Seen, double? WrittenAt)> _state = new();
    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _watching;
    private double _nextCheck;
    private double _lastNow;

    /// <summary>Documents\My Games\Wreckfest 2\&lt;steam id&gt;\savegame\telemetry\config.json for every profile.</summary>
    public static IEnumerable<string> DefaultFiles()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Wreckfest 2");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(d => Path.Combine(d, "savegame", "telemetry", "config.json"))
            .Where(File.Exists);
    }

    /// <summary>Check the files (at most every couple of seconds) and fix any that lost PitGlow's entry.</summary>
    public void Check(double now)
    {
        _lastNow = now;
        if (now < _nextCheck) return;
        _nextCheck = now + CheckEvery.TotalSeconds;
        var paths = _files().ToList();
        if (_watchEnabled) Watch(paths);
        foreach (var path in paths) Fix(path, now);
    }

    /// <summary>
    /// Motion software rewrites the file the moment it sees the game start, and the game reads it
    /// once while loading. Polling every couple of seconds can lose that race, so also react to
    /// the rewrite itself, within milliseconds.
    /// </summary>
    private void Watch(IEnumerable<string> paths)
    {
        if (_watching) return;
        _watching = true;
        foreach (var dir in paths.Select(Path.GetDirectoryName).OfType<string>().Distinct())
        {
            try
            {
                var w = new FileSystemWatcher(dir, "config.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                FileSystemEventHandler onChange = (_, e) => FixSoon(e.FullPath);
                w.Changed += onChange;
                w.Created += onChange;
                w.Renamed += (_, e) => FixSoon(e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Log.Warn($"Wreckfest 2: can't watch {dir} for changes: {ex.Message}");
            }
        }
    }

    private void FixSoon(string path)
    {
        // The writer may still have the file open: retry briefly.
        Task.Run(async () =>
        {
            const int attempts = 40;   // up to a second: the writer (or a reader) may hold the file briefly
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (Fix(path, _lastNow, quiet: attempt < attempts - 1)) return;
                await Task.Delay(25);
            }
        });
    }

    /// <summary>Put PitGlow's entry back in one file if it's missing. False when the file was busy.</summary>
    private bool Fix(string path, double now, bool quiet = false)
    {
        lock (_lock)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_state.TryGetValue(path, out var st) && st.Seen == stamp) return true;
                if (_state.TryGetValue(path, out st) && st.WrittenAt is double w && now - w < MinRewrite.TotalSeconds) return true;
                var text = File.ReadAllText(path);
                var fixedText = Ensure(text, port);
                if (fixedText is null)
                {
                    _state[path] = (stamp, st.WrittenAt);
                    return true;
                }
                File.WriteAllText(path, fixedText);
                _state[path] = (File.GetLastWriteTimeUtc(path), now);
                Log.Info($"Wreckfest 2: added PitGlow's telemetry output (port {port}) back to {path}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (!quiet) Log.Warn($"Wreckfest 2: couldn't update {path}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// The config with an enabled entry for 127.0.0.1:port added (or re-enabled), or null when it
    /// already has one. Other entries and settings are kept as they are.
    /// </summary>
    public static string? Ensure(string json, int port)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                   ?? throw new JsonException("not a JSON object");
        if (root["udp"] is not JsonArray udp)
        {
            udp = [];
            root["udp"] = udp;
        }
        var ours = udp.OfType<JsonObject>().FirstOrDefault(e => PortOf(e) == port && IsLocal(e));
        if (ours is not null)
        {
            if (Number(ours["enabled"]) == 1) return null;
            ours["enabled"] = 1;
        }
        else
        {
            udp.Add(new JsonObject { ["enabled"] = 1, ["ip"] = "127.0.0.1", ["port"] = port.ToString() });
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static int? PortOf(JsonObject e) => Number(e["port"]);

    private static bool IsLocal(JsonObject e)
    {
        var ip = e["ip"]?.ToString();
        return ip is null or "" or "127.0.0.1" or "localhost";
    }

    private static int? Number(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var j) => j,
        _ => null,
    };
}
