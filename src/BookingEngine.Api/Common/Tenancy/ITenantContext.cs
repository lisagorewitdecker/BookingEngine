namespace BookingEngine.Api.Common.Tenancy;

public interface ITenantContext
{
    Guid? TenantId { get; }
    string? IanaTimezone { get; }
    bool IsResolved => TenantId.HasValue;

    void SetTenant(Guid tenantId, string ianaTimezone);
}