using ControlHome.Application.Common;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Capabilities;

/// <summary>
/// Turns <see cref="CommandValidator"/> results into the API's error shape: 400 <c>commands.invalid_arguments</c> with
/// field errors <c>errors["arguments.&lt;key&gt;"]</c> (or <c>errors["arguments"]</c> when the arguments are not an
/// object) carrying the <c>commands.argument_*</c> codes.
/// </summary>
public static class CommandArgumentErrors
{
    public const string ArgumentsPath = "arguments";

    public static string CodeFor(ValueErrorKind kind) => kind switch
    {
        ValueErrorKind.Required => ErrorCodes.CommandsArgumentRequired,
        ValueErrorKind.Unknown => ErrorCodes.CommandsArgumentUnknown,
        ValueErrorKind.InvalidType => ErrorCodes.CommandsArgumentInvalidType,
        ValueErrorKind.OutOfRange => ErrorCodes.CommandsArgumentOutOfRange,
        ValueErrorKind.NotOnStep => ErrorCodes.CommandsArgumentNotOnStep,
        ValueErrorKind.NotAllowed => ErrorCodes.CommandsArgumentNotAllowed,
        ValueErrorKind.TooLong => ErrorCodes.CommandsArgumentTooLong,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The 400 error for an invalid result.</summary>
    /// <exception cref="ArgumentException">The result is valid.</exception>
    public static AppError ToAppError(ArgumentValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsValid)
        {
            throw new ArgumentException("The arguments are valid.", nameof(result));
        }

        var errors = result.Errors
            .GroupBy(e => e.Argument is null ? ArgumentsPath : $"{ArgumentsPath}.{e.Argument}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => CodeFor(e.Kind)).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return AppError.Validation(
            ErrorCodes.CommandsInvalidArguments,
            "The command's arguments are invalid.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { [AppError.ErrorsExtension] = errors });
    }
}
