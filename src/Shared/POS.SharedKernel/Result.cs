namespace POS.SharedKernel;

/// <summary>
/// The outcome of an operation that can fail in an expected way.
/// </summary>
/// <remarks>
/// Used for failures that are part of the contract: validation, not-found,
/// business rule violations. Genuine faults (database unreachable, null
/// dereference) remain exceptions — they are not something a caller can act on,
/// and threading them through every signature adds noise without benefit.
/// </remarks>
public readonly struct Result
{
    private readonly Error? _error;

    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        _error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// The failure reason. Throws if the result succeeded.
    /// </summary>
    /// <remarks>
    /// Non-nullable by design, mirroring <see cref="Result{TValue}.Value"/>. A failed
    /// result always has an error, and typing it as <c>Error?</c> forced a null-check
    /// or a <c>!</c> at every propagation site for an invariant that cannot be
    /// violated.
    /// </remarks>
    public Error Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Cannot access Error of a successful Result.");

    public static Result Success() => new(true, null);
    public static Result Failure(Error error) => new(false, error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <inheritdoc cref="Result"/>
public readonly struct Result<TValue>
{
    private readonly TValue? _value;
    private readonly Error? _error;

    private Result(bool isSuccess, TValue? value, Error? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    /// <inheritdoc cref="Result.Error"/>
    public Error Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Cannot access Error of a successful Result.");

    /// <summary>Throws if accessed on a failed result. Check <see cref="IsSuccess"/> first.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access Value of a failed Result.");

    public static Result<TValue> Success(TValue value) => new(true, value, null);
    public static Result<TValue> Failure(Error error) => new(false, default, error);

    public static implicit operator Result<TValue>(TValue value) => Success(value);
    public static implicit operator Result<TValue>(Error error) => Failure(error);

    /// <summary>
    /// Forces both branches to be handled. Prefer this to an <c>if (IsSuccess)</c>
    /// chain — the compiler will not let you forget the failure case.
    /// </summary>
    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<Error, TOut> onFailure)
        => IsSuccess ? onSuccess(_value!) : onFailure(Error);
}
