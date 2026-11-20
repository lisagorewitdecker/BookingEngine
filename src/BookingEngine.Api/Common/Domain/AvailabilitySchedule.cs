namespace BookingEngine.Api.Common.Domain;

public sealed class AvailabilitySchedule : ITenantEntity
{
    private AvailabilitySchedule() { } // Required by EF Core

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ResourceId { get; private set; }
    public DayOfWeek DayOfWeek { get; private set; }
    public TimeOnly StartLocalTime { get; private set; }
    public TimeOnly EndLocalTime { get; private set; }

    public static AvailabilitySchedule Create(
        Guid tenantId,
        Guid resourceId,
        DayOfWeek dayOfWeek,
        TimeOnly startLocalTime,
        TimeOnly endLocalTime)
    {
        if (endLocalTime <= startLocalTime)
        {
            throw new DomainException(
                "schedule.invalid_window",
                "End local time must be after start local time.");
        }

        return new AvailabilitySchedule
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ResourceId = resourceId,
            DayOfWeek = dayOfWeek,
            StartLocalTime = startLocalTime,
            EndLocalTime = endLocalTime
        };
    }
}
