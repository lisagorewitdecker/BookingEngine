using BookingEngine.Api.Common.Domain;
using BookingEngine.Api.Common.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BookingEngine.Api.Common.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenantContext) : DbContext(options)
{
    // Evaluated dynamically per query by EF Core (never cached across requests)
    private Guid? CurrentTenantId => tenantContext.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<AvailabilitySchedule> AvailabilitySchedules => Set<AvailabilitySchedule>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Tenant Configuration
        modelBuilder.Entity<Tenant>(builder =>
        {
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Name).HasMaxLength(120).IsRequired();
            builder.Property(t => t.Slug).HasMaxLength(60).IsRequired();
            builder.HasIndex(t => t.Slug).IsUnique();
            builder.Property(t => t.IanaTimezone).HasMaxLength(64).IsRequired();
        });

        // Resource Configuration
        modelBuilder.Entity<Resource>(builder =>
        {
            builder.HasKey(r => r.Id);
            builder.Property(r => r.Name).HasMaxLength(120).IsRequired();
            builder.Property(r => r.ResourceType).HasConversion<string>().HasMaxLength(32);
            builder.Property<uint>("xmin").IsRowVersion(); // Optimistic concurrency
            builder.HasIndex(r => r.TenantId);

            builder.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        // AvailabilitySchedule Configuration
        modelBuilder.Entity<AvailabilitySchedule>(builder =>
        {
            builder.HasKey(s => s.Id);
            builder.HasIndex(s => new { s.TenantId, s.ResourceId, s.DayOfWeek });

            builder.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        // Booking Configuration
        modelBuilder.Entity<Booking>(builder =>
        {
            builder.HasKey(b => b.Id);
            builder.Property(b => b.CustomerEmail).HasMaxLength(255).IsRequired();
            builder.Property(b => b.IdempotencyKey).HasMaxLength(128).IsRequired();
            builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(24);
            builder.Property<uint>("xmin").IsRowVersion(); // Optimistic concurrency

            // Prevent duplicate submissions per tenant for the same Idempotency-Key
            builder.HasIndex(b => new { b.TenantId, b.IdempotencyKey }).IsUnique();
            
            // Prevent overlapping bookings for the same resource
            builder.HasIndex(b => new { b.TenantId, b.ResourceId, b.StartUtc, b.EndUtc }).IsUnique();

            builder.HasQueryFilter(b => b.TenantId == CurrentTenantId);
        });

        // OutboxMessage Configuration
        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.HasKey(o => o.Id);
            builder.Property(o => o.EventType).HasMaxLength(128).IsRequired();
            builder.Property(o => o.Payload).HasColumnType("jsonb").IsRequired();

            // Filtered index keeps background worker polling fast as the table grows
            builder.HasIndex(o => o.OccurredAtUtc)
                .HasFilter("processed_at_utc IS NULL");
        });
    }
}