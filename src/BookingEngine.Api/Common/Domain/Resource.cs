namespace BookingEngine.Api.Common.Domain;

public enum ResourceType
{
    Room,
    Equipment,
    Specialist
}

public sealed class Resource : ITenantEntity
{
    private Resource() { } // Required by EF Core

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public ResourceType ResourceType { get; private set; }
    public int Capacity { get; private set; }
    public int BufferMinutesAfter { get; private set; }
    public bool IsActive { get; private set; }

    public static Resource Create(
        Guid tenantId,
        string name,
        ResourceType resourceType,
        int capacity = 1,
        int bufferMinutesAfter = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (capacity < 1) throw new DomainException("resource.invalid_capacity", "Capacity must be at least 1.");
        if (bufferMinutesAfter < 0) throw new DomainException("resource.invalid_buffer", "Buffer minutes cannot be negative.");

        return new Resource
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            ResourceType = resourceType,
            Capacity = capacity,
            BufferMinutesAfter = bufferMinutesAfter,
            IsActive = true
        };
    }

    public void Deactivate() => IsActive = false;
}