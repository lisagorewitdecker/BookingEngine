using System.Net;
using BookingEngine.Api.Common.Persistence;
using BookingEngine.Api.Common.Domain;
using BookingEngine.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookingEngine.IntegrationTests.Features.Availability;

[Collection(BookingEngineTestCollection.Name)]
public sealed class GetAvailableSlotsEndpointTests
{
    private readonly BookingEngineWebAppFactory _factory;

    public GetAvailableSlotsEndpointTests(BookingEngineWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAvailableSlots_WithValidResourceAndSchedule_ReturnsSlots()
    {
        // Arrange: Seed tenant, resource, and schedule
        var (tenantId, resourceId, targetDate) = await SeedTenantResourceAndScheduleAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());

        // Act
        var response = await client.GetAsync($"/api/availability/slots?resourceId={resourceId}&date={targetDate:yyyy-MM-dd}");

        // Assert
        Assert.True(response.IsSuccessStatusCode, $"Expected success but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(Guid TenantId, Guid ResourceId, DateOnly TargetDate)> SeedTenantResourceAndScheduleAsync()
    {
        var tenantId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        // Pick an upcoming Monday so the schedule matches
        var now = DateTimeOffset.UtcNow;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;

        var targetDate = DateOnly.FromDateTime(now.Date.AddDays(daysUntilMonday));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Seed tenant
        var uniqueSlug = $"test-{Guid.NewGuid().ToString()[..8]}";
        var tenant = Tenant.Create("Test Tenant", uniqueSlug, "UTC", DateTime.UtcNow);
        typeof(Tenant).GetProperty(nameof(Tenant.Id))!.SetValue(tenant, tenantId);
        dbContext.Tenants.Add(tenant);

        // Seed resource
        var resource = Resource.Create(tenantId, "Test Resource", ResourceType.Room);
        typeof(Resource).GetProperty(nameof(Resource.Id))!.SetValue(resource, resourceId);
        dbContext.Resources.Add(resource);

        // Seed availability schedule for the target day
        var schedule = AvailabilitySchedule.Create(
            tenantId,
            resourceId,
            DayOfWeek.Monday,
            new TimeOnly(9, 0),
            new TimeOnly(17, 0));

        dbContext.AvailabilitySchedules.Add(schedule);
        await dbContext.SaveChangesAsync();

        return (tenantId, resourceId, targetDate);
    }
}
