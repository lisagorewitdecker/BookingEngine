using BookingEngine.Api.Common.Domain;
using BookingEngine.Api.Features.Availability.GetAvailableSlots;
using Xunit;

namespace BookingEngine.UnitTests.Features.Availability;

public sealed class SlotCalculatorTests
{
    private readonly SlotCalculator _calculator = new();

    private static readonly DateOnly MondayDate = new(2026, 11, 23); // Monday
    private static readonly TimeSpan ThirtyMinutes = TimeSpan.FromMinutes(30);

    [Fact]
    public void Calculate_WhenBookingPartiallyOverlapsWindow_ExcludesOverlappingSlotsAndKeepsFreeSlots()
    {
        // Window: 09:00 - 11:00 UTC -> candidate 30m slots:
        // 09:00-09:30, 09:30-10:00, 10:00-10:30, 10:30-11:00
        var schedules = new[]
        {
            AvailabilitySchedule.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DayOfWeek.Monday,
                new TimeOnly(9, 0),
                new TimeOnly(11, 0))
        };

        // Booking from 09:45-10:15 blocks slots 09:30-10:00 and 10:00-10:30
        var booking = new TestBookingBuilder()
            .WithStartUtc(new DateTime(2026, 11, 23, 9, 45, 0, DateTimeKind.Utc))
            .WithEndUtc(new DateTime(2026, 11, 23, 10, 15, 0, DateTimeKind.Utc))
            .Build();

        IReadOnlyList<TimeSlotDto> slots = _calculator.Calculate(
            MondayDate,
            30,
            schedules,
            [booking]);

        // Available slots: 09:00-09:30 (before booking), 10:15-10:45, 10:45-11:15 (but 11:15 exceeds window)
        // So we get: 09:00-09:30, 10:15-10:45
        Assert.Collection(
            slots,
            slot =>
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero), slot.StartTime);
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 30, 0, TimeSpan.Zero), slot.EndTime);
            },
            slot =>
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 15, 0, TimeSpan.Zero), slot.StartTime);
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 45, 0, TimeSpan.Zero), slot.EndTime);
            });
    }

    [Fact]
    public void Calculate_WhenBookingsAreBackToBack_ProducesContiguousNonOverlappingSlotsWithoutGaps()
    {
        // Window: 09:00 - 10:30 UTC with 30m slots
        var schedules = new[]
        {
            AvailabilitySchedule.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DayOfWeek.Monday,
                new TimeOnly(9, 0),
                new TimeOnly(10, 30))
        };

        // Back-to-back busy blocks: 09:00-09:30 followed immediately by 09:30-10:00
        var bookings = new[]
        {
            new TestBookingBuilder()
                .WithStartUtc(new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc))
                .WithEndUtc(new DateTime(2026, 11, 23, 9, 30, 0, DateTimeKind.Utc))
                .Build(),
            new TestBookingBuilder()
                .WithStartUtc(new DateTime(2026, 11, 23, 9, 30, 0, DateTimeKind.Utc))
                .WithEndUtc(new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc))
                .Build()
        };

        IReadOnlyList<TimeSlotDto> slots = _calculator.Calculate(
            MondayDate,
            30,
            schedules,
            bookings);

        // Only 10:00-10:30 remains open
        TimeSlotDto onlySlot = Assert.Single(slots);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), onlySlot.StartTime);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 30, 0, TimeSpan.Zero), onlySlot.EndTime);
    }

    [Fact]
    public void Calculate_WhenRemainderIsShorterThanSlotDuration_DropsPartialTailWindow()
    {
        // Window: 09:00 - 10:15 UTC with 30m slots -> candidate slots would be:
        // 09:00-09:30, 09:30-10:00, 10:00-10:30 (exceeds window, so only 09:00-10:00)
        var schedules = new[]
        {
            AvailabilitySchedule.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DayOfWeek.Monday,
                new TimeOnly(9, 0),
                new TimeOnly(10, 15))
        };

        IReadOnlyList<TimeSlotDto> slots = _calculator.Calculate(
            MondayDate,
            30,
            schedules,
            []);

        // With a 10:15 end and 30m slots starting at 09:00: slots are 09:00-09:30, 09:30-10:00
        // The 10:00-10:15 remainder (15m) is dropped since it's < 30m
        Assert.Equal(2, slots.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero), slots[0].StartTime);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 30, 0, TimeSpan.Zero), slots[0].EndTime);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 30, 0, TimeSpan.Zero), slots[1].StartTime);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), slots[1].EndTime);
    }

    [Fact]
    public void Calculate_WhenBusyIntervalLeavesShorterGapBetweenBlocks_DropsInsufficientMiddleGap()
    {
        // Window: 09:00 - 12:00 UTC with 45m slots
        var schedules = new[]
        {
            AvailabilitySchedule.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DayOfWeek.Monday,
                new TimeOnly(9, 0),
                new TimeOnly(12, 0))
        };

        // Busy 09:15-10:15 leaves a 15m gap (09:00-09:15 is only 15m; 10:15-12:00 is 105m -> two 45m slots: 10:15-11:00, 11:00-11:45, 15m tail)
        var booking = new TestBookingBuilder()
            .WithStartUtc(new DateTime(2026, 11, 23, 9, 15, 0, DateTimeKind.Utc))
            .WithEndUtc(new DateTime(2026, 11, 23, 10, 15, 0, DateTimeKind.Utc))
            .Build();

        IReadOnlyList<TimeSlotDto> slots = _calculator.Calculate(
            MondayDate,
            45,
            schedules,
            [booking]);

        // 09:00-09:15 is 15m (< 45m, dropped); 10:15-12:00 yields 10:15-11:00 and 11:00-11:45 (11:45-12:00 is 15m, dropped)
        Assert.Collection(
            slots,
            slot =>
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 15, 0, TimeSpan.Zero), slot.StartTime);
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 11, 0, 0, TimeSpan.Zero), slot.EndTime);
            },
            slot =>
            {
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 11, 0, 0, TimeSpan.Zero), slot.StartTime);
                Assert.Equal(new DateTimeOffset(2026, 11, 23, 11, 45, 0, TimeSpan.Zero), slot.EndTime);
            });
    }
}

// Test helper for creating Booking instances
internal sealed class TestBookingBuilder
{
    private Guid _tenantId = Guid.NewGuid();
    private Guid _resourceId = Guid.NewGuid();
    private string _customerEmail = "test@example.com";
    private string _idempotencyKey = Guid.NewGuid().ToString();
    private DateTime _startUtc = DateTime.UtcNow.AddHours(1);
    private DateTime _endUtc = DateTime.UtcNow.AddHours(2);

    public TestBookingBuilder WithStartUtc(DateTime startUtc)
    {
        _startUtc = startUtc;
        return this;
    }

    public TestBookingBuilder WithEndUtc(DateTime endUtc)
    {
        _endUtc = endUtc;
        return this;
    }

    public Booking Build()
    {
        // Create a mock resource for testing
        var resource = new TestResourceBuilder()
            .WithId(_resourceId)
            .Build();

        return Booking.Create(
            _tenantId,
            resource,
            _customerEmail,
            _idempotencyKey,
            _startUtc,
            _endUtc,
            new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc)
    }
}

// Test helper for creating Resource instances
internal sealed class TestResourceBuilder
{
    private Guid _id = Guid.NewGuid();
    private Guid _tenantId = Guid.NewGuid();
    private string _name = "Test Resource";
    private int _bufferMinutesAfter = 0;

    public TestResourceBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public Resource Build()
    {
        return Resource.Create(_tenantId, _name, ResourceType.Room, bufferMinutesAfter: _bufferMinutesAfter);
    }
}