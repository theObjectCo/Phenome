using System.Globalization;

namespace Phenome.Geometry.Types;

/// <summary>
/// An immutable colour as four bytes in red, green, blue, alpha order.
/// </summary>
/// <remarks>
/// Four bytes rather than four floats, because a per-vertex colour on a million-vertex mesh costs 4 MB
/// this way and 16 MB the other, and eight bits per channel is all a display can show anyway.
/// <para>
/// Field order is deliberate: it matches the byte layout a WebGL vertex buffer expects, so a colour list
/// can be handed to the GPU as a straight memory copy with no per-element conversion.
/// </para>
/// <para>
/// Deliberately not <c>System.Drawing.Color</c>, which would drag <c>System.Drawing</c> into a payload
/// that has to be downloaded by a browser. The previous library had the same instinct with its own
/// four-byte colour struct.
/// </para>
/// </remarks>
public readonly struct Color32 : IEquatable<Color32>, IFormattable
{
    internal Color32(byte red, byte green, byte blue, byte alpha)
    {
        R = red;
        G = green;
        B = blue;
        A = alpha;
    }

    /// <summary>The red channel.</summary>
    public byte R { get; }

    /// <summary>The green channel.</summary>
    public byte G { get; }

    /// <summary>The blue channel.</summary>
    public byte B { get; }

    /// <summary>The alpha channel, 255 being fully opaque.</summary>
    public byte A { get; }

    /// <summary>Opaque white.</summary>
    public static Color32 White => new(255, 255, 255, 255);

    /// <summary>Opaque black.</summary>
    public static Color32 Black => new(0, 0, 0, 255);

    /// <summary>Fully transparent black.</summary>
    public static Color32 Transparent => default;

    /// <summary>Splits the colour into its channels.</summary>
    public void Deconstruct(out byte red, out byte green, out byte blue, out byte alpha)
    {
        red = R;
        green = G;
        blue = B;
        alpha = A;
    }

    /// <inheritdoc/>
    public override string ToString() => ToString(null, null);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        formatProvider ??= CultureInfo.InvariantCulture;

        return string.Concat(
            "Color(R",
            R.ToString(format, formatProvider),
            " G",
            G.ToString(format, formatProvider),
            " B",
            B.ToString(format, formatProvider),
            " A",
            A.ToString(format, formatProvider),
            ")");
    }

    /// <inheritdoc/>
    public bool Equals(Color32 other) => R == other.R && G == other.G && B == other.B && A == other.A;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Color32 other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    /// <summary>Compares all four channels.</summary>
    public static bool operator ==(Color32 a, Color32 b) => a.Equals(b);

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(Color32 a, Color32 b) => !a.Equals(b);
}
