namespace BookingEngine.Api.Common.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; }
    public DateTime? ProcessedAtUtc { get; set; }
    public string? Error { get; set; }
}