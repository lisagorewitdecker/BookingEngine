using System.Text.Json;
using BookingEngine.Api.Common.Domain;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BookingEngine.Api.Common.Persistence.Interceptors;

public sealed class ConvertDomainEventsToOutboxMessagesInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var dbContext = eventData.Context;
        if (dbContext is null)
        {
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        var outboxMessages = dbContext.ChangeTracker
            .Entries<AggregateRoot>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .SelectMany(aggregate =>
            {
                var tenantId = (aggregate as ITenantEntity)?.TenantId ?? Guid.Empty;
                var events = aggregate.DomainEvents.ToList();
                aggregate.ClearDomainEvents();

                return events.Select(domainEvent => new OutboxMessage
                {
                    Id = domainEvent.EventId,
                    TenantId = tenantId,
                    EventType = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), SerializerOptions),
                    OccurredAtUtc = domainEvent.OccurredAtUtc
                });
            })
            .ToList();

        if (outboxMessages.Count > 0)
        {
            dbContext.Set<OutboxMessage>().AddRange(outboxMessages);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}