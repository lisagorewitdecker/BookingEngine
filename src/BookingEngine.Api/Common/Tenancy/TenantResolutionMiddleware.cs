using BookingEngine.Api.Common.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookingEngine.Api.Common.Tenancy;

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string TenantHeaderName = "X-Tenant-Id";

    public async Task InvokeAsync(
        HttpContext httpContext,
        ITenantContext tenantContext,
        AppDbContext dbContext)
    {
        var rawTenantId = ExtractTenantId(httpContext.Request);

        if (!string.IsNullOrWhiteSpace(rawTenantId))
        {
            if (!Guid.TryParse(rawTenantId, out var parsedTenantId) || parsedTenantId == Guid.Empty)
            {
                await WriteProblemAsync(
                    httpContext,
                    StatusCodes.Status400BadRequest,
                    "Invalid Tenant Identifier",
                    $"Header '{TenantHeaderName}' must be a valid non-empty GUID.");
                return;
            }

            // Tenants table has no global query filter, so this safely looks up tenant metadata
            var tenant = await dbContext.Tenants
                .AsNoTracking()
                .Where(t => t.Id == parsedTenantId)
                .Select(t => new { t.Id, t.IanaTimezone })
                .FirstOrDefaultAsync(httpContext.RequestAborted);

            if (tenant is null)
            {
                await WriteProblemAsync(
                    httpContext,
                    StatusCodes.Status404NotFound,
                    "Tenant Not Found",
                    $"No tenant exists with ID '{parsedTenantId}'.");
                return;
            }

            tenantContext.SetTenant(tenant.Id, tenant.IanaTimezone);
        }

        await next(httpContext);
    }

    private static string? ExtractTenantId(HttpRequest request)
    {
        if (request.Headers.TryGetValue(TenantHeaderName, out var headerValues))
        {
            return headerValues.FirstOrDefault();
        }

        // Fallback for SignalR WebSocket connections where custom headers aren't supported by browsers
        if (request.Query.TryGetValue("tenantId", out var queryValues))
        {
            return queryValues.FirstOrDefault();
        }

        return null;
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = $"{context.Request.Method} {context.Request.Path}"
        });
    }
}