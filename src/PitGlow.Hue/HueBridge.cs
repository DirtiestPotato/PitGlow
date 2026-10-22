using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PitGlow.Backends;

namespace PitGlow.Hue;

/// <summary>One light (or one segment of a gradient light) in an Entertainment area.</summary>
public sealed record HueChannel(int Id, string Name, string? LightId);

/// <summary>A Hue Entertainment area: the lights PitGlow can stream colors to.</summary>
public sealed record HueArea(string Id, string Name, string Status, string? StreamerId, IReadOnlyList<HueChannel> Channels)
{
    public bool InUseByOther(string? ourAppId) => Status == "active" && StreamerId is not null && StreamerId != ourAppId;
}

public sealed record HueBridgeInfo(string Ip, string Id, string Name);

/// <summary>Result of pressing-the-button pairing.</summary>
public sealed record HuePairing(string AppKey, string ClientKey);

/// <summary>
/// The Hue Bridge's local REST API (CLIP v2) over HTTPS. The bridge uses a certificate
/// from Signify's private authority, so it can't be checked against Windows' store;
/// this client only ever talks to the one bridge address it was given on your network.
/// </summary>
public sealed class HueBridge : IDisposable
{
    public const string LinkButtonNotPressed = "101";

    private readonly HttpClient _http;
    public string Ip { get; }
    public string AppKey { get; }

    public HueBridge(string ip, string appKey = "", TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        Ip = ip;
        AppKey = appKey;
        bool own = handler is null;
        handler ??= new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        _http = new HttpClient(handler, disposeHandler: own) { BaseAddress = new Uri($"https://{ip}/"), Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        if (appKey.Length > 0) _http.DefaultRequestHeaders.Add("hue-application-key", appKey);
    }

    public void Dispose() => _http.Dispose();

    // ---- finding and pairing -----------------------------------------------------------

    /// <summary>Bridges on this network, from Philips' discovery service.</summary>
    public static IReadOnlyList<string> FindBridges(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = timeout;
        var text = http.GetStringAsync("https://discovery.meethue.com/").GetAwaiter().GetResult();
        return ParseDiscovery(text);
    }

    public static IReadOnlyList<string> ParseDiscovery(string json) =>
        JsonNode.Parse(json) is JsonArray arr
            ? [.. arr.Select(b => b?["internalipaddress"]?.GetValue<string>()).OfType<string>().Distinct()]
            : [];

    /// <summary>Name and id of the bridge (works without pairing).</summary>
    public HueBridgeInfo Describe()
    {
        var node = GetJson("api/0/config");
        return new HueBridgeInfo(Ip, node?["bridgeid"]?.GetValue<string>() ?? "", node?["name"]?.GetValue<string>() ?? "Hue Bridge");
    }

    /// <summary>
    /// Ask for a key. Returns null while the bridge's link button hasn't been pressed.
    /// </summary>
    public HuePairing? TryPair(string deviceName)
    {
        var body = new JsonObject { ["devicetype"] = $"pitglow#{Clip(deviceName, 19)}", ["generateclientkey"] = true };
        var text = Send(HttpMethod.Post, "api", body);
        return ParsePairing(text);
    }

    public static HuePairing? ParsePairing(string json)
    {
        var first = (JsonNode.Parse(json) as JsonArray)?.FirstOrDefault();
        if (first?["success"] is JsonNode ok)
            return new HuePairing(ok["username"]!.GetValue<string>(), ok["clientkey"]!.GetValue<string>());
        var error = first?["error"];
        var type = error?["type"]?.ToString();
        if (type == LinkButtonNotPressed) return null;
        throw new BackendException($"Hue Bridge refused pairing: {error?["description"]?.GetValue<string>() ?? json}");
    }

    private static string Clip(string s, int max)
    {
        var clean = new string(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length > max ? clean[..max] : clean.Length == 0 ? "PC" : clean;
    }

    // ---- areas and lights --------------------------------------------------------------

    /// <summary>Our application id: the identity used for streaming and shown as the active streamer.</summary>
    public string ApplicationId()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "auth/v1");
        using var resp = _http.Send(req);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new BackendException("the Hue Bridge no longer accepts PitGlow's key; run HueSetup.bat again");
        if (resp.Headers.TryGetValues("hue-application-id", out var values) && values.FirstOrDefault() is string id)
            return id;
        throw new BackendException("the Hue Bridge did not report an application id (bridge software too old? update it in the Hue app)");
    }

    public IReadOnlyList<HueArea> Areas() =>
        ParseAreas(Resource("entertainment_configuration"), Resource("entertainment"), Resource("device"));

    public HueArea? Area(string id) => Areas().FirstOrDefault(a => a.Id == id);

    /// <summary>Current color of each light, by light id (approximate, for "no flag" frames).</summary>
    public IReadOnlyDictionary<string, Rgb> LightColors() => ParseLightColors(Resource("light"));

    public void Start(string areaId) => Action(areaId, "start");

    public void Stop(string areaId) => Action(areaId, "stop");

    private void Action(string areaId, string action)
    {
        var text = Send(HttpMethod.Put, $"clip/v2/resource/entertainment_configuration/{areaId}", new JsonObject { ["action"] = action });
        var errors = (JsonNode.Parse(text)?["errors"] as JsonArray)?.Select(e => e?["description"]?.GetValue<string>()).OfType<string>().ToList();
        if (errors is { Count: > 0 })
            throw new BackendException($"Hue Bridge could not {action} streaming: {string.Join("; ", errors)}");
    }

    public static IReadOnlyList<HueArea> ParseAreas(string configsJson, string entertainmentJson, string devicesJson)
    {
        var deviceNames = Data(devicesJson).ToDictionary(
            d => d["id"]!.GetValue<string>(),
            d => (Name: d["metadata"]?["name"]?.GetValue<string>() ?? "Hue light",
                  Light: (d["services"] as JsonArray)?.FirstOrDefault(s => s?["rtype"]?.GetValue<string>() == "light")?["rid"]?.GetValue<string>()));
        var owners = Data(entertainmentJson).ToDictionary(
            e => e["id"]!.GetValue<string>(),
            e => e["owner"]?["rid"]?.GetValue<string>());
        var areas = new List<HueArea>();
        foreach (var c in Data(configsJson))
        {
            var channels = new List<HueChannel>();
            foreach (var ch in c["channels"] as JsonArray ?? [])
            {
                if (ch is null) continue;
                int id = ch["channel_id"]!.GetValue<int>();
                var member = (ch["members"] as JsonArray)?.FirstOrDefault();
                var service = member?["service"]?["rid"]?.GetValue<string>();
                string name = $"Channel {id}";
                string? light = null;
                if (service is not null && owners.TryGetValue(service, out var owner) && owner is not null
                    && deviceNames.TryGetValue(owner, out var dev))
                {
                    name = dev.Name;
                    light = dev.Light;
                }
                channels.Add(new HueChannel(id, name, light));
            }
            // Gradient lights give one channel per segment: number them.
            var dupes = channels.GroupBy(x => x.Name).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.Count());
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < channels.Count; i++)
            {
                var ch = channels[i];
                if (!dupes.TryGetValue(ch.Name, out var total)) continue;
                seen[ch.Name] = seen.GetValueOrDefault(ch.Name) + 1;
                channels[i] = ch with { Name = $"{ch.Name} ({seen[ch.Name]}/{total})" };
            }
            areas.Add(new HueArea(
                c["id"]!.GetValue<string>(),
                c["metadata"]?["name"]?.GetValue<string>() ?? c["name"]?.GetValue<string>() ?? "Entertainment area",
                c["status"]?.GetValue<string>() ?? "inactive",
                c["active_streamer"]?["rid"]?.GetValue<string>(),
                channels.OrderBy(x => x.Id).ToList()));
        }
        return areas;
    }

    public static IReadOnlyDictionary<string, Rgb> ParseLightColors(string lightsJson)
    {
        var colors = new Dictionary<string, Rgb>();
        foreach (var l in Data(lightsJson))
        {
            var id = l["id"]!.GetValue<string>();
            bool on = l["on"]?["on"]?.GetValue<bool>() ?? false;
            double bri = (l["dimming"]?["brightness"]?.GetValue<double>() ?? 100) / 100.0;
            if (!on) { colors[id] = Rgb.Off; continue; }
            var mirek = l["color_temperature"]?["mirek"];
            bool ctValid = l["color_temperature"]?["mirek_valid"]?.GetValue<bool>() ?? false;
            var xy = l["color"]?["xy"];
            if (ctValid && mirek is not null && mirek.GetValueKind() == JsonValueKind.Number)
                colors[id] = HueColor.FromMirek(mirek.GetValue<double>(), bri);
            else if (xy is not null)
                colors[id] = HueColor.FromXy(xy["x"]!.GetValue<double>(), xy["y"]!.GetValue<double>(), bri);
            else
                colors[id] = HueColor.FromMirek(370, bri);   // white-only bulb: warm white
        }
        return colors;
    }

    private static IEnumerable<JsonNode> Data(string json) =>
        (JsonNode.Parse(json)?["data"] as JsonArray)?.OfType<JsonNode>() ?? [];

    // ---- plumbing ----------------------------------------------------------------------

    private string Resource(string type) => GetString($"clip/v2/resource/{type}");

    private JsonNode? GetJson(string path) => JsonNode.Parse(GetString(path));

    private string GetString(string path) => Send(HttpMethod.Get, path, null);

    private string Send(HttpMethod method, string path, JsonNode? body)
    {
        try
        {
            using var req = new HttpRequestMessage(method, path);
            if (body is not null)
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
            using var resp = _http.Send(req);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new BackendException("the Hue Bridge no longer accepts PitGlow's key; run HueSetup.bat again");
            using var reader = new StreamReader(resp.Content.ReadAsStream());
            return reader.ReadToEnd();
        }
        catch (BackendException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new BackendException($"can't reach the Hue Bridge at {Ip} ({ex.Message})", ex);
        }
    }
}

/// <summary>Hue color conversions (Philips' published wide-gamut formulas).</summary>
public static class HueColor
{
    public static Rgb FromXy(double x, double y, double brightness)
    {
        if (y <= 0) return Rgb.Off;
        double z = 1.0 - x - y;
        double Y = 1.0, X = Y / y * x, Z = Y / y * z;
        double r = X * 1.656492 - Y * 0.354851 - Z * 0.255038;
        double g = -X * 0.707196 + Y * 1.655397 + Z * 0.036152;
        double b = X * 0.051713 - Y * 0.121364 + Z * 1.011530;
        r = Math.Max(0, r); g = Math.Max(0, g); b = Math.Max(0, b);
        double max = Math.Max(r, Math.Max(g, b));
        if (max > 1) { r /= max; g /= max; b /= max; }
        return Make(Gamma(r), Gamma(g), Gamma(b), brightness);
    }

    /// <summary>Color temperature (mirek = 1,000,000 / kelvin) to RGB.</summary>
    public static Rgb FromMirek(double mirek, double brightness)
    {
        double t = Math.Clamp(1_000_000.0 / Math.Max(mirek, 1), 1000, 40000) / 100.0;
        double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return Make(Math.Clamp(r, 0, 255) / 255, Math.Clamp(g, 0, 255) / 255, Math.Clamp(b, 0, 255) / 255, brightness);
    }

    private static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    private static Rgb Make(double r, double g, double b, double brightness)
    {
        double k = Math.Clamp(brightness, 0, 1) * 255;
        return new Rgb((byte)Math.Round(Math.Clamp(r, 0, 1) * k), (byte)Math.Round(Math.Clamp(g, 0, 1) * k), (byte)Math.Round(Math.Clamp(b, 0, 1) * k));
    }
}
