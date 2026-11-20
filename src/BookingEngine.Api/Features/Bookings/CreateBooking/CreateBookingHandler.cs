using BookingEngine.Api.Common.Domain;
using BookingEngine.Api.Common.Persistence;
using BookingEngine.Api.Common.Tenancy;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BookingEngine.Api.Features.Bookings.CreateBooking;

public sealed record CreateBookingResult(CreateBookingResponse Booking, bool IsReplay);

public sealed class CreateBookingHandler(
    AppDbContext dbContext,
    ITenantContext tenantContext,
    IValidator<CreateBookingCommand> validator,
    TimeProvider timeProvider)
{
    public async Task<CreateBookingResult> HandleAsync(
        CreateBookingCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context must be resolved before handling bookings.");

        var normalizedKey = command.IdempotencyKey.Trim();
        var normalizedEmail = command.CustomerEmail.Trim().ToLowerInvariant();

        // 1. Idempotency Check (scoped automatically to current tenant via Global Query Filter)
        var existingBooking = await dbContext.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.IdempotencyKey == normalizedKey, cancellationToken);

        if (existingBooking is not null)
        {
            // Ensure the retry payload matches the original request
            var isSamePayload =
                existingBooking.ResourceId == command.ResourceId &&
                existingBooking.CustomerEmail == normalizedEmail &&
                existingBooking.StartUtc == command.StartUtc;

            if (!isSamePayload)
            {
                throw new DomainException(
                    "booking.idempotency_payload_mismatch",
                    "This Idempotency-Key was already used with different booking parameters.");
            }

            return new CreateBookingResult(CreateBookingResponse.FromEntity(existingBooking), IsReplay: true);
        }

        // 2. Load Resource (tenant-scoped via Global Query Filter)
        var resource = await dbContext.Resources
            .FirstOrDefaultAsync(r => r.Id == command.ResourceId, cancellationToken)
            ?? throw new DomainException("booking.resource_not_found", $"Resource '{command.ResourceId}' was not found.");

        // 3. Evaluate Tenant-Local Working Hours (DST-safe via IANA timezone)
        await EnsureWithinResourceScheduleAsync(
            resource,
            command.StartUtc,
            command.EndUtc,
            tenantContext.IanaTimezone!,
            cancellationToken);

        // 4. Create Aggregate (enforces lead time, horizon, buffer, and raises BookingCreatedEvent)
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        var booking = Booking.Create(
            tenantId: tenantId,
            resource: resource,
            customerEmail: normalizedEmail,
            idempotencyKey: normalizedKey,
            requestedStartUtc: command.StartUtc,
            requestedEndUtc: command.EndUtc,
            utcNow: utcNow);

        dbContext.Bookings.Add(booking);

        // Commits Booking + OutboxMessage in one atomic transaction.
        // If another request raced for the same window, PostgreSQL throws 23P01 (caught by GlobalExceptionHandler).
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreateBookingResult(CreateBookingResponse.FromEntity(booking), IsReplay: false);
    }

    private async Task EnsureWithinResourceScheduleAsync(
        Resource resource,
        DateTime startUtc,
        DateTime endUtc,
        string ianaTimezone,
        CancellationToken cancellationToken)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(ianaTimezone);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(endUtc.AddMinutes(resource.BufferMinutesAfter), tz);

        // Prevent bookings spanning across midnight into a different local day
        if (localStart.Date != localEnd.Date)
        {
            throw new DomainException(
                "booking.multi_day_not_supported",
                "Bookings (including cleanup buffer) must start and end on the same local calendar day.");
        }

        var dayOfWeek = localStart.DayOfWeek;
        var requestedStartTime = TimeOnly.FromDateTime(localStart);
        var requestedEndTime = TimeOnly.FromDateTime(localEnd);

        var fitsSchedule = await dbContext.AvailabilitySchedules
            .AsNoTracking()
            .AnyAsync(s =>
                s.ResourceId == resource.Id &&
                s.DayOfWeek == dayOfWeek &&
                s.StartLocalTime <= requestedStartTime &&
                s.EndLocalTime >= requestedEndTime,
                cancellationToken);

        if (!fitsSchedule)
        {
            throw new DomainException(
                "booking.outside_operating_hours",
                $"The requested window falls outside the resource's {dayOfWeek} availability schedule ({ianaTimezone}).");
        }
    }
}