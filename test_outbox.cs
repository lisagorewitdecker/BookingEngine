using Microsoft.EntityFrameworkCore;
using BookingEngine.Api.Common.Persistence;

var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=bookingdb;Username=postgres;Password=postgres;");

await using var context = new AppDbContext(optionsBuilder.Options);

// Check outbox messages
var outboxCount = await context.OutboxMessages.CountAsync();
var pendingCount = await context.OutboxMessages.Where(m => m.ProcessedAtUtc == null).CountAsync();

Console.WriteLine($"Total outbox messages: {outboxCount}");
Console.WriteLine($"Pending messages: {pendingCount}");

var recent = await context.OutboxMessages
    .OrderByDescending(m => m.OccurredAtUtc)
    .Take(5)
    .ToListAsync();

foreach (var msg in recent)
{
    Console.WriteLine($"\nMessage ID: {msg.Id}");
    Console.WriteLine($"Event Type: {msg.EventType}");
    Console.WriteLine($"Occurred At: {msg.OccurredAtUtc}");
    Console.WriteLine($"Processed At: {msg.ProcessedAtUtc}");
}
