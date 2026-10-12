using ControlHome.Api.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace ControlHome.Api.Hosting;

/// <summary>
/// OpenAPI 3.1 document. Generated at build time into <c>web/packages/api-client/openapi.json</c> (the TypeScript client
/// is generated from it); served live (with the Scalar UI) only in Development.
/// </summary>
public static class OpenApiSetup
{
    public static IServiceCollection AddControlHomeOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "ControlHome API",
                    Version = "v1",
                    Description = "Same-origin API of the ControlHome web app. Mutating requests must send an Origin header equal "
                        + "to the app's origin and the header 'X-ControlHome: 1'. Errors are RFC 7807 ProblemDetails with a "
                        + "stable 'code'.",
                };

                // Same-origin only: no server URLs (they would also depend on the machine that built the document).
                document.Servers = [];
                return Task.CompletedTask;
            });

            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (typeof(ProblemDetails).IsAssignableFrom(context.JsonTypeInfo.Type))
                {
                    schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
                    schema.Properties[Problems.CodeExtension] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "Stable, dotted error code (e.g. auth.invalid_credentials); the UI translates errors.<code>.",
                    };
                    schema.Properties[ProblemDetailsSetup.TraceIdExtension] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "Correlates the response with the server log.",
                    };
                    schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
                    schema.Required.Add(Problems.CodeExtension);
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>Development only: <c>/openapi/v1.json</c> and the Scalar UI at <c>/scalar</c>.</summary>
    public static WebApplication MapControlHomeOpenApi(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }

        return app;
    }
}
