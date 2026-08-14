namespace Phenome.Geometry;

/// <summary>
/// How an operation ended.
/// </summary>
public enum ResultStatus
{
    /// <summary>The operation completed and its output is complete.</summary>
    Success = 0,

    /// <summary>
    /// The operation produced usable output, but had to skip or approximate part of the input.
    /// </summary>
    Partial = 1,

    /// <summary>The operation produced nothing usable.</summary>
    Failed = 2,
}

/// <summary>
/// The outcome of an operation that can succeed, succeed partially, or fail.
/// </summary>
/// <remarks>
/// This replaces the integer exit code the previous library returned from operations like the mesh
/// dual, where 0 meant "fine" and 1 meant "some faces were skipped" with no way to learn which or why.
/// An integer cannot carry that, and a <see langword="bool"/> cannot represent three states at all.
/// <para>
/// Simple primitives — normalising a vector, inverting a matrix, measuring an angle — keep the plain
/// <see langword="bool"/> plus nullable <c>out</c> convention. They have exactly one way to fail and
/// the name says what it is, so a message would only ever restate the method name. This type is for
/// algorithms that can fail for several distinguishable reasons, or that can come back with a usable
/// but incomplete answer.
/// </para>
/// <para>
/// As a node output this gives a status to colour and a message to show on hover. Note that
/// <see cref="Message"/> is developer-facing English; if the editor ever needs localised text, the
/// machine-readable part to switch on is <see cref="Status"/>.
/// </para>
/// <para>
/// Unlike the geometry types, this carries its own predicates rather than pushing them into a module.
/// The data-bucket rule exists so that geometry operations all become uniform nodes; a status carrier
/// has no geometry operations to expose, and <c>ResultOps.IsSuccess(r)</c> would be noise.
/// </para>
/// </remarks>
public readonly struct OperationResult : IEquatable<OperationResult>
{
    private OperationResult(ResultStatus status, string? message)
    {
        Status = status;
        Message = message;
    }

    /// <summary>How the operation ended.</summary>
    public ResultStatus Status { get; }

    /// <summary>
    /// What went wrong or what was skipped, or <see langword="null"/> when the operation was a clean
    /// success.
    /// </summary>
    /// <remarks>
    /// Only allocated when there is something to say, so the success path costs nothing.
    /// </remarks>
    public string? Message { get; }

    /// <summary>The operation completed with complete output.</summary>
    public bool IsSuccess => Status == ResultStatus.Success;

    /// <summary>The operation produced usable but incomplete output.</summary>
    public bool IsPartial => Status == ResultStatus.Partial;

    /// <summary>The operation produced nothing usable.</summary>
    public bool IsFailed => Status == ResultStatus.Failed;

    /// <summary>
    /// The output parameter of the operation holds something worth looking at, which is true for both
    /// <see cref="ResultStatus.Success"/> and <see cref="ResultStatus.Partial"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately no implicit conversion to <see langword="bool"/>: whether a partial result counts
    /// as good enough is the caller's decision, and a silent conversion would make it for them.
    /// </remarks>
    public bool HasOutput => Status != ResultStatus.Failed;

    /// <summary>A clean success, with nothing to report.</summary>
    public static OperationResult Success => new(ResultStatus.Success, null);

    /// <summary>
    /// A success that had to skip or approximate part of the input.
    /// </summary>
    /// <param name="message">What was skipped or approximated, and why.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is blank.</exception>
    /// <remarks>
    /// The blank check stays where a null check would not: an empty string is a perfectly good
    /// <see cref="string"/>, so the signature cannot rule it out, and a status carrying no explanation is
    /// the thing this type exists to prevent.
    /// </remarks>
    public static OperationResult Partial(string message)
    {
        Guard.NotBlank(message);
        return new OperationResult(ResultStatus.Partial, message);
    }

    /// <summary>A failure, with no usable output.</summary>
    /// <param name="message">Why the operation could not proceed.</param>
    /// <exception cref="ArgumentException"><paramref name="message"/> is blank.</exception>
    /// <remarks>
    /// The blank check stays where a null check would not: an empty string is a perfectly good
    /// <see cref="string"/>, so the signature cannot rule it out, and a status carrying no explanation is
    /// the thing this type exists to prevent.
    /// </remarks>
    public static OperationResult Failed(string message)
    {
        Guard.NotBlank(message);
        return new OperationResult(ResultStatus.Failed, message);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Message is null ? Status.ToString() : $"{Status}: {Message}";

    /// <inheritdoc/>
    public bool Equals(OperationResult other) =>
        Status == other.Status && Message == other.Message;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is OperationResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Status, Message);

    /// <summary>Compares status and message.</summary>
    public static bool operator ==(OperationResult a, OperationResult b) => a.Equals(b);

    /// <summary>The negation of <c>==</c>.</summary>
    public static bool operator !=(OperationResult a, OperationResult b) => !a.Equals(b);
}
