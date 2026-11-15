using PitGlow.RgbNet;

namespace PitGlow.Tests;

public class ICueLogWatcherTests
{
    private const string Dropped =
        "2026-10-31 21:58:59.553 C cue.mod.bragi.hid_report_dispatcher: Command ignored by device bragiprotocol::CommandId::WriteBufferBegin Lifetime: 405 ms. \"Commander CORE\"\n";
    private const string Noise =
        "2026-10-31 21:59:01.000 W cue.mod.bragi.hid_report_dispatcher: No command candidates for unexpected reply bragiprotocol::CommandId::GetProperty\n";

    [Fact]
    public void CountsOnlyDroppedWritesLoggedAfterItStarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pitglow-icuelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "2026-10-31_21-22-12.804.log");
            File.WriteAllText(log, Dropped + Noise);                // old trouble, before PitGlow
            var watcher = new ICueLogWatcher(() => [dir]);
            Assert.Equal(0, watcher.Poll());                         // starts at the end
            File.AppendAllText(log, Noise);
            Assert.Equal(0, watcher.Poll());                         // the 10-second noise isn't a drop
            File.AppendAllText(log, Dropped + Dropped.Replace("WriteBufferBegin", "WriteBuffer"));
            Assert.Equal(2, watcher.Poll());
            Assert.Equal(0, watcher.Poll());                         // each line counted once

            // iCUE restarts and opens a new log: read that one from its start.
            var next = Path.Combine(dir, "2026-10-31_22-30-00.000.log");
            File.WriteAllText(next, Noise + Dropped);
            File.SetLastWriteTimeUtc(next, DateTime.UtcNow.AddSeconds(5));
            Assert.Equal(1, watcher.Poll());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
