using BookingEngine.Api.Common.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BookingEngine.Api.Common.Exceptions;

public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, detail, errorCode) = MapException(exception);

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                exception,
                "Unhandled exception occurred while processing {Method} {Path}",
                httpContext.Request.Method,
                httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(
                "Handled domain/conflict exception ({ErrorCode}) on {Method}",
                errorCode ?? "none",
                httpContext.Request.Method);
                httpContext.Request.Method,
                httpContext.Request.Path,
                exception.Message.ReplaceLineEndings(" "));
        }

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{statusCode}",
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}"
        };

        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            problemDetails.Extensions["code"] = errorCode;
        }

        // Include FluentValidation field errors if thrown directly
        if (exception is ValidationException validationException)
        {
            problemDetails.Extensions["errors"] = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray());
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails
        });
    }

    private static (int StatusCode, string Title, string Detail, string? ErrorCode) MapException(Exception exception)
    {
        return exception switch
        {
            // 1. PostgreSQL Double-Booking Exclusion Constraint Violation (23P01)
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } } =>
                (
                    StatusCodes.Status409Conflict,
                    "Booking Slot Unavailable",
                    "The selected resource was booked by another user for an overlapping time window.",
                    "booking.slot_conflict"
                ),

            // 2. PostgreSQL Unique Constraint Violation (23505) - e.g., simultaneous identical Idempotency-Key race
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pgEx } =>
                (
                    StatusCodes.Status409Conflict,
                    "Duplicate Request Conflict",
                    pgEx.ConstraintName?.Contains("idempotency_key", StringComparison.OrdinalIgnoreCase) == true
                        ? "A request with this Idempotency-Key is already being processed or completed."
                        : "A record with the same unique identifier already exists.",
                    "persistence.unique_violation"
                ),

            // 3. EF Core Optimistic Concurrency Conflict (xmin mismatch)
            DbUpdateConcurrencyException =>
                (
                    StatusCodes.Status409Conflict,
                    "Resource Modified Concurrently",
                    "The record was modified by another request. Refresh the latest state and retry.",
                    "persistence.concurrency_conflict"
                ),

            // 4. Explicit Domain Rule Violations (e.g., cancellation < 2 hrs, inactive resource)
            DomainException domainEx =>
                (
                    MapDomainStatusCode(domainEx.Code),
                    "Business Rule Violation",
                    domainEx.Message,
                    domainEx.Code
                ),

            // 5. FluentValidation Errors
            ValidationException =>
                (
                    StatusCodes.Status400BadRequest,
                    "Validation Failed",
                    "One or more request validation rules failed.",
                    "request.validation_failed"
                ),

            // 6. Fallback for unexpected errors (never leak raw exception messages to clients)
            _ =>
                (
                    StatusCodes.Status500InternalServerError,
                    "Internal Server Error",
                    "An unexpected error occurred while processing your request.",
                    "server.unexpected_error"
                )
        };
    }

    private static int MapDomainStatusCode(string code) => code switch
    {
        "booking.invalid_transition" or "booking.terminal_state" or "booking.cancellation_window_closed"
            => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity
    };
}