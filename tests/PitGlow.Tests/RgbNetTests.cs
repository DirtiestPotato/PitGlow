using PitGlow.RgbNet;

namespace PitGlow.Tests;

public class RgbNetTests
{
    [Fact]
    public void OccasionalUpdateFailuresAreToleratedButABurstReconnects()
    {
        var errors = new RgbNetBackend.UpdateErrors(limit: 5, windowSeconds: 3.0);
        Assert.True(errors.Add(10.0, "Failed to update device"));    // first of a burst: logged
        Assert.False(errors.Add(10.5, "Failed to update device"));   // same burst: not logged again
        Assert.False(errors.TooMany(10.5));                           // a couple of skipped frames: keep going
        for (double t = 11.0; t < 12.5; t += 0.5) errors.Add(t, "Failed to update device");
        Assert.True(errors.TooMany(12.5));                            // 5 inside 3 s: reconnect
        Assert.False(errors.TooMany(16.0));                           // old failures age out
        Assert.True(errors.Add(16.0, "again"));                       // a new burst is logged again
        errors.Reset();
        Assert.False(errors.TooMany(16.0));
        Assert.Null(errors.LastMessage);
    }
}
