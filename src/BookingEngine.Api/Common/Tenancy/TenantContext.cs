namespace BookingEngine.Api.Common.Tenancy;

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? IanaTimezone { get; private set; }

    public void SetTenant(Guid tenantId, string ianaTimezone)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant ID cannot be empty.", nameof(tenantId));

        ArgumentException.ThrowIfNullOrWhiteSpace(ianaTimezone);

        TenantId = tenantId;
        IanaTimezone = ianaTimezone;
    }
}