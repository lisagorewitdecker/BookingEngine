using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace BookingEngine.Api.Features.Bookings.CreateBooking;

public static class CreateBookingEndpoint
{
    public static RouteGroupBuilder MapCreateBooking(this RouteGroupBuilder group)
    {
        group.MapPost("/", HandleAsync)
            .WithName("CreateBooking")
            .WithSummary("Creates a new resource booking idempotently")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return group;
    }

    private static async Task<Results<Created<CreateBookingResponse>, Ok<CreateBookingResponse>, ProblemHttpResult>> HandleAsync(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateBookingRequest request,
        CreateBookingHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return TypedResults.Problem(
                title: "Missing Idempotency Key",
                detail: "Include a unique 'Idempotency-Key' header to safely retry booking requests.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new CreateBookingCommand(
            request.ResourceId,
            request.CustomerEmail,
            idempotencyKey,
            request.StartUtc,
            request.EndUtc);

        var result = await handler.HandleAsync(command, cancellationToken);

        return result.IsReplay
            ? TypedResults.Ok(result.Booking)
            : TypedResults.Created($"/api/bookings/{result.Booking.BookingId}", result.Booking);
    }
}