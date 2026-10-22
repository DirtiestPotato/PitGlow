using System.Net;
using System.Text;
using PitGlow.Backends;
using PitGlow.Config;
using PitGlow.Devices;
using PitGlow.Engine;
using PitGlow.Hue;
using PitGlow.Rendering;

namespace PitGlow.Tests;

public class HueTests
{
    public const string AreaId = "1a8d99cc-967b-44f2-9202-43f976c0fa6b";
    private const string AppId = "c0ffee00-1234-4abc-9def-000000000001";

    public const string Configs = """
        {"errors":[],"data":[{"id":"1a8d99cc-967b-44f2-9202-43f976c0fa6b","type":"entertainment_configuration",
          "metadata":{"name":"Sim Rig"},"configuration_type":"screen","status":"inactive",
          "channels":[
            {"channel_id":0,"position":{"x":-0.5,"y":0.8,"z":0},"members":[{"service":{"rid":"ent-play-l","rtype":"entertainment"},"index":0}]},
            {"channel_id":1,"position":{"x":0.5,"y":0.8,"z":0},"members":[{"service":{"rid":"ent-play-r","rtype":"entertainment"},"index":0}]},
            {"channel_id":2,"members":[{"service":{"rid":"ent-strip","rtype":"entertainment"},"index":0}]},
            {"channel_id":3,"members":[{"service":{"rid":"ent-strip","rtype":"entertainment"},"index":1}]}]}]}
        """;

    public const string Entertainment = """
        {"errors":[],"data":[
          {"id":"ent-play-l","owner":{"rid":"dev-play-l","rtype":"device"}},
          {"id":"ent-play-r","owner":{"rid":"dev-play-r","rtype":"device"}},
          {"id":"ent-strip","owner":{"rid":"dev-strip","rtype":"device"}}]}
        """;

    public const string DevicesJson = """
        {"errors":[],"data":[
          {"id":"dev-play-l","metadata":{"name":"Play Left"},"services":[{"rid":"light-l","rtype":"light"},{"rid":"ent-play-l","rtype":"entertainment"}]},
          {"id":"dev-play-r","metadata":{"name":"Play Right"},"services":[{"rid":"light-r","rtype":"light"}]},
          {"id":"dev-strip","metadata":{"name":"Desk Gradient"},"services":[{"rid":"light-s","rtype":"light"}]}]}
        """;

    public const string Lights = """
        {"errors":[],"data":[
          {"id":"light-l","on":{"on":true},"dimming":{"brightness":100.0},"color":{"xy":{"x":0.675,"y":0.322}},"color_temperature":{"mirek":null,"mirek_valid":false}},
          {"id":"light-r","on":{"on":false},"dimming":{"brightness":50.0},"color":{"xy":{"x":0.3,"y":0.3}}},
          {"id":"light-s","on":{"on":true},"dimming":{"brightness":50.0},"color":{"xy":{"x":0.45,"y":0.41}},"color_temperature":{"mirek":370,"mirek_valid":true}}]}
        """;

    /// <summary>A pretend Hue Bridge answering the CLIP v2 calls PitGlow makes.</summary>
    public sealed class FakeBridge : HttpMessageHandler
    {
        public string ConfigsJson = Configs;
        public List<string> Calls = [];
        public int PairAttempts;
        public int PressButtonAfter = 2;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body = request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult() ?? "";
            Calls.Add($"{request.Method} {path} {body}".Trim());
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            string json = path switch
            {
                "/api/0/config" => """{"name":"Hue Bridge","bridgeid":"ECB5FAFFFE123456","apiversion":"1.66.0"}""",
                "/api" => ++PairAttempts <= PressButtonAfter
                    ? """[{"error":{"type":101,"address":"","description":"link button not pressed"}}]"""
                    : """[{"success":{"username":"app-key-123","clientkey":"00112233445566778899AABBCCDDEEFF"}}]""",
                "/auth/v1" => "",
                "/clip/v2/resource/entertainment_configuration" => ConfigsJson,
                "/clip/v2/resource/entertainment" => Entertainment,
                "/clip/v2/resource/device" => DevicesJson,
                "/clip/v2/resource/light" => Lights,
                _ when path.StartsWith("/clip/v2/resource/entertainment_configuration/") => """{"errors":[],"data":[]}""",
                _ => throw new InvalidOperationException($"unexpected {path}"),
            };
            if (path == "/auth/v1") resp.Headers.Add("hue-application-id", AppId);
            resp.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return resp;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(Send(request, token));
    }

    private static HueCfg Paired() => new() { Bridge = "192.168.1.50", AppKey = "app-key-123", ClientKey = "00112233445566778899AABBCCDDEEFF" };

    private static HueBackend Backend(FakeBridge fake) =>
        new(Paired(), () => new HueBridge("192.168.1.50", "app-key-123", handler: fake));

    [Fact]
    public void FindsBridgesFromDiscoveryService()
    {
        var ips = HueBridge.ParseDiscovery("""[{"id":"ecb5fafffe123456","internalipaddress":"192.168.1.50","port":443},{"id":"x","internalipaddress":"192.168.1.50"}]""");
        Assert.Equal(["192.168.1.50"], ips);
        Assert.Empty(HueBridge.ParseDiscovery("[]"));
    }

    [Fact]
    public void PairingWaitsForTheButton()
    {
        var fake = new FakeBridge();
        using var b = new HueBridge("192.168.1.50", handler: fake);
        Assert.Null(b.TryPair("RACING-PC"));
        Assert.Null(b.TryPair("RACING-PC"));
        var p = b.TryPair("RACING-PC")!;
        Assert.Equal(("app-key-123", "00112233445566778899AABBCCDDEEFF"), (p.AppKey, p.ClientKey));
        Assert.Contains(fake.Calls, c => c.Contains("\"devicetype\":\"pitglow#RACING-PC\"") && c.Contains("\"generateclientkey\":true"));
        Assert.Throws<BackendException>(() => HueBridge.ParsePairing("""[{"error":{"type":7,"description":"invalid value"}}]"""));
    }

    [Fact]
    public void AreasNameEachLightAndNumberGradientSegments()
    {
        var area = HueBridge.ParseAreas(Configs, Entertainment, DevicesJson).Single();
        Assert.Equal((AreaId, "Sim Rig", "inactive"), (area.Id, area.Name, area.Status));
        Assert.Equal(["Play Left", "Play Right", "Desk Gradient (1/2)", "Desk Gradient (2/2)"], area.Channels.Select(c => c.Name));
        Assert.Equal(["light-l", "light-r", "light-s", "light-s"], area.Channels.Select(c => c.LightId));
        Assert.False(area.InUseByOther(AppId));
        var busy = area with { Status = "active", StreamerId = "icue-app-id" };
        Assert.True(busy.InUseByOther(AppId));
        Assert.False((area with { Status = "active", StreamerId = AppId }).InUseByOther(AppId));
    }

    [Fact]
    public void LightColorsApproximateWhatTheLightsShowNow()
    {
        var colors = HueBridge.ParseLightColors(Lights);
        var red = colors["light-l"];
        Assert.True(red.R > 200 && red.G < 100 && red.B < 40, red.ToString());
        Assert.Equal(Rgb.Off, colors["light-r"]);                       // switched off
        var warm = colors["light-s"];                                    // 370 mirek warm white, half brightness
        Assert.True(warm.R > warm.G && warm.G > warm.B && warm.R < 140, warm.ToString());
        var daylight = HueColor.FromMirek(153, 1.0);                     // 6500 K: close to white
        Assert.True(daylight.R > 230 && daylight.G > 230 && daylight.B > 230, daylight.ToString());
    }

    [Fact]
    public void StreamMessageLayout()
    {
        var channels = HueBridge.ParseAreas(Configs, Entertainment, DevicesJson).Single().Channels;
        var msg = HueStream.Message(AreaId, 7, channels, [new Rgb(255, 0, 0), new Rgb(0, 128, 0), Rgb.Off, new Rgb(1, 2, 3)]);
        Assert.Equal(16 + 36 + 4 * 7, msg.Length);
        Assert.Equal("HueStream", Encoding.ASCII.GetString(msg, 0, 9));
        Assert.Equal(new byte[] { 2, 0, 7, 0, 0, 0, 0 }, msg[9..16]);    // v2.0, sequence, reserved, RGB, reserved
        Assert.Equal(AreaId, Encoding.ASCII.GetString(msg, 16, 36));
        Assert.Equal(new byte[] { 0, 0xFF, 0xFF, 0, 0, 0, 0 }, msg[52..59]);       // channel 0 red
        Assert.Equal(new byte[] { 1, 0, 0, 0x80, 0x80, 0, 0 }, msg[59..66]);       // channel 1 green 128 -> 0x8080
        Assert.Equal(new byte[] { 3, 0x01, 0x01, 0x02, 0x02, 0x03, 0x03 }, msg[73..80]);
        Assert.Throws<ArgumentException>(() => HueStream.Message("short", 0, channels, []));
    }

    [Fact]
    public void AreaBecomesAFlagDevice()
    {
        var fake = new FakeBridge();
        var info = Backend(fake).Discover(TimeSpan.FromSeconds(1)).Single();
        Assert.Equal(("Hue Sim Rig", DeviceKind.Light, 4, $"hue:{AreaId}"), (info.Name, info.Kind, info.LedCount, info.Location));
        Assert.Equal(["Play Left", "Play Right", "Desk Gradient (1/2)", "Desk Gradient (2/2)"], info.Zones.Select(z => z.Name));
        Assert.Equal(Rgb.Off, info.OriginalColors[1]);
        Assert.Equal(info.OriginalColors[2], info.OriginalColors[3]);   // both segments of one light

        var cfg = Suggest.Config([info]);
        var dev = cfg.Devices.Single();
        Assert.Equal(("hue", Roles.Flags), (dev.Backend, dev.Role));
        var plan = Layouts.Build(cfg, [info]);
        Assert.Empty(plan.Problems);

        // Yellow flag: every light flashes yellow.
        var r = new Renderer(cfg);
        var s = new Sample("t") { Flags = new HashSet<Flag> { Flag.Yellow } };
        r.Update(s, 0);
        Assert.All(r.Frame(plan.Layouts.Single(), s, 0), c => Assert.Equal(new Rgb(255, 170, 0), c));
    }

    [Fact]
    public void NotPairedOrNoAreasGiveClearMessages()
    {
        var unpaired = new HueBackend(new HueCfg());
        Assert.Contains("HueSetup.bat", Assert.Throws<BackendException>(() => unpaired.Discover(TimeSpan.FromSeconds(1))).Message);
        var fake = new FakeBridge { ConfigsJson = """{"errors":[],"data":[]}""" };
        Assert.Contains("Entertainment areas", Assert.Throws<BackendException>(() => Backend(fake).Discover(TimeSpan.FromSeconds(1))).Message);
    }

    [Fact]
    public void SessionRefusesAnAreaAnotherAppIsStreaming()
    {
        var fake = new FakeBridge();
        var backend = Backend(fake);
        var info = backend.Discover(TimeSpan.FromSeconds(1)).Single();
        fake.ConfigsJson = Configs.Replace("\"status\":\"inactive\"", "\"status\":\"active\",\"active_streamer\":{\"rid\":\"icue\",\"rtype\":\"auth_v1\"}");
        using var session = backend.OpenSession(info);
        session.Connect();
        var ex = Assert.Throws<BackendException>(session.Acquire);
        Assert.Contains("iCUE", ex.Message);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("\"action\":\"start\""));
        Assert.Equal(VerifyResult.ChangedByOther, session.Verify(engaged: true));
    }

    [Fact]
    public void HueSettingsRoundTrip()
    {
        var cfg = new Settings { Hue = Paired() };
        var loaded = SettingsFile.Parse(SettingsFile.ToToml(cfg));
        Assert.True(loaded.Hue.Paired);
        Assert.Equal(("192.168.1.50", "app-key-123", "00112233445566778899AABBCCDDEEFF"), (loaded.Hue.Bridge, loaded.Hue.AppKey, loaded.Hue.ClientKey));
        Assert.False(SettingsFile.Parse("").Hue.Paired);
    }
}

public class HueBluetoothTests
{
    [Fact]
    public void FrameSetsPowerBrightnessAndColor()
    {
        var red = HueBleProtocol.Frame(new Rgb(255, 0, 0));
        Assert.Equal(new byte[] { 1, 1, 1, 2, 1, 254 }, red[..6]);
        Assert.Equal(new byte[] { 4, 4 }, red[6..8]);
        int x = red[8] | red[9] << 8, y = red[10] | red[11] << 8;
        Assert.InRange(x / 65535.0, 0.68, 0.72);       // Philips' red corner
        Assert.InRange(y / 65535.0, 0.28, 0.31);
        Assert.Equal(new byte[] { 1, 1, 0 }, HueBleProtocol.Frame(Rgb.Off));
        var dim = HueBleProtocol.Frame(new Rgb(128, 0, 0));
        Assert.Equal(red[8..], dim[8..]);                 // same hue...
        Assert.Equal(127, dim[5]);                        // ...half brightness (128/255 of 254)
    }

    [Fact]
    public void ParseKeepsWhatToRestoreAndDropsEffects()
    {
        // On, brightness 200, color temp 370 mirek, then an effect entry.
        byte[] read = [1, 1, 1, 2, 1, 200, 3, 2, 0x72, 0x01, 6, 1, 0x02];
        var s = HueBleProtocol.Parse(read);
        Assert.Equal(new byte[] { 1, 1, 1, 2, 1, 200, 3, 2, 0x72, 0x01 }, s.Restore);
        Assert.True(s.Look.R > s.Look.G && s.Look.G > s.Look.B);   // warm white
        var off = HueBleProtocol.Parse([1, 1, 0, 2, 1, 254]);
        Assert.Equal(Rgb.Off, off.Look);
        var xy = HueBleProtocol.Frame(new Rgb(0, 0, 255));
        var blue = HueBleProtocol.Parse(xy).Look;
        Assert.True(blue.B > 200 && blue.R < 80 && blue.G < 80, blue.ToString());
        Assert.Empty(HueBleProtocol.Parse([2, 9, 1]).Restore);    // truncated: ignored
    }

    [Fact]
    public void RecognisesHueNames()
    {
        Assert.True(HueBleProtocol.LooksLikeHue("Hue color lamp"));
        Assert.True(HueBleProtocol.LooksLikeHue("Hue lightstrip plus"));
        Assert.False(HueBleProtocol.LooksLikeHue("Bluetooth Headphones"));
        Assert.False(HueBleProtocol.LooksLikeHue("Huey's speaker"));
        Assert.True(HueBleProtocol.LooksLikeHue("Desk", advertisesSignifyService: true));
    }

    [Fact]
    public void BulbsBecomeFlagLights()
    {
        var info = new DeviceInfo
        {
            Backend = "huebt", Name = "Hue color lamp", Kind = DeviceKind.Light, Location = "huebt:001788AABBCC", LedCount = 1,
            Zones = [new LedZone("Light", 0, [0])], OriginalColors = [new Rgb(255, 180, 110)], BackendState = 0x001788AABBCCUL,
        };
        var dev = Suggest.Config([info]).Devices.Single();
        Assert.Equal(("huebt", Roles.Flags, 10.0), (dev.Backend, dev.Role, dev.MaxFps));
        Assert.True(SettingsFile.Parse(SettingsFile.ToToml(new Settings())).HueBluetoothEnabled);
        Assert.False(SettingsFile.Parse("[hue_bluetooth]\nenabled = false\n").HueBluetoothEnabled);
    }
}
