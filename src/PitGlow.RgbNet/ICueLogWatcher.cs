using System.Text;
using System.Text.RegularExpressions;

namespace PitGlow.RgbNet;

/// <summary>
/// Reads iCUE's own log for the moment a Corsair hub ignores a lighting write ("Command ignored by
/// device ... WriteBuffer"). After that iCUE can stop putting SDK colors on that hub while still
/// accepting them, with no error reported to the app, so the log is the only place to see it.
/// Only lines written after the watcher starts count.
/// </summary>
public sealed partial class ICueLogWatcher
{
    private readonly Func<IEnumerable<string>> _dirs;
    private string? _file;
    private long _position;

    public ICueLogWatcher(Func<IEnumerable<string>>? dirs = null) => _dirs = dirs ?? DefaultDirs;

    public static IEnumerable<string> DefaultDirs() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Corsair", "Logs", "CUE5"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corsair", "Logs", "CUE5"),
    ];

    [GeneratedRegex(@"Command ignored by device bragiprotocol::CommandId::WriteBuffer")]
    private static partial Regex DroppedWrite();

    /// <summary>Lighting writes the hub dropped since the last call (0 the first time: it starts at the end).</summary>
    public int Poll()
    {
        var newest = _dirs().Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.log"))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null) return 0;
        try
        {
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (newest.FullName != _file)
            {
                // iCUE (re)started a log: a new file is read from its start, the first one from its end.
                bool first = _file is null;
                _file = newest.FullName;
                _position = first ? fs.Length : 0;
                if (first) return 0;
            }
            if (fs.Length < _position) _position = 0;
            if (fs.Length == _position) return 0;
            fs.Position = _position;
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var text = reader.ReadToEnd();
            _position = fs.Length;
            return DroppedWrite().Matches(text).Count;
        }
        catch (IOException)
        {
            return 0;   // iCUE is rotating or holding the file; try next time
        }
    }
}
