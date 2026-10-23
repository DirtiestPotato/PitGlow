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
public sealed class Wreckfest2Config(int port, Func<IEnumerable<string>>? files = null)
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinRewrite = TimeSpan.FromSeconds(10);   // never fight another app in a tight loop

    private readonly Func<IEnumerable<string>> _files = files ?? DefaultFiles;
    private readonly Dictionary<string, (DateTime Seen, double WrittenAt)> _state = new();
    private double _nextCheck;

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
        if (now < _nextCheck) return;
        _nextCheck = now + CheckEvery.TotalSeconds;
        foreach (var path in _files())
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_state.TryGetValue(path, out var st) && st.Seen == stamp) continue;
                if (_state.TryGetValue(path, out st) && now - st.WrittenAt < MinRewrite.TotalSeconds) continue;
                var text = File.ReadAllText(path);
                var fixedText = Ensure(text, port);
                if (fixedText is null)
                {
                    _state[path] = (stamp, st.WrittenAt);
                    continue;
                }
                File.WriteAllText(path, fixedText);
                _state[path] = (File.GetLastWriteTimeUtc(path), now);
                Log.Info($"Wreckfest 2: added PitGlow's telemetry output (port {port}) back to {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warn($"Wreckfest 2: couldn't update {path}: {ex.Message}");
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
