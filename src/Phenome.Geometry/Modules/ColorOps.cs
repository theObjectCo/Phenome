namespace Phenome.Geometry.Modules;

/// <summary>
/// Everything you can do with a <see cref="Color32"/>.
/// </summary>
public static class ColorOps
{
    /// <summary>An opaque colour from three channels.</summary>
    public static Color32 Create(byte red, byte green, byte blue) => new(red, green, blue, 255);

    /// <summary>A colour from four channels.</summary>
    public static Color32 Create(byte red, byte green, byte blue, byte alpha) =>
        new(red, green, blue, alpha);

    /// <summary>
    /// A colour from four channels given as fractions in [0, 1], rounded to the nearest byte.
    /// </summary>
    /// <remarks>Values outside [0, 1] are clamped rather than wrapping around.</remarks>
    public static Color32 CreateFromFractions(double red, double green, double blue, double alpha = 1.0) =>
        new(ToByte(red), ToByte(green), ToByte(blue), ToByte(alpha));

    /// <summary>
    /// Packs the colour into a single integer, red in the least significant byte.
    /// </summary>
    /// <remarks>
    /// Matches the byte order of the struct itself, so a packed value and the struct agree when either is
    /// reinterpreted as bytes on a little-endian machine — which every browser target is.
    /// </remarks>
    public static uint ToUInt32(Color32 color) =>
        color.R | ((uint)color.G << 8) | ((uint)color.B << 16) | ((uint)color.A << 24);

    /// <summary>Unpacks a colour written by <see cref="ToUInt32"/>.</summary>
    public static Color32 CreateFromUInt32(uint packed) => new(
        (byte)(packed & 0xFF),
        (byte)((packed >> 8) & 0xFF),
        (byte)((packed >> 16) & 0xFF),
        (byte)((packed >> 24) & 0xFF));

    /// <summary>
    /// Blends between two colours channel by channel. <paramref name="parameter"/> 0 returns
    /// <paramref name="from"/>, 1 returns <paramref name="to"/>; values outside [0, 1] are clamped.
    /// </summary>
    /// <remarks>
    /// Interpolates the stored byte values directly, which is what a vertex-colour gradient wants. Note
    /// that this is not perceptually uniform and not gamma-correct; it is the same blend a GPU does when
    /// it interpolates vertex colours across a triangle.
    /// </remarks>
    public static Color32 Lerp(Color32 from, Color32 to, double parameter)
    {
        double t = Math.Clamp(parameter, 0.0, 1.0);

        return new Color32(
            Blend(from.R, to.R, t),
            Blend(from.G, to.G, t),
            Blend(from.B, to.B, t),
            Blend(from.A, to.A, t));
    }

    private static byte Blend(byte from, byte to, double parameter) =>
        (byte)Math.Round(from + ((to - from) * parameter));

    private static byte ToByte(double fraction) =>
        (byte)Math.Round(Math.Clamp(fraction, 0.0, 1.0) * 255.0);
}
