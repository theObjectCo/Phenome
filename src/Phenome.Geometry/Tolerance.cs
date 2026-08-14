namespace Phenome.Geometry;

/// <summary>
/// Default tolerances used by the geometry primitives.
/// </summary>
/// <remarks>
/// These are <em>numeric</em> tolerances, deliberately tight. They exist so that primitives have a
/// sane default for degeneracy checks, not so that callers can configure model precision globally.
/// Anything that depends on the scale of the model — welding vertices, joining curves, deciding
/// whether two fabricated parts touch — must take its tolerance as an explicit argument, because
/// only the caller knows the units and the acceptable error.
/// <para>
/// There is intentionally no mutable global tolerance: a static setter is shared mutable state, and
/// it makes results depend on call order and on which thread got there first.
/// </para>
/// </remarks>
public static class Tolerance
{
    /// <summary>
    /// Threshold below which a length is treated as zero.
    /// </summary>
    public const double Zero = 1e-12;

    /// <summary>
    /// <see cref="Zero"/> squared, for comparing against squared lengths without taking a root.
    /// </summary>
    public const double ZeroSquared = Zero * Zero;

    /// <summary>
    /// Default tolerance for comparing positions and distances.
    /// </summary>
    public const double Distance = 1e-9;

    /// <summary>
    /// Default tolerance for comparing angles, in radians.
    /// </summary>
    public const double Angle = 1e-9;
}
