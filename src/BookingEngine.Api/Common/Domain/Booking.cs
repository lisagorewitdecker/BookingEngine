namespace BookingEngine.Api.Common.Domain;

public enum BookingStatus
{
    Pending,
    Confirmed,
    Cancelled,
    Completed
}

public sealed class Booking : AggregateRoot, ITenantEntity
{
    private static readonly TimeSpan MinimumLeadTime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumAdvanceHorizon = TimeSpan.FromDays(90);
    private static readonly TimeSpan MinimumCancellationNotice = TimeSpan.FromHours(2);

    private Booking() { } // Required by EF Core

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ResourceId { get; private set; }
    public string CustomerEmail { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTime StartUtc { get; private set; }
    public DateTime EndUtc { get; private set; }
    public BookingStatus Status { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Booking Create(
        Guid tenantId,
        Resource resource,
        string customerEmail,
        string idempotencyKey,
        DateTime requestedStartUtc,
        DateTime requestedEndUtc,
        DateTime utcNow,
        bool autoConfirm = true)
    {
        if (!resource.IsActive)
            throw new DomainException("booking.resource_inactive", "Cannot book an inactive resource.");

        if (requestedEndUtc <= requestedStartUtc)
            throw new DomainException("booking.invalid_range", "Booking end time must be after start time.");

        if (requestedStartUtc < utcNow.Add(MinimumLeadTime))
            throw new DomainException("booking.insufficient_lead_time", "Bookings must be made at least 15 minutes in advance.");

        if (requestedStartUtc > utcNow.Add(MaximumAdvanceHorizon))
            throw new DomainException("booking.exceeds_horizon", "Bookings cannot be scheduled more than 90 days in advance.");

        // Include the resource's cleanup/reset buffer in the stored occupied interval
        var occupiedEndUtc = requestedEndUtc.AddMinutes(resource.BufferMinutesAfter);
        var initialStatus = autoConfirm ? BookingStatus.Confirmed : BookingStatus.Pending;

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ResourceId = resource.Id,
            CustomerEmail = customerEmail.Trim().ToLowerInvariant(),
            IdempotencyKey = idempotencyKey.Trim(),
            StartUtc = requestedStartUtc,
            EndUtc = occupiedEndUtc,
            Status = initialStatus,
            CreatedAtUtc = utcNow
        };

        booking.RaiseDomainEvent(new BookingCreatedEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: utcNow,
            BookingId: booking.Id,
            TenantId: booking.TenantId,
            ResourceId: booking.ResourceId,
            CustomerEmail: booking.CustomerEmail,
            StartUtc: booking.StartUtc,
            EndUtc: booking.EndUtc));

        return booking;
    }

    public void Confirm(DateTime utcNow)
    {
        if (Status != BookingStatus.Pending)
            throw new DomainException("booking.invalid_transition", $"Cannot confirm a booking in '{Status}' status.");

        Status = BookingStatus.Confirmed;

        RaiseDomainEvent(new BookingConfirmedEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: utcNow,
            BookingId: Id,
            TenantId: TenantId,
            ResourceId: ResourceId));
    }

    public void Cancel(string reason, DateTime utcNow)
    {
        if (Status is BookingStatus.Cancelled or BookingStatus.Completed)
            throw new DomainException("booking.terminal_state", $"Cannot cancel a booking that is already '{Status}'.");

        if (Status == BookingStatus.Confirmed && StartUtc - utcNow < MinimumCancellationNotice)
            throw new DomainException("booking.cancellation_window_closed", "Confirmed bookings must be cancelled at least 2 hours before start time.");

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Status = BookingStatus.Cancelled;
        CancellationReason = reason.Trim();

        RaiseDomainEvent(new BookingCancelledEvent(
            EventId: Guid.NewGuid(),
            OccurredAtUtc: utcNow,
            BookingId: Id,
            TenantId: TenantId,
            ResourceId: ResourceId,
            Reason: CancellationReason));
    }
}