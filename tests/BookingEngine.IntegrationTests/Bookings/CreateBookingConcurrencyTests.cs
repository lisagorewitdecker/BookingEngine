using System.Net;
using System.Net.Http.Json;
using BookingEngine.Api.Common.Persistence;
using BookingEngine.Api.Common.Domain;
using BookingEngine.Api.Common.Tenancy;
using BookingEngine.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookingEngine.IntegrationTests.Bookings;

[Collection(BookingEngineTestCollection.Name)]
public sealed class CreateBookingConcurrencyTests
{
    private readonly BookingEngineWebAppFactory _factory;

    public CreateBookingConcurrencyTests(BookingEngineWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateBooking_When10ConcurrentRequestsTargetSameSlot_AllowsOnlyOneAndReturnsConflictForNine()
    {
        // Arrange: Seed an isolated tenant, resource, and open availability window
        var (tenantId, resourceId, slotStart, slotEnd) = await SeedTenantAndResourceAsync();

        var tasks = Enumerable.Range(0, 10).Select(index =>
        {
            var client = CreateTenantClient(tenantId, idempotencyKey: Guid.NewGuid().ToString());
            var payload = new
            {
                ResourceId = resourceId,
                CustomerEmail = $"cust-{index}@example.com",
                StartUtc = slotStart.UtcDateTime,
                EndUtc = slotEnd.UtcDateTime
            };

            return client.PostAsJsonAsync("/api/bookings", payload);
        });

        // Act: Fire all 10 requests simultaneously
        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Debug: Log error details if validation fails
        if (!responses.Any(r => r.StatusCode == HttpStatusCode.Created))
        {
            var errorBody = await responses[0].Content.ReadAsStringAsync();
            throw new Exception($"No 201 Created response. First response: {responses[0].StatusCode}\n{errorBody}");
        }

        // Assert: Exactly one succeeds (201 Created) and nine fail with 409 Conflict
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var savedCount = await dbContext.Bookings
            .IgnoreQueryFilters()
            .CountAsync(b => b.TenantId == tenantId && b.ResourceId == resourceId);

        Assert.Equal(1, savedCount);
    }

    [Fact]
    public async Task CreateBooking_WhenRetriedWithSameIdempotencyKey_Returns200OkWithoutDuplicate()
    {
        // Arrange
        var (tenantId, resourceId, slotStart, slotEnd) = await SeedTenantAndResourceAsync();
        var idempotencyKey = Guid.NewGuid().ToString();
        var client = CreateTenantClient(tenantId, idempotencyKey);

        var payload = new
        {
            ResourceId = resourceId,
            CustomerEmail = "cust-idempotent-1@example.com",
            StartUtc = slotStart.UtcDateTime,
            EndUtc = slotEnd.UtcDateTime
        };

        // Act: Initial creation + retry with identical Idempotency-Key
        HttpResponseMessage firstResponse = await client.PostAsJsonAsync("/api/bookings", payload);
        HttpResponseMessage retryResponse = await client.PostAsJsonAsync("/api/bookings", payload);

        // Assert: First call creates (201), retry replays cached response (200) with no second row
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);

        var firstBody = await firstResponse.Content.ReadFromJsonAsync<BookingCreatedDto>();
        var retryBody = await retryResponse.Content.ReadFromJsonAsync<BookingCreatedDto>();

        Assert.NotNull(firstBody);
        Assert.NotNull(retryBody);
        Assert.Equal(firstBody.Id, retryBody.Id);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var savedCount = await dbContext.Bookings
            .IgnoreQueryFilters()
            .CountAsync(b => b.TenantId == tenantId && b.ResourceId == resourceId);

        Assert.Equal(1, savedCount);
    }

    private HttpClient CreateTenantClient(Guid tenantId, string idempotencyKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        return client;
    }

    private async Task<(Guid TenantId, Guid ResourceId, DateTimeOffset SlotStart, DateTimeOffset SlotEnd)> SeedTenantAndResourceAsync()
    {
        var tenantId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        // Pick an upcoming Monday at 10:00 UTC so DayOfWeek matches deterministically
        var now = DateTimeOffset.UtcNow;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;

        var targetDate = now.Date.AddDays(daysUntilMonday);
        var slotStart = new DateTimeOffset(targetDate.AddHours(10), TimeSpan.Zero);
        var slotEnd = slotStart.AddHours(1);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Seed tenant using factory with unique slug
        var uniqueSlug = $"test-{Guid.NewGuid().ToString()[..8]}";
        var tenant = Tenant.Create("Test Tenant", uniqueSlug, "UTC", DateTime.UtcNow);
        // Override the auto-generated ID for reproducibility in tests
        typeof(Tenant).GetProperty(nameof(Tenant.Id))!.SetValue(tenant, tenantId);
        dbContext.Tenants.Add(tenant);

        // Seed resource using factory
        var resource = Resource.Create(tenantId, "Test Resource", ResourceType.Room);
        // Override the auto-generated ID for reproducibility in tests
        typeof(Resource).GetProperty(nameof(Resource.Id))!.SetValue(resource, resourceId);
        dbContext.Resources.Add(resource);

        var schedule = AvailabilitySchedule.Create(
            tenantId,
            resourceId,
            DayOfWeek.Monday,
            new TimeOnly(9, 0),
            new TimeOnly(17, 0));

        dbContext.AvailabilitySchedules.Add(schedule);
        await dbContext.SaveChangesAsync();

        // Verify resource was saved
        var savedResource = await dbContext.Resources
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == resourceId);
        if (savedResource == null)
        {
            throw new Exception($"Failed to seed resource {resourceId}");
        }

        return (tenantId, resourceId, slotStart, slotEnd);
    }

    private sealed record BookingCreatedDto(Guid Id);
}