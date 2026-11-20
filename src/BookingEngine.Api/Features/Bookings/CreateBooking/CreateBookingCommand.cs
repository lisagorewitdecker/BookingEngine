using BookingEngine.Api.Common.Domain;

namespace BookingEngine.Api.Features.Bookings.CreateBooking;

public sealed record CreateBookingRequest(
    Guid ResourceId,
    string CustomerEmail,
    DateTime StartUtc,
    DateTime EndUtc);

public sealed record CreateBookingCommand(
    Guid ResourceId,
    string CustomerEmail,
    string IdempotencyKey,
    DateTime StartUtc,
    DateTime EndUtc);

public sealed record CreateBookingResponse(
    Guid BookingId,
    Guid ResourceId,
    string CustomerEmail,
    DateTime StartUtc,
    DateTime OccupiedEndUtc,
    BookingStatus Status,
    DateTime CreatedAtUtc)
{
    public static CreateBookingResponse FromEntity(Booking booking) => new(
        booking.Id,
        booking.ResourceId,
        booking.CustomerEmail,
        booking.StartUtc,
        booking.EndUtc,
        booking.Status,
        booking.CreatedAtUtc);
}