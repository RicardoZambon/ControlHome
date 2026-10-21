using System.Text.Json;
using ControlHome.Api.Http;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Domain.Capabilities;
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
    /// <summary>The payload types of the <c>/hubs/home</c> messages, added to the document's components.</summary>
    internal static readonly Type[] HubMessageTypes =
    [
        typeof(StateDelta),
        typeof(AvailabilityChangedDto),
        typeof(IntegrationStatusChangedDto),
        typeof(CommandStatusDto),
        typeof(SystemStatusDto),
    ];

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

            // The hub's message payloads are not part of any REST operation: add them to the components so the generated
            // TypeScript client has their types too (see HomeHubMessages for the method names).
            options.AddDocumentTransformer(async (document, context, cancellationToken) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
                foreach (var type in HubMessageTypes)
                {
                    if (document.Components.Schemas.ContainsKey(type.Name))
                    {
                        continue;
                    }

                    var schema = await context.GetOrCreateSchemaAsync(type, null, cancellationToken);

                    // Members whose type already is a component (e.g. StateValue) reference it instead of repeating it inline.
                    foreach (var property in type.GetProperties())
                    {
                        var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        if (schema.Properties is { } properties && properties.ContainsKey(name)
                            && document.Components.Schemas.ContainsKey(property.PropertyType.Name))
                        {
                            properties[name] = new OpenApiSchemaReference(property.PropertyType.Name, document);
                        }
                    }

                    document.Components.Schemas[type.Name] = schema;
                }
            });

            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type == typeof(StateValue))
                {
                    // On the wire only the member named by `kind` is present (the others are left out, never null):
                    // `{ kind, b?, n?, s?, j? }`.
                    schema.Required = new HashSet<string>(StringComparer.Ordinal) { "kind" };
                    foreach (var member in schema.Properties?.Values.OfType<OpenApiSchema>() ?? [])
                    {
                        if (member.Type is { } type)
                        {
                            member.Type = type & ~JsonSchemaType.Null;
                        }
                    }
                }

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
