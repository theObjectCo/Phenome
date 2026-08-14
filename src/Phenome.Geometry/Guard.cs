using System.Runtime.CompilerServices;

namespace Phenome.Geometry;

/// <summary>
/// Argument checks, written out rather than taken from the framework.
/// </summary>
/// <remarks>
/// The BCL has these — <c>ArgumentOutOfRangeException.ThrowIfNegativeOrZero</c> and friends — but they
/// arrived in .NET 8, and this library also targets .NET 7 so that it can be referenced from a Rhino 8
/// plugin. Writing them here means one file knows about that constraint instead of every call site carrying
/// a conditional.
/// <para>
/// Named for what they require rather than for what they reject, so a call site reads as the precondition it
/// is documenting: <c>Guard.Positive(width)</c> rather than "throw if not positive".
/// </para>
/// <para>
/// <see cref="CallerArgumentExpressionAttribute"/> supplies the parameter name, so the message names the
/// caller's own variable without anyone passing <c>nameof</c>.
/// </para>
/// </remarks>
internal static class Guard
{
    /// <summary>Requires a number to be greater than zero.</summary>
    public static void Positive(
        double value,
        [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be greater than zero.");
        }
    }

    /// <summary>Requires a count to be greater than zero.</summary>
    public static void Positive(
        int value,
        [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be greater than zero.");
        }
    }

    /// <summary>Requires a number to be zero or more.</summary>
    public static void NotNegative(
        int value,
        [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must not be negative.");
        }
    }

    /// <summary>Requires a number to be at least <paramref name="minimum"/>.</summary>
    public static void AtLeast(
        int value,
        int minimum,
        [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"The value must be at least {minimum}.");
        }
    }

    /// <summary>Requires a number to be below <paramref name="limit"/>.</summary>
    public static void LessThan(
        int value,
        int limit,
        [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value >= limit)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"The value must be less than {limit}.");
        }
    }

    /// <summary>Requires a string to hold something other than whitespace.</summary>
    /// <remarks>
    /// The one check here that a signature cannot make instead: an empty string is a perfectly good
    /// <see cref="string"/>.
    /// </remarks>
    public static void NotBlank(
        string value,
        [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value must not be blank.", name);
        }
    }
}
