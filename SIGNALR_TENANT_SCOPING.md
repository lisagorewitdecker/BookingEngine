# Tenant-Scoped SignalR Implementation

## Overview

This document explains how BookingEngine implements **tenant-scoped real-time updates** via SignalR to ensure that booking notifications only reach clients watching a specific tenant, preventing cross-tenant data leakage.

## Architecture

### Message Flow

```
OutboxProcessorWorker (background service)
    ↓
 Creates BookingUpdateMessage with TenantId
    ↓
 Invokes BookingsHub.PublishBookingUpdate via SignalR
    ↓
 BookingsHub routes to Clients.Group(tenant:{tenantId})
    ↓
 Connected clients in that tenant's group receive update
```

### Tenant Group Naming

Each tenant has a **deterministic group name** based on their ID:
```
tenant:{tenantId}
```

For example: `tenant:550e8400-e29b-41d4-a716-446655440000`

## Implementation Details

### 1. Unified Message Type

**Location:** [`BookingEngine.Api/Common/Messaging/BookingUpdateMessage.cs`](./src/BookingEngine.Api/Common/Messaging/BookingUpdateMessage.cs)

```csharp
public sealed record BookingUpdateMessage(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    JsonElement Payload,
    Guid TenantId);  // Required for group routing
```

**Key Design:**
- `TenantId` is **required** (non-nullable)
- Used by both `OutboxProcessorWorker` and `BookingsHub`
- JSON-serializable for SignalR transport
- Ensures type safety across the pipeline

### 2. BookingsHub Group Management

**Location:** [`BookingEngine.Api/Hubs/BookingsHub.cs`](./src/BookingEngine.Api/Hubs/BookingsHub.cs)

#### Connection Handling

```csharp
public override async Task OnConnectedAsync()
{
    // Extract tenantId from query string (development) or claims (production)
    var httpContext = Context.GetHttpContext();
    if (httpContext?.Request.Query.TryGetValue("tenantId", out var tenantIdValue) == true &&
        Guid.TryParse(tenantIdValue.ToString(), out var tenantId))
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantId));
    }

    await base.OnConnectedAsync();
}
```

**Client Connection Example (JavaScript):**
```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://api.example.com/hubs/bookings?tenantId=550e8400-e29b-41d4-a716-446655440000")
    .withAutomaticReconnect()
    .build();

connection.on("BookingUpdated", (message) => {
    console.log("Booking updated:", message.EventType, message.Payload);
});

await connection.start();
```

#### Explicit Group Management

Clients can dynamically join/leave tenant groups:

```csharp
// Join a tenant group
await hubConnection.InvokeAsync("JoinTenantGroup", tenantId);

// Leave a tenant group
await hubConnection.InvokeAsync("LeaveTenantGroup", tenantId);
```

#### Publishing Updates to Tenant Groups

```csharp
public async Task PublishBookingUpdate(BookingUpdateMessage message)
{
    // Route only to clients in this tenant's group
    var targetClients = Clients.Group(GetTenantGroupName(message.TenantId));
    await targetClients.SendAsync(ClientEventName, message);
}
```

### 3. OutboxProcessorWorker Integration

**Location:** [`BookingEngine.Worker/OutboxProcessorWorker.cs`](./src/BookingEngine.Worker/OutboxProcessorWorker.cs)

The worker polls the `outbox_messages` table and broadcasts updates:

```csharp
var batch = await dbContext.OutboxMessages
    .FromSqlRaw(
        """
        SELECT "id", "tenant_id", "event_type", "payload", "occurred_at_utc"
        FROM "outbox_messages"
        WHERE "processed_at_utc" IS NULL
        ORDER BY "occurred_at_utc" ASC
        LIMIT {0}
        """,
        _options.BatchSize)
    .IgnoreQueryFilters()
    .Select(m => new OutboxRow(m.Id, m.TenantId, m.EventType, m.Payload, m.OccurredAtUtc))
    .ToListAsync(ct);

foreach (var message in batch)
{
    var payloadElement = ParsePayloadOrFallback(message.Payload);

    await hubConnection.InvokeAsync(
        BookingsHub.PublishMethodName,
        new BookingUpdateMessage(
            message.Id,
            message.EventType,
            message.OccurredAtUtc,
            payloadElement,
            message.TenantId),  // ← TenantId included
        ct);
}
```

**Key Points:**
- `IgnoreQueryFilters()` bypasses tenant isolation in EF Core (necessary because worker processes all tenants)
- Each message includes its `TenantId` from the database
- Messages are invoked as server-to-hub calls, not client-initiated
- Failed messages retry after reconnect; processed ones are marked with `processed_at_utc`

## Security Considerations

### Development vs. Production

**Development (Current):**
- TenantId extracted from query string: `?tenantId=...`
- Suitable for testing and local development
- **Not suitable for production**

**Production (Recommended):**
1. Derive `tenantId` from authenticated user claims or JWT token
2. Validate that the user is authorized for the requested tenant
3. Update `OnConnectedAsync` to extract from `HttpContext.User.FindFirst("tenant_id")`

**Example Production Implementation:**
```csharp
public override async Task OnConnectedAsync()
{
    var user = Context.User;
    if (user?.FindFirst("tenant_id")?.Value is string tenantIdStr &&
        Guid.TryParse(tenantIdStr, out var tenantId))
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantId));
    }
    else
    {
        throw new HubException("User must have a valid tenant_id claim.");
    }

    await base.OnConnectedAsync();
}
```

### Message Validation

The `PublishBookingUpdate` method does not perform additional validation because:
1. Messages originate from the **trusted outbox pipeline** (not from clients)
2. Each message carries its `TenantId` from the database (immutable)
3. Group routing ensures messages only reach clients in that tenant's group

If you expose a public API endpoint for broadcasting, validate tenant ownership:
```csharp
app.MapPost("/internal/outbox/broadcast", async (
    BookingUpdateMessage message,
    ITenantContext tenantContext,
    IHubContext<BookingsHub> hubContext,
    CancellationToken cancellationToken) =>
{
    // Validate that the request's tenant matches the message tenant
    if (tenantContext.TenantId != message.TenantId)
    {
        return TypedResults.Forbid();
    }

    await hubContext.Clients.Group(BookingsHub.GetTenantGroupName(message.TenantId))
        .SendAsync(BookingsHub.ClientEventName, message, cancellationToken);

    return TypedResults.Accepted();
});
```

## Configuration

**Worker Configuration** (`appsettings.json`):
```json
{
  "OutboxWorker": {
    "ApiBaseUrl": "https://localhost:7046",
    "HubPath": "/hubs/bookings",
    "BroadcastMethod": "BookingUpdated",
    "PollIntervalMilliseconds": 1000,
    "BatchSize": 50,
    "AllowSelfSignedCertificates": true
  }
}
```

**Hub Configuration** (`Program.cs`):
```csharp
app.MapHub<BookingsHub>("/hubs/bookings");
```

## Outbox Table Schema

```sql
CREATE TABLE "outbox_messages" (
    "id" UUID PRIMARY KEY,
    "tenant_id" UUID NOT NULL,  -- ← Tenant identifier
    "event_type" TEXT NOT NULL,
    "payload" TEXT NOT NULL,     -- ← JSON payload
    "occurred_at_utc" TIMESTAMP NOT NULL,
    "processed_at_utc" TIMESTAMP NULL,
    FOREIGN KEY ("tenant_id") REFERENCES "tenants"("id")
);
```

## Event Types

The `EventType` field in the outbox message describes the booking event. Examples:
- `BookingCreated`
- `BookingCancelled`
- `BookingRescheduled`
- `AvailabilityUpdated`

Clients can filter by event type:
```javascript
connection.on("BookingUpdated", (message) => {
    switch (message.eventType) {
        case "BookingCreated":
            updateBookingList(message.payload);
            break;
        case "BookingCancelled":
            removeBooking(message.payload.bookingId);
            break;
    }
});
```

## Monitoring and Debugging

### Worker Logs

The `OutboxProcessorWorker` logs key events:
```
INFO: OutboxProcessorWorker starting...
INFO: Full hub URL: https://localhost:7046/hubs/bookings
INFO: Successfully connected to SignalR hub
INFO: Broadcast and marked 25 outbox messages as processed.
WARN: Hub connection is not connected; deferring batch processing.
ERROR: Unexpected error broadcasting message {MessageId}; will continue with next message.
```

### Verify Tenant Group Routing

To verify that tenant groups are working:

1. **Connect two clients to different tenants:**
   ```
   Client A: ws://localhost:7046/hubs/bookings?tenantId=tenant-1
   Client B: ws://localhost:7046/hubs/bookings?tenantId=tenant-2
   ```

2. **Create a booking in Tenant 1**
   - Outbox message is inserted with `tenant_id=tenant-1`
   - Worker broadcasts to `tenant:tenant-1` group
   - Only Client A receives the update ✓

3. **Check SignalR diagnostics:**
   ```csharp
   app.MapHub<BookingsHub>("/hubs/bookings", options =>
   {
       options.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
   });
   ```

## Testing

### Unit Test Example

```csharp
[Fact]
public async Task PublishBookingUpdate_RoutesToTenantGroup()
{
    // Arrange
    var message = new BookingUpdateMessage(
        Guid.NewGuid(),
        "BookingCreated",
        DateTimeOffset.UtcNow,
        JsonDocument.Parse("{}").RootElement,
        tenantId: Guid.Parse("550e8400-e29b-41d4-a716-446655440000"));

    var mockClients = new Mock<IHubCallerClients>();
    var mockGroup = new Mock<IClientProxy>();

    mockClients
        .Setup(x => x.Group(It.IsAny<string>()))
        .Returns(mockGroup.Object);

    var hub = new BookingsHub
    {
        Clients = mockClients.Object
    };

    // Act
    await hub.PublishBookingUpdate(message);

    // Assert
    mockClients.Verify(
        x => x.Group("tenant:550e8400-e29b-41d4-a716-446655440000"),
        Times.Once);

    mockGroup.Verify(
        x => x.SendAsync(
            BookingsHub.ClientEventName,
            It.Is<BookingUpdateMessage>(m => m.TenantId == message.TenantId),
            It.IsAny<CancellationToken>()),
        Times.Once);
}
```

### Integration Test Example

```csharp
[Fact]
public async Task TenantGroups_PreventCrossTenantUpdates()
{
    // Arrange
    var tenantA = Guid.NewGuid();
    var tenantB = Guid.NewGuid();

    var clientA = new HubConnectionBuilder()
        .WithUrl($"https://localhost:7046/hubs/bookings?tenantId={tenantA}")
        .Build();

    var clientB = new HubConnectionBuilder()
        .WithUrl($"https://localhost:7046/hubs/bookings?tenantId={tenantB}")
        .Build();

    await clientA.StartAsync();
    await clientB.StartAsync();

    var receivedByA = new TaskCompletionSource<BookingUpdateMessage>();
    var receivedByB = new TaskCompletionSource<BookingUpdateMessage>();

    clientA.On<BookingUpdateMessage>(BookingsHub.ClientEventName,
        msg => receivedByA.TrySetResult(msg));

    clientB.On<BookingUpdateMessage>(BookingsHub.ClientEventName,
        msg => receivedByB.TrySetResult(msg));

    // Act
    // Create a booking for Tenant A
    var bookingResponse = await httpClient.PostAsync("/api/bookings",
        new StringContent(
            JsonSerializer.Serialize(new { tenantId = tenantA, ... }),
            Encoding.UTF8,
            "application/json"));

    // Assert
    var tenantAUpdate = await receivedByA.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.NotNull(tenantAUpdate);
    Assert.Equal(tenantA, tenantAUpdate.TenantId);

    // Verify Tenant B did NOT receive the message
    Assert.False(receivedByB.Task.IsCompleted);
}
```

## Troubleshooting

### "No clients in group"
**Cause:** Client connected but not added to tenant group.
**Solution:** Verify query string contains valid `tenantId` parameter, or check production claims.

### "Connection lost while broadcasting"
**Cause:** Worker connection to hub dropped.
**Solution:** Worker auto-reconnects with exponential backoff (0s, 1s, 2s, 5s, 10s).

### Messages not appearing on client
1. Check client is connected: `connection.state === signalR.HubConnectionState.Connected`
2. Verify event listener is registered: `connection.on("BookingUpdated", ...)`
3. Check browser console for SignalR logs
4. Verify outbox table has unprocessed rows: `SELECT COUNT(*) FROM outbox_messages WHERE processed_at_utc IS NULL;`

## References

- [ASP.NET Core SignalR Groups](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups)
- [Transactional Outbox Pattern](https://microservices.io/patterns/data/transactional-outbox.html)
- [Multi-Tenant ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-context)
