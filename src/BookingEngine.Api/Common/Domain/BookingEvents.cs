namespace BookingEngine.Api.Common.Domain;

public sealed record BookingCreatedEvent(
    Guid EventId,
    DateTime OccurredAtUtc,
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId,
    string CustomerEmail,
    DateTime StartUtc,
    DateTime EndUtc) : IDomainEvent;

public sealed record BookingConfirmedEvent(
    Guid EventId,
    DateTime OccurredAtUtc,
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId) : IDomainEvent;

public sealed record BookingCancelledEvent(
    Guid EventId,
    DateTime OccurredAtUtc,
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId,
    string Reason) : IDomainEvent;