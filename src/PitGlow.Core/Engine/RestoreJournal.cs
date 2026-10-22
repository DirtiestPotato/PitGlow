using System.Text.Json;
using PitGlow.Backends;
using PitGlow.Devices;

namespace PitGlow.Engine;

/// <summary>
/// Remembers the original lighting of every device PitGlow is currently driving, on disk,
/// so that if PitGlow is killed the next start can put everything back.
/// </summary>
public sealed class RestoreJournal(string path)
{
    public sealed record Entry(string Backend, string Name, int Occurrence, string Location,
                               string? Mode, int LedCount, string[] Colors);

    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path => path;

    public static string DefaultPath()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitGlow");
        return System.IO.Path.Combine(dir, "restore.json");
    }

    public IReadOnlyList<Entry> Entries()
    {
        lock (_gate) return Load();
    }

    public void Add(DeviceInfo info)
    {
        lock (_gate)
        {
            var entries = Load().Where(e => !Matches(e, info)).ToList();
            entries.Add(new Entry(info.Backend, info.Name, info.Occurrence, info.Location, info.OriginalMode,
                                  info.LedCount, [.. info.OriginalColors.Select(c => c.ToHex())]));
            Save(entries);
        }
    }

    public void Remove(DeviceInfo info)
    {
        lock (_gate)
        {
            var entries = Load();
            var kept = entries.Where(e => !Matches(e, info)).ToList();
            if (kept.Count != entries.Count) Save(kept);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    public static bool Matches(Entry e, DeviceInfo info) =>
        e.Backend == info.Backend && e.Name == info.Name && e.Occurrence == info.Occurrence;

    /// <summary>
    /// After a crash: put back the lighting recorded for each device still listed.
    /// Returns how many devices were restored. Must run before a fresh discovery is trusted,
    /// because the "original" colors seen after a crash are PitGlow's own last frame.
    /// </summary>
    public int Recover(IBackend backend, IReadOnlyList<DeviceInfo> devices)
    {
        var entries = Entries().Where(e => e.Backend == backend.Id).ToList();
        int restored = 0;
        foreach (var e in entries)
        {
            var info = devices.FirstOrDefault(d => d.Name == e.Name && d.Occurrence == e.Occurrence)
                       ?? devices.FirstOrDefault(d => e.Location.Length > 0 && d.Location == e.Location);
            if (info is null || info.LedCount != e.LedCount) continue;
            try
            {
                using var session = backend.OpenSession(info);
                session.Connect();
                session.RestoreTo(e.Mode, [.. e.Colors.Select(Rgb.FromHex)]);
                restored++;
            }
            catch (Exception ex)
            {
                Log.Warn($"{info.Label}: could not restore lighting from the last session: {ex.Message}");
            }
        }
        lock (_gate)
        {
            var kept = Load().Where(e => e.Backend != backend.Id).ToList();
            if (kept.Count == 0) Clear(); else Save(kept);
        }
        return restored;
    }

    private List<Entry> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void Save(List<Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            if (entries.Count == 0)
            {
                File.Delete(path);
                return;
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write the restore file: {ex.Message}");
        }
    }
}
