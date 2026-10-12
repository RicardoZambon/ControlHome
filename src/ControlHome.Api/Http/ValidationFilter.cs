using System.Text.Json;
using ControlHome.Application.Common;
using FluentValidation;

namespace ControlHome.Api.Http;

/// <summary>
/// Validates the endpoint's <typeparamref name="T"/> argument with its FluentValidation validator (registered from
/// ControlHome.Application) before the handler runs. Failure: 400 <c>validation.failed</c> with an <c>errors</c>
/// dictionary keyed by camelCase property path (e.g. <c>admin.userName</c>).
/// </summary>
public sealed class ValidationFilter<T> : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            return ValidationProblem(new Dictionary<string, string[]> { [string.Empty] = ["A request body is required."] });
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<IValidator<T>>();
        var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(failure => ToCamelCasePath(failure.PropertyName), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray(), StringComparer.Ordinal);
        return ValidationProblem(errors);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem ValidationProblem(Dictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            detail: "One or more fields are invalid.",
            extensions: new Dictionary<string, object?> { [Problems.CodeExtension] = ErrorCodes.ValidationFailed });

    /// <summary><c>Admin.UserName</c> → <c>admin.userName</c>; <c>Items[0].Name</c> → <c>items[0].name</c>.</summary>
    internal static string ToCamelCasePath(string propertyPath) =>
        string.IsNullOrEmpty(propertyPath)
            ? string.Empty
            : string.Join('.', propertyPath.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}

public static class ValidationEndpointExtensions
{
    /// <summary>Validates the <typeparamref name="T"/> body with FluentValidation (400 <c>validation.failed</c>).</summary>
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder)
        where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();
}
