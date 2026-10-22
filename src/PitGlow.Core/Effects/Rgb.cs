using System.Globalization;

namespace PitGlow;

/// <summary>An 8-bit RGB color.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Off = new(0, 0, 0);

    public static Rgb FromHex(string text)
    {
        var t = text.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new ConfigException($"Colors must look like #RRGGBB, got \"{text}\"");
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public int Max => Math.Max(R, Math.Max(G, B));

    /// <summary>Scale brightness. Rounds half to even, like the original Python build.</summary>
    public Rgb Scale(double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return new Rgb(Round(R * amount), Round(G * amount), Round(B * amount));
    }

    public static Rgb Lerp(Rgb a, Rgb b, double t) => new(
        Round(a.R + (b.R - a.R) * t), Round(a.G + (b.G - a.G) * t), Round(a.B + (b.B - a.B) * t));

    private static byte Round(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), 0, 255);

    public override string ToString() => ToHex();
}
