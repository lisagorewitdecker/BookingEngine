# SignalR Integration - Quick Reference

## API Endpoint

The SignalR Hub is available at:
```
wss://localhost:7046/hubs/bookings  (WebSocket Secure)
ws://localhost:5011/hubs/bookings   (WebSocket)
```

## Client Connection Example (JavaScript)

```typescript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("https://localhost:7046/hubs/bookings")
  .withAutomaticReconnect()
  .build();

// Listen for broadcast messages from the worker
connection.on("BookingUpdated", (message) => {
  console.log("Received booking update:", {
    messageId: message.messageId,
    eventType: message.eventType,
    occurredAt: message.occurredAtUtc,
    payload: message.payload
  });
  
  // Update UI with new booking information
  updateBookingUI(message.payload);
});

connection.onclose(() => console.log("Disconnected"));

// Connect to hub
connection.start()
  .catch(err => console.error("Connection failed:", err));
```

## Broadcast Message Structure

The Worker broadcasts messages with this structure:

```typescript
interface OutboxBroadcastMessage {
  messageId: string;        // GUID
  eventType: string;        // e.g., "booking.created"
  occurredAtUtc: string;    // ISO 8601 timestamp
  payload: object;          // Event data (dynamic JSON)
}
```

## Example Payload

When a booking is created:

```json
{
  "messageId": "550e8400-e29b-41d4-a716-446655440000",
  "eventType": "booking.created",
  "occurredAtUtc": "2026-11-20T16:40:34.634Z",
  "payload": {
    "bookingId": "550e8400-e29b-41d4-a716-446655440001",
    "tenantId": "550e8400-e29b-41d4-a716-446655440002",
    "resourceId": "550e8400-e29b-41d4-a716-446655440003",
    "customerEmail": "customer@example.com",
    "startUtc": "2026-11-25T10:00:00Z",
    "endUtc": "2026-11-25T11:00:00Z",
    "status": "confirmed"
  }
}
```

## Configuration

### Worker Settings (`appsettings.Development.json`)

```json
{
  "OutboxWorker": {
    "ApiBaseUrl": "https://localhost:7046",        // API endpoint (overridden by AppHost)
    "HubPath": "/hubs/bookings",                   // SignalR hub path
    "BroadcastMethod": "BookingUpdated",           // Client method name
    "PollIntervalMilliseconds": 1000,              // Poll delay when idle
    "BatchSize": 50                                // Messages per batch
  }
}
```

### Environment Variables (AppHost)

```
OutboxWorker__ApiBaseUrl=https://localhost:7046  # Set by AppHost automatically
```

The double underscore (`__`) maps to nested configuration properties.

## Database Schema

### Outbox Messages Table

```sql
CREATE TABLE "outbox_messages" (
  "id" UUID PRIMARY KEY,
  "tenant_id" UUID NOT NULL,
  "event_type" VARCHAR(128) NOT NULL,
  "payload" JSONB NOT NULL,
  "occurred_at_utc" TIMESTAMP WITH TIME ZONE NOT NULL,
  "processed_at_utc" TIMESTAMP WITH TIME ZONE,
  "error" TEXT
);

-- Filtered index for fast polling
CREATE INDEX "ix_outbox_messages_occurred_at_utc"
  ON "outbox_messages" ("occurred_at_utc")
  WHERE "processed_at_utc" IS NULL;
```

## Worker Processing Flow

1. **Poll** - Worker queries outbox_messages table every 1 second
2. **Lock** - Uses `FOR UPDATE SKIP LOCKED` for safe concurrent processing
3. **Broadcast** - Sends each message via SignalR Hub to all connected clients
4. **Mark** - Updates `processed_at_utc` timestamp after successful broadcast
5. **Retry** - Implements automatic reconnection with exponential backoff

## Important Property Mappings

| Entity Property | Database Column | C# Type |
|---|---|---|
| Id | id | Guid |
| TenantId | tenant_id | Guid |
| EventType | event_type | string |
| Payload | payload | string (JSONB) |
| OccurredAtUtc | occurred_at_utc | DateTimeOffset |
| ProcessedAtUtc | processed_at_utc | DateTimeOffset? |
| Error | error | string? |

## Configuration Binding Rules

The .NET configuration system uses these rules:

```
JSON Key                    →  Property
OutboxWorker:ApiBaseUrl     →  OutboxWorkerOptions.ApiBaseUrl
OutboxWorker__ApiBaseUrl    →  OutboxWorkerOptions.ApiBaseUrl (via env var)

Section Name: "OutboxWorker" (defined in OutboxWorkerOptions.SectionName)
```

## Running Scenarios

### Scenario 1: AppHost (Production-like)
```bash
dotnet run --project src/BookingEngine.AppHost
# Automatically:
# - Starts PostgreSQL
# - Applies migrations
# - Starts API (https://localhost:7046)
# - Starts Worker with ApiBaseUrl injected
```

### Scenario 2: Individual Development
```bash
# Terminal 1
cd src/BookingEngine.Api && dotnet run

# Terminal 2
cd src/BookingEngine.Worker && dotnet run

# Worker reads ApiBaseUrl from appsettings.Development.json
```

### Scenario 3: Remote API
```json
{
  "OutboxWorker": {
    "ApiBaseUrl": "https://api.example.com"
  }
}
```

## Logging

### Worker Logs

```
[Information] Connected to SignalR hub at ...
[Information] Broadcast and marked 10 outbox messages as processed.
[Warning] SignalR hub connection attempt failed; retrying in 2s.
[Error] Error while polling and broadcasting outbox messages.
```

## Troubleshooting

| Issue | Root Cause | Solution |
|---|---|---|
| Worker can't connect | API not running or wrong URL | Check ApiBaseUrl in appsettings |
| No messages broadcast | Outbox table is empty | Check if domain events are being persisted |
| Connection refused | Hub not mapped | Verify app.MapHub<BookingsHub>() in Program.cs |
| Outdated messages | ProcessedAtUtc not updating | Check database transaction handling |
| High CPU usage | Batch size too large | Reduce BatchSize in appsettings |
| Message delay | Poll interval too long | Reduce PollIntervalMilliseconds |

## Performance Tuning

```json
{
  "OutboxWorker": {
    "BatchSize": 50,                    // Increase for high throughput
    "PollIntervalMilliseconds": 1000    // Decrease for lower latency
  }
}
```

**Recommendations:**
- Low volume (< 100 messages/sec): BatchSize 50, PollInterval 1000ms
- Medium volume (100-1000 messages/sec): BatchSize 100, PollInterval 500ms
- High volume (> 1000 messages/sec): BatchSize 200, PollInterval 250ms

## Security Considerations

### CORS Configuration (if frontend on different domain)
```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins("https://app.example.com")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials());
});

app.UseCors("AllowFrontend");
```

### Authentication (if required)
```csharp
app.MapHub<BookingsHub>("/hubs/bookings")
   .RequireAuthorization();
```

## Health Checks

The Worker implements automatic health management:

```csharp
// Connection states
HubConnectionState.Connected      // OK - processing messages
HubConnectionState.Connecting     // Attempting to connect
HubConnectionState.Disconnected   // Will retry
HubConnectionState.Reconnecting   // Will retry

// Automatic reconnection
.WithAutomaticReconnect()  // Built-in exponential backoff
```

Monitor the Worker logs for:
- `Connected to SignalR hub` - Connection established
- `connection attempt failed; retrying in 2s` - Retry in progress
- `Error while polling` - Database or broadcast error

## Testing

### Unit Test Example

```csharp
[Test]
public async Task ProcessBatch_BroadcastsMessagesToHub()
{
    // Arrange
    var hubConnection = Substitute.For<HubConnection>();
    var dbContext = new AppDbContext(options);
    var message = new OutboxMessage 
    { 
        Id = Guid.NewGuid(),
        EventType = "booking.created",
        Payload = "{\"bookingId\":\"123\"}"
    };
    
    // Act
    await processBatch.InvokeAsync(hubConnection);
    
    // Assert
    await hubConnection.Received()
        .InvokeAsync("BookingUpdated", Arg.Any<OutboxBroadcastMessage>());
}
```

### Integration Test Example

```csharp
[Test]
public async Task SignalRHub_ReceivesMessageFromWorker()
{
    // Arrange - Start API and Worker
    var app = new WebApplicationFactory<Program>();
    var client = app.CreateClient();
    
    // Act - Create booking (which queues outbox message)
    await client.PostAsJsonAsync("/api/bookings", bookingRequest);
    
    // Assert - Wait for SignalR message
    await Task.Delay(2000); // Wait for worker to process
    Assert.That(receivedMessages, Has.Count.GreaterThan(0));
}
```

## Deployment Checklist

- [ ] API endpoint (ApiBaseUrl) accessible from Worker
- [ ] SSL certificate configured for HTTPS
- [ ] Database migrations applied to production
- [ ] Environment variables set for production API endpoint
- [ ] CORS policy configured for frontend domain
- [ ] Authentication/authorization configured if required
- [ ] Load balancer configured for SignalR (sticky sessions)
- [ ] Monitoring/alerting set up for Worker health
- [ ] Backup strategy for outbox table in case of prolonged outages
- [ ] Log aggregation configured for Worker logs

---

**Version:** 1.0  
**Last Updated:** 2026-11-20  
**Status:** ✅ Implementation Complete
