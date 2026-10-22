using System.Text.RegularExpressions;

namespace PitGlow.Hue;

/// <summary>
/// Hue Bluetooth bulbs (no bridge). Not a published Philips API: this follows the GATT layout
/// verified on real bulbs over an encrypted Windows bond by the LUMEN project (MIT).
/// The light-control characteristic 932C32BD-0007 takes a list of type/length/value entries:
/// 01/1 power, 02/1 brightness (1-254), 03/2 color temperature (mirek, little endian),
/// 04/4 CIE xy (two little-endian 16-bit fractions of 65535). Writes need "write with response"
/// and a paired (bonded) bulb.
/// </summary>
public static partial class HueBleProtocol
{
    public static readonly Guid LightService = new("932C32BD-0000-47A2-835A-A8D455B859DD");
    public static readonly Guid LightState = new("932C32BD-0007-47A2-835A-A8D455B859DD");
    public static readonly Guid SignifyService = new("0000FE0F-0000-1000-8000-00805F9B34FB");
    public const ushort SignifyCompanyId = 0xFE0F;

    public const byte Power = 0x01, Brightness = 0x02, Temperature = 0x03, Xy = 0x04;

    [GeneratedRegex(@"(?<![\p{L}\p{N}])hue(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HueName();

    /// <summary>Does a Bluetooth name or advertisement look like a Hue light?</summary>
    public static bool LooksLikeHue(string? name, bool advertisesSignifyService = false) =>
        advertisesSignifyService || (!string.IsNullOrWhiteSpace(name) && HueName().IsMatch(name));

    /// <summary>One write that shows this color: power, brightness and xy together (or power off for black).</summary>
    public static byte[] Frame(Rgb color)
    {
        if (color.Max == 0) return [Power, 1, 0];
        // Hue from the color at full strength, brightness from how strong it is.
        double k = 255.0 / color.Max;
        var (x, y) = ToXy(color.R * k, color.G * k, color.B * k);
        byte bri = (byte)Math.Clamp(Math.Round(color.Max / 255.0 * 254), 1, 254);
        return [Power, 1, 1, Brightness, 1, bri, Xy, 4, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8)];
    }

    /// <summary>sRGB (0-255) to CIE xy with Philips' wide-gamut matrix, as 16-bit fractions.</summary>
    public static (ushort X, ushort Y) ToXy(double r, double g, double b)
    {
        double R = Linear(r), G = Linear(g), B = Linear(b);
        double X = R * 0.664511 + G * 0.154324 + B * 0.162028;
        double Y = R * 0.283881 + G * 0.668433 + B * 0.047685;
        double Z = R * 0.000088 + G * 0.072310 + B * 0.986039;
        double sum = X + Y + Z;
        if (sum <= 0) return (0, 0);
        return (Fraction(X / sum), Fraction(Y / sum));
    }

    private static double Linear(double c)
    {
        double v = Math.Clamp(c / 255.0, 0, 1);
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static ushort Fraction(double v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);

    /// <summary>What a bulb reported: the entries to write back afterwards, and roughly what it looks like.</summary>
    public sealed record State(byte[] Restore, Rgb Look);

    /// <summary>
    /// Parse a read of the light-state characteristic. Restore keeps the power, brightness,
    /// temperature and xy entries exactly as read (effects are dropped), so writing it back
    /// returns the bulb to how it was.
    /// </summary>
    public static State Parse(ReadOnlySpan<byte> payload)
    {
        var restore = new List<byte>();
        bool on = true;
        double bri = 1.0;
        ushort? mirek = null, x = null, y = null;
        bool xyLast = false;
        int i = 0;
        while (i + 2 <= payload.Length)
        {
            byte type = payload[i], len = payload[i + 1];
            if (i + 2 + len > payload.Length) break;
            var v = payload.Slice(i + 2, len);
            switch (type)
            {
                case Power when len == 1: on = v[0] != 0; break;
                case Brightness when len == 1: bri = v[0] / 254.0; break;
                case Temperature when len == 2: mirek = (ushort)(v[0] | v[1] << 8); xyLast = false; break;
                case Xy when len == 4: x = (ushort)(v[0] | v[1] << 8); y = (ushort)(v[2] | v[3] << 8); xyLast = true; break;
            }
            if (type is Power or Brightness or Temperature or Xy) restore.AddRange(payload.Slice(i, 2 + len).ToArray());
            i += 2 + len;
        }
        Rgb look;
        if (!on) look = Rgb.Off;
        else if (x is ushort xx && y is ushort yy && yy > 0 && (xyLast || mirek is null or 0xFFFF))
            look = HueColor.FromXy(xx / 65535.0, yy / 65535.0, bri);
        else
            look = HueColor.FromMirek(mirek is ushort m and > 0 and < 1000 ? m : 370, bri);
        return new State([.. restore], look);
    }
}
