namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Routing;

/// <summary>
///     Checks if the tenant id is a valid string.
/// </summary>
internal class TenantIdRouteConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        // check nulls
        var isMatch = values.TryGetValue(routeKey, out var value) && value != null;
        if (isMatch)
        {
            // Idempotent assignment: route matching evaluates constraints for every candidate endpoint,
            // so Match can run several times per request (e.g. a path that matches both a literal action
            // like `tenants/lifecycle` and the `tenants/{id}` template). Dictionary.Add threw
            // "same key already added" on the second evaluation (AB#4348 Phase 4).
            if (httpContext != null)
            {
                httpContext.Items["d"] = value;
            }
        }

        return isMatch;
    }
}