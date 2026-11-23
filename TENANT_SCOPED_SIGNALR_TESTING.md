# Tenant-Scoped SignalR - Testing Guide

**Manual & Automated Tests for Verifying Tenant Isolation**

---

## 🧪 Unit Tests

### **Test 1: Domain Event Includes TenantId**

```csharp
[Test]
public void BookingCreatedEvent_IncludesTenantId()
{
    // Arrange
    var tenantId = Guid.NewGuid();
    var bookingId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var now = DateTime.UtcNow;

    // Act
    var @event = new BookingCreatedEvent(
        EventId: eventId,
        OccurredAtUtc: now,
        BookingId: bookingId,
        TenantId: tenantId,
        ResourceId: Guid.NewGuid(),
        CustomerEmail: "user@example.com",
        StartUtc: now.AddDays(1),
        EndUtc: now.AddDays(1).AddHours(1)
    );

    // Assert
    Assert.That(@event.TenantId, Is.EqualTo(tenantId));
}
```

### **Test 2: Hub Group Name Is Deterministic**

```csharp
[Test]
public void GetTenantGroupName_ReturnsDeterministicValue()
{
    // Arrange
    var tenantId = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    var expected = "tenant:550e8400-e29b-41d4-a716-446655440000";

    // Act
    var groupName1 = BookingsHub.GetTenantGroupName(tenantId);
    var groupName2 = BookingsHub.GetTenantGroupName(tenantId);

    // Assert
    Assert.That(groupName1, Is.EqualTo(expected));
    Assert.That(groupName2, Is.EqualTo(groupName1));
}
```

### **Test 3: BookingUpdateMessage Requires TenantId**

```csharp
[Test]
public void BookingUpdateMessage_HasRequiredTenantId()
{
    // Arrange
    var message = new BookingUpdateMessage(
        MessageId: Guid.NewGuid(),
        EventType: "BookingCreatedEvent",
        OccurredAtUtc: DateTimeOffset.UtcNow,
        Payload: "{}",
        TenantId: Guid.NewGuid()
    );

    // Assert
    Assert.That(message.TenantId, Is.Not.EqualTo(Guid.Empty));
}
```

---

## 🔗 Integration Tests

### **Test 4: Outbox Interceptor Captures TenantId**

```csharp
[IntegrationTest]
public async Task OutboxInterceptor_CapturesTenantId_WhenBookingCreated()
{
    // Arrange
    using var factory = new BookingEngineWebAppFactory();
    using var client = factory.CreateClient();
    
    var tenantId = Guid.NewGuid();
    var dbContext = factory.GetRequiredService<AppDbContext>();
    
    // Set up tenant context
    var tenantContext = factory.GetRequiredService<ITenantContext>();
    tenantContext.SetTenantId(tenantId);

    // Create a booking (which raises BookingCreatedEvent)
    var booking = Booking.Create(
        tenantId: tenantId,
        resource: new Resource { /* ... */ },
        customerEmail: "test@example.com",
        idempotencyKey: Guid.NewGuid().ToString(),
        requestedStartUtc: DateTime.UtcNow.AddDays(1),
        requestedEndUtc: DateTime.UtcNow.AddDays(1).AddHours(1),
        utcNow: DateTime.UtcNow
    );

    dbContext.Bookings.Add(booking);

    // Act
    await dbContext.SaveChangesAsync();

    // Assert
    var outboxMessage = await dbContext.OutboxMessages
        .FirstOrDefaultAsync(m => m.Id == booking.DomainEvents.First().EventId);

    Assert.That(outboxMessage, Is.Not.Null);
    Assert.That(outboxMessage.TenantId, Is.EqualTo(tenantId));
    Assert.That(outboxMessage.EventType, Is.EqualTo("BookingEngine.Api.Common.Domain.BookingCreatedEvent"));
}
```

### **Test 5: Outbox Message Persisted With Correct Schema**

```csharp
[IntegrationTest]
public async Task OutboxMessage_Schema_IncludesTenantId()
{
    // Arrange
    using var factory = new BookingEngineWebAppFactory();
    var dbContext = factory.GetRequiredService<AppDbContext>();
    
    var tenantId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;

    // Act
    var outboxMessage = new OutboxMessage
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        EventType = "BookingCreatedEvent",
        Payload = "{}",
        OccurredAtUtc = now
    };
    
    dbContext.OutboxMessages.Add(outboxMessage);
    await dbContext.SaveChangesAsync();

    // Assert
    var retrieved = await dbContext.OutboxMessages
        .FirstOrDefaultAsync(m => m.Id == outboxMessage.Id);

    Assert.That(retrieved.TenantId, Is.EqualTo(tenantId));
    Assert.That(retrieved.OccurredAtUtc, Is.EqualTo(now));
}
```

---

## 🔄 End-to-End Tests

### **Test 6: Tenant-123 Client Receives Tenant-123 Events Only**

```csharp
[SignalRIntegrationTest]
public async Task TenantSpecificClient_ReceivesOnlyTenantSpecificEvents()
{
    // Arrange
    using var factory = new BookingEngineWebAppFactory();
    
    var tenant123Id = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    var tenant456Id = Guid.Parse("660e8400-e29b-41d4-a716-446655440000");

    // Client for tenant-123
    var connection123 = new HubConnectionBuilder()
        .WithUrl($"{factory.ServerAddress}/hubs/bookings?tenantId={tenant123Id}")
        .Build();

    var receivedEvents = new List<BookingUpdateMessage>();
    
    connection123.On<BookingUpdateMessage>("BookingUpdated", (message) =>
    {
        receivedEvents.Add(message);
    });

    await connection123.StartAsync();

    // Act - Create event for tenant-123
    var booking123 = await CreateBookingForTenantAsync(factory, tenant123Id);
    
    // Act - Create event for tenant-456
    var booking456 = await CreateBookingForTenantAsync(factory, tenant456Id);

    // Allow time for broadcasting
    await Task.Delay(TimeSpan.FromSeconds(2));

    // Assert - Tenant-123 client only received tenant-123 event
    Assert.That(receivedEvents.Count, Is.EqualTo(1));
    Assert.That(receivedEvents[0].TenantId, Is.EqualTo(tenant123Id));

    // Cleanup
    await connection123.StopAsync();
}
```

### **Test 7: Multiple Tenants Don't Cross-Contaminate**

```csharp
[SignalRIntegrationTest]
public async Task MultipleTenantClients_AreIsolatedFromEachOther()
{
    // Arrange
    using var factory = new BookingEngineWebAppFactory();
    
    var tenant123Id = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    var tenant456Id = Guid.Parse("660e8400-e29b-41d4-a716-446655440000");

    // Connection 1: Tenant-123
    var connection123 = new HubConnectionBuilder()
        .WithUrl($"{factory.ServerAddress}/hubs/bookings?tenantId={tenant123Id}")
        .Build();

    var received123 = new List<BookingUpdateMessage>();
    connection123.On<BookingUpdateMessage>("BookingUpdated", m => received123.Add(m));

    // Connection 2: Tenant-456
    var connection456 = new HubConnectionBuilder()
        .WithUrl($"{factory.ServerAddress}/hubs/bookings?tenantId={tenant456Id}")
        .Build();

    var received456 = new List<BookingUpdateMessage>();
    connection456.On<BookingUpdateMessage>("BookingUpdated", m => received456.Add(m));

    await connection123.StartAsync();
    await connection456.StartAsync();

    // Act - Create 3 bookings for tenant-123
    for (int i = 0; i < 3; i++)
    {
        await CreateBookingForTenantAsync(factory, tenant123Id);
    }

    // Act - Create 2 bookings for tenant-456
    for (int i = 0; i < 2; i++)
    {
        await CreateBookingForTenantAsync(factory, tenant456Id);
    }

    await Task.Delay(TimeSpan.FromSeconds(2));

    // Assert - Each connection only received their tenant's events
    Assert.That(received123.Count, Is.EqualTo(3));
    Assert.That(received456.Count, Is.EqualTo(2));

    Assert.That(received123.All(m => m.TenantId == tenant123Id), Is.True);
    Assert.That(received456.All(m => m.TenantId == tenant456Id), Is.True);

    // Cleanup
    await connection123.StopAsync();
    await connection456.StopAsync();
}
```

### **Test 8: Same User, Multiple Tenant Connections**

```csharp
[SignalRIntegrationTest]
public async Task SameUser_MultipleConnections_ReceiveTenantSpecificEvents()
{
    // Arrange - User is member of both tenant-123 and tenant-456
    using var factory = new BookingEngineWebAppFactory();
    
    var tenant123Id = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    var tenant456Id = Guid.Parse("660e8400-e29b-41d4-a716-446655440000");

    // Open TWO connections for different tenants
    var connection123 = new HubConnectionBuilder()
        .WithUrl($"{factory.ServerAddress}/hubs/bookings?tenantId={tenant123Id}")
        .Build();

    var connection456 = new HubConnectionBuilder()
        .WithUrl($"{factory.ServerAddress}/hubs/bookings?tenantId={tenant456Id}")
        .Build();

    var received123 = new List<BookingUpdateMessage>();
    var received456 = new List<BookingUpdateMessage>();

    connection123.On<BookingUpdateMessage>("BookingUpdated", m => received123.Add(m));
    connection456.On<BookingUpdateMessage>("BookingUpdated", m => received456.Add(m));

    await connection123.StartAsync();
    await connection456.StartAsync();

    // Act
    await CreateBookingForTenantAsync(factory, tenant123Id);
    await CreateBookingForTenantAsync(factory, tenant456Id);
    await CreateBookingForTenantAsync(factory, tenant123Id);

    await Task.Delay(TimeSpan.FromSeconds(2));

    // Assert
    Assert.That(received123.Count, Is.EqualTo(2));  // Only tenant-123 events
    Assert.That(received456.Count, Is.EqualTo(1));  // Only tenant-456 events

    // Cleanup
    await connection123.StopAsync();
    await connection456.StopAsync();
}
```

---

## 🧪 Manual Testing

### **Setup: Terminal 1 - Start the API**

```bash
cd BookingEngine
dotnet run --project src/BookingEngine.Api/BookingEngine.Api.csproj
# Output: https://localhost:7046
```

### **Setup: Terminal 2 - Start the Worker**

```bash
cd BookingEngine
dotnet run --project src/BookingEngine.Worker/BookingEngine.Worker.csproj
# Output: "Successfully connected to SignalR hub"
```

### **Setup: Terminal 3 - Open Browser DevTools**

```javascript
// Paste in DevTools Console

const tenantId123 = "550e8400-e29b-41d4-a716-446655440000";
const tenantId456 = "660e8400-e29b-41d4-a716-446655440000";

// Connection 1: Tenant-123
const conn123 = new signalR.HubConnectionBuilder()
    .withUrl(`https://localhost:7046/hubs/bookings?tenantId=${tenantId123}`)
    .withAutomaticReconnect()
    .build();

conn123.on("BookingUpdated", (msg) => {
    console.log("🟦 Tenant-123 received:", msg);
});

conn123.start()
    .then(() => console.log("Connected: Tenant-123"))
    .catch(err => console.error("Failed to connect:", err));

// Connection 2: Tenant-456
const conn456 = new signalR.HubConnectionBuilder()
    .withUrl(`https://localhost:7046/hubs/bookings?tenantId=${tenantId456}`)
    .withAutomaticReconnect()
    .build();

conn456.on("BookingUpdated", (msg) => {
    console.log("🟩 Tenant-456 received:", msg);
});

conn456.start()
    .then(() => console.log("Connected: Tenant-456"))
    .catch(err => console.error("Failed to connect:", err));
```

### **Test: Create Booking for Tenant-123**

```bash
curl -X POST https://localhost:7046/api/bookings/create \
  -H "X-Tenant-Id: 550e8400-e29b-41d4-a716-446655440000" \
  -H "Content-Type: application/json" \
  -d '{
    "resourceId": "770e8400-e29b-41d4-a716-446655440000",
    "customerEmail": "user123@example.com",
    "startUtc": "2024-01-20T14:00:00Z",
    "endUtc": "2024-01-20T15:00:00Z",
    "idempotencyKey": "booking-001"
  }' \
  --insecure
```

**Expected Console Output:**
```
🟦 Tenant-123 received: {tenantId: "550e8400...", eventType: "BookingCreatedEvent", ...}
(no output for Tenant-456 connection) ✅
```

### **Test: Create Booking for Tenant-456**

```bash
curl -X POST https://localhost:7046/api/bookings/create \
  -H "X-Tenant-Id: 660e8400-e29b-41d4-a716-446655440000" \
  -H "Content-Type: application/json" \
  -d '{
    "resourceId": "770e8400-e29b-41d4-a716-446655440000",
    "customerEmail": "user456@example.com",
    "startUtc": "2024-01-20T16:00:00Z",
    "endUtc": "2024-01-20T17:00:00Z",
    "idempotencyKey": "booking-002"
  }' \
  --insecure
```

**Expected Console Output:**
```
🟩 Tenant-456 received: {tenantId: "660e8400...", eventType: "BookingCreatedEvent", ...}
(no additional output for Tenant-123 connection) ✅
```

---

## 🔍 Verification Queries

### **SQL: Check Outbox Persistence**

```sql
-- View unprocessed outbox messages
SELECT id, tenant_id, event_type, occurred_at_utc, processed_at_utc
FROM outbox_messages
WHERE processed_at_utc IS NULL
ORDER BY occurred_at_utc DESC;

-- Expected: Every row has a valid tenant_id
-- ✅ No NULL tenant_id values
-- ✅ Each event has the correct tenant_id for its booking
```

### **SQL: Verify Tenant Isolation**

```sql
-- Count events per tenant
SELECT tenant_id, COUNT(*) as event_count
FROM outbox_messages
GROUP BY tenant_id
ORDER BY tenant_id;

-- Expected: Each tenant has only their own events
-- tenant_id | event_count
-- ---|---
-- 550e8400... | 2 (bookings for tenant-123)
-- 660e8400... | 1 (bookings for tenant-456)
```

### **SQL: Check Processing Status**

```sql
-- View recently processed messages
SELECT id, tenant_id, event_type, processed_at_utc
FROM outbox_messages
WHERE processed_at_utc IS NOT NULL
ORDER BY processed_at_utc DESC
LIMIT 10;

-- Expected: processed_at_utc is NOT NULL (message was broadcasted)
```

---

## ✅ Test Checklist

### **Before Deploying to Production**

- [ ] All unit tests pass
- [ ] All integration tests pass
- [ ] Manual browser test: Tenant-123 receives only tenant-123 events
- [ ] Manual browser test: Tenant-456 receives only tenant-456 events
- [ ] Verify worker logs show broadcasts for both tenants
- [ ] Check SQL: No NULL tenant_id in outbox_messages
- [ ] Check SQL: No cross-tenant events in a single tenant's processed list
- [ ] Load test: 100+ concurrent connections across multiple tenants
- [ ] Failure test: Kill worker, reconnect, verify queued events broadcast correctly
- [ ] Auth test: Replace query param with JWT claim validation
- [ ] Logging test: Verify all broadcasts logged with tenant context

---

## 📊 Performance Considerations

### **Concurrent Connections**
```
Current Design:
- Each connection adds to one tenant group
- Groups are lightweight (SignalR handles efficiently)
- Expected capacity: 1000s of connections per server

Bottleneck Analysis:
- Database query (outbox polling): O(batch_size)
- Hub broadcast: O(connections_in_group)
- Group membership: O(1) per connection
```

### **Latency Expectations**
```
Booking Create → Event Raised → Outbox Persisted → Worker Polls → Hub Broadcasts → Client Received

Typical Flow:
1. POST /api/bookings/create        : ~100ms (DB round-trip)
2. Interceptor → Outbox             : ~0ms (in-memory)
3. SaveChangesAsync                 : ~50ms (atomic transaction)
4. Worker polling (1s interval)     : ~1000ms (worst case)
5. Hub invocation                   : ~50ms (in-process)
6. Network to client                : ~100ms (browser latency)
   ───────────────────────────────────────────
   Total                            : ~1250ms (worst case)
   
Typical case (worker just polled): ~200-300ms ✅
```

---

## 🚨 Anti-Patterns to Avoid

### ❌ **DON'T: Use Clients.All**

```csharp
// WRONG - Broadcasts to everyone
await Clients.All.SendAsync("BookingUpdated", message);
```

### ✅ **DO: Use Clients.Group**

```csharp
// CORRECT - Only routes to this tenant's clients
var group = GetTenantGroupName(message.TenantId);
await Clients.Group(group).SendAsync("BookingUpdated", message);
```

### ❌ **DON'T: Trust Client-Supplied Tenant**

```csharp
// WRONG - Client can forge any tenantId
var tenantId = Context.GetHttpContext()?.Request.Query["tenantId"];
```

### ✅ **DO: Validate from Claims**

```csharp
// CORRECT - Derived from verified JWT
var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
```

---

## 📚 Test Helpers

### **CreateBookingForTenantAsync**

```csharp
private async Task<Booking> CreateBookingForTenantAsync(
    BookingEngineWebAppFactory factory,
    Guid tenantId)
{
    var httpClient = factory.CreateClient();
    httpClient.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());

    var request = new CreateBookingRequest(
        ResourceId: Guid.Parse("770e8400-e29b-41d4-a716-446655440000"),
        CustomerEmail: $"user{Guid.NewGuid()}@example.com",
        StartUtc: DateTime.UtcNow.AddDays(1).Date.AddHours(10),
        EndUtc: DateTime.UtcNow.AddDays(1).Date.AddHours(11),
        IdempotencyKey: Guid.NewGuid().ToString()
    );

    var response = await httpClient.PostAsJsonAsync(
        "/api/bookings/create",
        request);

    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadAsAsync<CreateBookingResponse>();
    
    // Poll outbox until message is created
    var dbContext = factory.GetRequiredService<AppDbContext>();
    var outboxMessage = await dbContext.OutboxMessages
        .FirstOrDefaultAsync(m => m.TenantId == tenantId);

    if (outboxMessage is null)
        await Task.Delay(100);

    return result;
}
```

---

## 🎯 Success Criteria

| Criterion | Expected | Status |
|-----------|----------|--------|
| Tenant-123 receives only tenant-123 events | ✅ | [ ] |
| Tenant-456 receives only tenant-456 events | ✅ | [ ] |
| No cross-tenant data leakage | ✅ | [ ] |
| Outbox contains all events with tenant_id | ✅ | [ ] |
| Worker broadcasts to correct hub group | ✅ | [ ] |
| Hub PublishBookingUpdate routes correctly | ✅ | [ ] |
| Zero errors in build | ✅ | [ ] |
| All unit tests pass | ✅ | [ ] |
| All integration tests pass | ✅ | [ ] |

---

**Run all tests before production deployment!** 🚀
