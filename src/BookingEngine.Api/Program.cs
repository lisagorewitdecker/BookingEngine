using BookingEngine.Api.Common.Exceptions;
using BookingEngine.Api.Common.Persistence;
using BookingEngine.Api.Common.Persistence.Interceptors;
using BookingEngine.Api.Common.Tenancy;
using BookingEngine.Api.Features.Bookings;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Aspire ServiceDefaults (OpenTelemetry, health checks, resilience)
builder.AddServiceDefaults();

// 2. API & RFC 9457 ProblemDetails Error Handling
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

// 3. Core Services, Multi-Tenancy & Validation
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// 4. Persistence (PostgreSQL + Outbox Interceptor + snake_case naming)
builder.Services.AddSingleton<ConvertDomainEventsToOutboxMessagesInterceptor>();

builder.AddNpgsqlDbContext<AppDbContext>("bookingdb", configureDbContextOptions: options =>
{
    options.UseSnakeCaseNamingConvention();
});

// 5. Register Vertical Slice Handlers
builder.Services.AddBookingFeatures();

var app = builder.Build();

// 6. Apply EF Core Migrations automatically in Development
if (app.Environment.IsDevelopment())
{
    await app.ApplyMigrationsAsync();
    app.MapOpenApi();
}

// 7. HTTP Request Pipeline
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseMiddleware<TenantResolutionMiddleware>();

// 8. Map Aspire Health Checks & Vertical Slice Endpoints
app.MapDefaultEndpoints();

var apiGroup = app.MapGroup("/api")
    .RequireTenant();

apiGroup.MapBookingEndpoints();

app.Run();

// Expose Program class for WebApplicationFactory<Program> in IntegrationTests
public partial class Program;
