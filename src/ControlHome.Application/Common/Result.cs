using System.Diagnostics.CodeAnalysis;

namespace ControlHome.Application.Common;

/// <summary>The outcome of an operation without a value: success, or an <see cref="AppError"/>.</summary>
public sealed class Result
{
    private static readonly Result _success = new(null);

    private Result(AppError? error) => Error = error;

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public AppError? Error { get; }

    public static Result Success() => _success;

    public static Result Failure(AppError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static implicit operator Result(AppError error) => Failure(error);
}

/// <summary>The outcome of an operation that produces a <typeparamref name="T"/>: a value, or an <see cref="AppError"/>.</summary>
public sealed class Result<T>
{
    private Result(T? value, AppError? error)
    {
        Value = value;
        Error = error;
    }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(false, nameof(Value))]
    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    /// <summary>The value when <see cref="IsSuccess"/>; otherwise <c>default</c>.</summary>
    public T? Value { get; }

    public AppError? Error { get; }

    public static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<T>(value, null);
    }

    public static Result<T> Failure(AppError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(AppError error) => Failure(error);
}
