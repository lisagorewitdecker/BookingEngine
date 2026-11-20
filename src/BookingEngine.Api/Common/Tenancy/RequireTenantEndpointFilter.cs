namespace BookingEngine.Api.Common.Tenancy;

public sealed class RequireTenantEndpointFilter(ITenantContext tenantContext) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!tenantContext.IsResolved)
        {
            return TypedResults.Problem(
                title: "Missing Tenant Header",
                detail: $"A valid '{TenantResolutionMiddleware.TenantHeaderName}' header is required to access this endpoint.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await next(context);
    }
}

public static class TenantEndpointExtensions
{
    public static RouteGroupBuilder RequireTenant(this RouteGroupBuilder group)
    {
        return group.AddEndpointFilter<RequireTenantEndpointFilter>();
    }
}