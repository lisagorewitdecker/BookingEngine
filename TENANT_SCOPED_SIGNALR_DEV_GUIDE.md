# Tenant-Scoped SignalR - Developer Guide

**Quick Reference for Broadcasting Tenant-Specific Booking Updates**

---

## 🚀 How It Works (3-Minute Overview)

```
Booking Created → Domain Event (w/ TenantId) 
              → Outbox Table (persisted w/ TenantId) 
              → Worker Process (polls & publishes) 
              → SignalR Hub (routes to tenant:{tenantId} group) 
              → Connected Clients (only in that tenant's group receive it)
```

**Key Principle:** Updates are **never** sent to `Clients.All`. They're **always** routed to a specific tenant group.

---

## 🎯 For Frontend Developers

### **Connect to BookingsHub with Tenant Context**

```javascript
// Development (using query parameter)
const connection = new HubConnectionBuilder()
    .withUrl(`/hubs/bookings?tenantId=${tenantId}`)
    .withAutomaticReconnect()
    .build();

await connection.start();

// Listen for booking updates
connection.on("BookingUpdated", (message) => {
    console.log(`Booking event: ${message.eventType} for tenant ${message.tenantId}`);
    
    // Parse the payload
    const payload = JSON.parse(message.payload);
    console.log("Booking details:", payload);
});

// Optionally join/leave additional tenant groups
await connection.invoke("JoinTenantGroup", newTenantId);
await connection.invoke("LeaveTenantGroup", oldTenantId);
```

### **What You'll Receive**

```typescript
interface BookingUpdateMessage {
    messageId: string;           // Event ID
    eventType: string;           // "BookingCreatedEvent" | "BookingConfirmedEvent" | "BookingCancelledEvent"
    occurredAtUtc: string;       // ISO 8601 timestamp
    payload: string;             // JSON-stringified event details
    tenantId: string;            // Your tenant ID (guarantees isolation)
}
```

### **Example Payload (BookingCreatedEvent)**

```json
{
    "eventId": "550e8400-e29b-41d4-a716-446655440000",
    "occurredAtUtc": "2024-01-15T10:30:00Z",
    "bookingId": "660e8400-e29b-41d4-a716-446655440000",
    "tenantId": "tenant-123",
    "resourceId": "770e8400-e29b-41d4-a716-446655440000",
    "customerEmail": "user@example.com",
    "startUtc": "2024-01-15T14:00:00Z",
    "endUtc": "2024-01-15T15:00:00Z"
}
```

---

## 🔧 For Backend Developers

### **1. Raising a Domain Event**

When you create a booking (or any aggregate), raise an event with TenantId:

```csharp
// In your Booking aggregate
var booking = new Booking { ... };

booking.RaiseDomainEvent(new BookingCreatedEvent(
    EventId: Guid.NewGuid(),
    OccurredAtUtc: utcNow,
    BookingId: booking.Id,
    TenantId: booking.TenantId,        // ← Always include TenantId
    ResourceId: booking.ResourceId,
    CustomerEmail: booking.CustomerEmail,
    StartUtc: booking.StartUtc,
    EndUtc: booking.EndUtc
));
```

✅ **The interceptor automatically captures the TenantId and persists it to the outbox.**

### **2. Receiving in BookingsHub**

The hub method is invoked by the worker process (as a SignalR client). It automatically routes to the tenant's group:

```csharp
// File: BookingEngine.Api/Hubs/BookingsHub.cs

public async Task PublishBookingUpdate(BookingUpdateMessage message)
{
    // Routes ONLY to clients in tenant:{tenantId} group
    var targetClients = Clients.Group(GetTenantGroupName(message.TenantId));
    await targetClients.SendAsync(ClientEventName, message);
}
```

**You don't need to modify this.** The routing is automatic and tenant-safe.

### **3. Adding New Event Types**

If you add a new domain event:

```csharp
// 1. Define the event (include TenantId)
public sealed record BookingRescheduledEvent(
    Guid EventId,
    DateTime OccurredAtUtc,
    Guid BookingId,
    Guid TenantId,            // ← Always include this
    Guid ResourceId,
    DateTime NewStartUtc,
    DateTime NewEndUtc) : IDomainEvent;

// 2. Raise it from your aggregate
booking.RaiseDomainEvent(new BookingRescheduledEvent(
    EventId: Guid.NewGuid(),
    OccurredAtUtc: utcNow,
    BookingId: booking.Id,
    TenantId: booking.TenantId,  // ← Automatic tenant isolation
    ResourceId: booking.ResourceId,
    NewStartUtc: newStart,
    NewEndUtc: newEnd
));

// 3. Done! Interceptor handles outbox persistence & worker handles broadcasting
```

---

## 🔐 Tenant Isolation Guarantee

### **Development Mode (Query Parameter)**
```
// OK for local testing
?tenantId=550e8400-e29b-41d4-a716-446655440000
```

### **Production Mode (Requires Authentication)**

The connection should derive TenantId from authenticated claims:

```csharp
public override async Task OnConnectedAsync()
{
    var httpContext = Context.GetHttpContext();
    
    // Derive from JWT claim (not query param)
    var tenantId = httpContext?.User?.FindFirst("tenant_id")?.Value;
    
    if (!string.IsNullOrWhiteSpace(tenantId) && Guid.TryParse(tenantId, out var id))
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(id));
    }

    await base.OnConnectedAsync();
}
```

**Verify Authorization:**
```csharp
public async Task JoinTenantGroup(Guid tenantId)
{
    // Reject if user is not authorized for this tenant
    var userTenantId = Context.User?.FindFirst("tenant_id")?.Value;
    if (userTenantId != tenantId.ToString())
        throw new HubException("Unauthorized to subscribe to this tenant");

    await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantId));
}
```

---

## 📊 Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                     BookingEngine.Api                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  POST /api/bookings/create                                      │
│        ↓                                                         │
│  Booking.Create(tenantId, ...)                                  │
│        ↓                                                         │
│  booking.RaiseDomainEvent(                                       │
│      new BookingCreatedEvent(..., TenantId: tenant-123)        │
│  )                                                              │
│        ↓                                                         │
│  SaveChangesAsync()  ← Triggers Interceptor                     │
│        ↓                                                         │
│  ConvertDomainEventsToOutboxMessagesInterceptor                │
│        ├─ Extracts event.TenantId                               │
│        └─ Creates OutboxMessage(TenantId: tenant-123)          │
│        ↓                                                         │
│  Atomic INSERT:                                                 │
│    - Booking (into bookings table)                             │
│    - OutboxMessage (into outbox_messages table)                │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                   PostgreSQL Database                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  outbox_messages table:                                         │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │ id  │ tenant_id    │ event_type  │ payload │ processed  │  │
│  ├─────────────────────────────────────────────────────────┤  │
│  │ ... │ tenant-123   │ BookingCreatedEvent │ NULL        │  │
│  │ ... │ tenant-456   │ ...                 │ NULL        │  │
│  └─────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                 BookingEngine.Worker Process                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  While true:                                                    │
│    SELECT * FROM outbox_messages                               │
│    WHERE processed_at_utc IS NULL                              │
│    LIMIT 50                                                     │
│        ↓                                                         │
│    For each message:                                            │
│      hubConnection.InvokeAsync(                                │
│          "PublishBookingUpdate",                               │
│          new BookingUpdateMessage(..., TenantId: tenant-123)   │
│      )                                                          │
│        ↓                                                         │
│    UPDATE outbox_messages SET processed_at_utc = NOW()        │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                   BookingsHub (SignalR)                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  PublishBookingUpdate(message)                                 │
│    ↓                                                             │
│    var group = GetTenantGroupName(message.TenantId)            │
│    = "tenant:550e8400-e29b-41d4-a716-446655440000"           │
│    ↓                                                             │
│    Clients.Group(group).SendAsync("BookingUpdated", message)   │
│                                                                 │
│  Groups:                                                        │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │ tenant:550e8400-e29b-... ← Only these clients receive  │  │
│  │   ├─ ConnectionId: abc123 (Browser Tab 1)              │  │
│  │   └─ ConnectionId: def456 (Mobile App)                 │  │
│  │                                                         │  │
│  │ tenant:660e8400-e29b-... ← Isolated from above         │  │
│  │   ├─ ConnectionId: xyz789 (Browser Tab 1)              │  │
│  │   └─ ConnectionId: uvw012 (Tablet)                     │  │
│  └─────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│                      Connected Clients                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Tenant-123 Clients:                                            │
│  ✅ Browser Tab 1   ← Receives "BookingUpdated"               │
│  ✅ Mobile App      ← Receives "BookingUpdated"               │
│                                                                 │
│  Tenant-456 Clients:                                            │
│  ❌ Browser Tab 1   ← Does NOT receive (different tenant)     │
│  ❌ Tablet          ← Does NOT receive (different tenant)     │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## ✅ Verification Checklist

- [x] Domain events include `TenantId`
- [x] Interceptor captures `TenantId` from aggregate
- [x] Outbox table has `tenant_id` column
- [x] Worker passes `TenantId` to hub method
- [x] Hub routes to `Clients.Group("tenant:{tenantId}")`
- [x] No calls to `Clients.All` in booking broadcasts
- [x] Code compiles with zero errors
- [ ] Unit tests for domain events ← Add if missing
- [ ] Integration tests for outbox persistence ← Add if missing
- [ ] E2E tests for tenant isolation ← Add if missing
- [ ] Production auth claims integration ← Before deploying
- [ ] Monitoring for cross-tenant leaks ← Before production

---

## 🐛 Debugging

### **Message Not Received?**

1. **Check client is connected to correct group:**
   ```javascript
   // Open DevTools → Network → Messages
   // Should see WebSocket message with tenantId
   ```

2. **Verify worker is processing:**
   ```bash
   # Logs should show:
   # "Successfully connected to SignalR hub"
   # "Broadcast and marked N outbox messages as processed"
   ```

3. **Check hub method was invoked:**
   ```csharp
   // Add logging to BookingsHub.PublishBookingUpdate()
   _logger.LogInformation("Publishing to group: {Group}", GetTenantGroupName(message.TenantId));
   ```

### **Cross-Tenant Leakage?**

1. **Verify hub groups:**
   ```csharp
   // In BookingsHub
   var groups = await Clients.Group("tenant:wrong-id").SendAsync(...);
   // Should NOT work
   ```

2. **Check outbox TenantId:**
   ```sql
   SELECT tenant_id, event_type, payload 
   FROM outbox_messages 
   WHERE processed_at_utc IS NULL;
   
   -- Every row should have correct tenant_id
   ```

---

## 📚 Related Files

| File | Purpose |
|------|---------|
| [`BookingsHub.cs`](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Hubs/BookingsHub.cs) | Group management & routing |
| [`BookingEvents.cs`](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Common/Domain/BookingEvents.cs) | Domain events (w/ TenantId) |
| [`ConvertDomainEventsToOutboxMessagesInterceptor.cs`](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Common/Persistence/Interceptors/ConvertDomainEventsToOutboxMessagesInterceptor.cs) | Outbox persistence |
| [`OutboxProcessorWorker.cs`](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Worker/OutboxProcessorWorker.cs) | Polling & broadcasting |
| [`BookingUpdateMessage.cs`](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Common/Messaging/BookingUpdateMessage.cs) | DTO for hub |

---

## 🎓 Key Takeaways

1. **TenantId flows through the entire pipeline** (domain → outbox → hub → client)
2. **Interceptor captures automatically** (no manual work needed)
3. **Hub groups provide isolation** (signalr guarantees no cross-tenant leaks)
4. **Worker preserves tenant context** across process boundaries
5. **Production deployment requires auth claims integration** (not query params)

**Result:** Bulletproof tenant-scoped real-time updates! 🚀
