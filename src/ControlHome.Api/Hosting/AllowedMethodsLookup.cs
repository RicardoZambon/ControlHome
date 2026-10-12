using Microsoft.AspNetCore.Routing.Template;

namespace ControlHome.Api.Hosting;

/// <summary>
/// For a request that only a catch-all fallback matched: the HTTP methods that the app's method-specific endpoints accept for
/// its path (route constraints included), so the fallback can answer 405 with <c>Allow</c> instead of 404. The endpoint list
/// is read once, on first use (all endpoints are mapped by then).
/// </summary>
internal sealed class AllowedMethodsLookup(IServiceProvider services)
{
    private readonly Lazy<Candidate[]> _candidates = new(() => Load(services));

    /// <summary>
    /// The methods, sorted, without duplicates; empty when no endpoint has this path (or, defensively, when one of them
    /// accepts the request's own method).
    /// </summary>
    public IReadOnlyList<string> For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var methods = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in _candidates.Value)
        {
            var values = new RouteValueDictionary();
            if (candidate.Matcher.TryMatch(context.Request.Path, values)
                && candidate.Constraints.All(c => c.Constraint.Match(context, route: null, c.Name, values, RouteDirection.IncomingRequest)))
            {
                methods.UnionWith(candidate.Methods);
            }
        }

        return methods.Contains(context.Request.Method) ? [] : [.. methods];
    }

    private static Candidate[] Load(IServiceProvider services)
    {
        var policies = services.GetRequiredService<ParameterPolicyFactory>();
        return services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Order != int.MaxValue) // fallbacks (the SPA one is GET/HEAD for every path)
            .Select(endpoint => (endpoint.RoutePattern, Methods: endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods))
            .Where(e => e.Methods is { Count: > 0 })
            .Select(e => new Candidate(
                new TemplateMatcher(new RouteTemplate(e.RoutePattern), new RouteValueDictionary(e.RoutePattern.Defaults)),
                [
                    .. e.RoutePattern.Parameters.SelectMany(parameter => parameter.ParameterPolicies
                        .Select(reference => policies.Create(parameter, reference))
                        .OfType<IRouteConstraint>()
                        .Select(constraint => (parameter.Name, constraint))),
                ],
                e.Methods!))
            .ToArray();
    }

    private sealed record Candidate(
        TemplateMatcher Matcher,
        (string Name, IRouteConstraint Constraint)[] Constraints,
        IReadOnlyList<string> Methods);
}
