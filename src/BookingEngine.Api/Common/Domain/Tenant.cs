namespace BookingEngine.Api.Common.Domain;

public sealed class Tenant
{
    private Tenant() { } // Required by EF Core

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string IanaTimezone { get; private set; } = "UTC";
    public DateTime CreatedAtUtc { get; private set; }

    public static Tenant Create(string name, string slug, string ianaTimezone, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        // Validate that the IANA timezone exists on the host OS
        _ = TimeZoneInfo.FindSystemTimeZoneById(ianaTimezone);

        return new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Slug = slug.Trim().ToLowerInvariant(),
            IanaTimezone = ianaTimezone,
            CreatedAtUtc = utcNow
        };
    }
}