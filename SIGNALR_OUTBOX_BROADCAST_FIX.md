# SignalR Outbox Broadcast Fix: HubException Resolution

## Problem
The `OutboxProcessorWorker` was encountering `HubException: Method does not exist` when attempting to broadcast `BookingUpdated` events to connected SignalR clients. The root cause was related to serialization compatibility between the `OutboxProcessorWorker` (a separate process) and the `BookingsHub` (running in the API).

## Root Cause Analysis

### Issue 1: `JsonElement` Serialization
The original `BookingUpdateMessage` used `JsonElement` for the `Payload` field:
```csharp
public sealed record BookingUpdateMessage(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    JsonElement Payload,  // ❌ Not JSON-serializable across process boundaries
    Guid TenantId);
```

**Why this failed:**
- `JsonElement` is a low-level type designed for in-memory JSON operations
- When crossing process boundaries via SignalR, SignalR's JSON serializer cannot properly serialize/deserialize `JsonElement`
- This caused SignalR to fail during method invocation, resulting in "Method does not exist" (the method signature couldn't be matched)

### Issue 2: Missing Tenant Context
The outbox worker wasn't including tenant information when broadcasting, which is required for proper multi-tenant routing.

## Solution

### Change 1: Updated `BookingUpdateMessage` ✅
Changed the `Payload` field from `JsonElement` to `string`:
```csharp
// File: src/BookingEngine.Api/Common/Messaging/BookingUpdateMessage.cs
public sealed record BookingUpdateMessage(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string Payload,  // ✅ JSON string - fully serializable
    Guid TenantId);  // ✅ Includes tenant for group routing
```

**Benefits:**
- Strings are universally JSON-serializable
- Clients can easily parse `Payload` as needed (JSON.parse in TypeScript/JavaScript)
- Maintains backward compatibility with the outbox payload storage format

### Change 2: Updated `OutboxProcessorWorker` ✅
Modified to pass payload as a string instead of converting to `JsonElement`:

```csharp
// File: src/BookingEngine.Worker/OutboxProcessorWorker.cs
foreach (var message in batch)
{
    string payload = message.Payload ?? "{}";  // ✅ Use string directly

    try
    {
        await hubConnection.InvokeAsync(
            BookingsHub.PublishMethodName,
            new BookingUpdateMessage(
                message.Id,
                message.EventType,
                message.OccurredAtUtc,
                payload,              // ✅ Pass string
                message.TenantId),    // ✅ Pass tenant ID
            ct);
        // ...
    }
}
```

**Changes:**
- Removed `ParsePayloadOrFallback()` method (no longer needed)
- Removed unused `using System.Text.Json;` import
- Added tenant ID selection in SQL query
- Pass payload directly as a string

### Change 3: Enhanced Certificate Validation ✅
Improved self-signed certificate handling to work reliably across connection attempts:

```csharp
// Each HubConnection attempt now gets a fresh HttpClientHandler
.WithUrl(hubUrl, options => options.HttpMessageHandlerFactory = _ =>
{
    var handler = new HttpClientHandler();
    if (allowSelfSigned)
    {
        handler.ServerCertificateCustomValidationCallback = ValidateServerCertificate;
    }
    return handler;
})
```

**Benefits:**
- Prevents handler disposal issues
- More robust SSL certificate handling

### Change 4: `BookingsHub` Documentation ✅
Enhanced method documentation for clarity:

```csharp
/// <summary>
/// Server hub method invoked by the outbox worker (as a SignalR client).
/// Receives the booking update and broadcasts it to all clients in the tenant group.
/// </summary>
public async Task PublishBookingUpdate(BookingUpdateMessage message)
{
    var targetClients = Clients.Group(GetTenantGroupName(message.TenantId));
    await targetClients.SendAsync(ClientEventName, message);
}
```

## Verification Steps

### 1. Build Verification
```bash
dotnet build
# ✅ Build succeeded (no errors, expected MSB3277 warnings for EF Core version conflicts are acceptable)
```

### 2. Runtime Verification
Once services are running, verify outbox processing:

```sql
-- Check if outbox messages are being marked as processed
SELECT
    "id",
    "event_type",
    "occurred_at_utc",
    "processed_at_utc"
FROM "outbox_messages"
ORDER BY "occurred_at_utc" DESC
LIMIT 20;
```

Expected result: `processed_at_utc` should be populated once the worker processes messages.

### 3. API Test
Send a booking creation request and verify the outbox flow:

```bash
# Create a booking (adjust tenantId as needed)
curl -X POST https://localhost:7046/api/bookings \
  -H "Content-Type: application/json" \
  -H "X-Tenant-Id: <your-tenant-id>" \
  -d '{"slotId":"<slot-id>","customerName":"John Doe","customerEmail":"john@example.com"}'
```

### 4. Worker Logs
The worker logs should show:
```
[INFO] Successfully connected to SignalR hub at https://localhost:7046/hubs/bookings
[INFO] Broadcast and marked N outbox messages as processed.
```

No `HubException` should appear.

## Files Modified

| File | Changes | Impact |
|------|---------|--------|
| [src/BookingEngine.Api/Common/Messaging/BookingUpdateMessage.cs](</src/BookingEngine.Api/Common/Messaging/BookingUpdateMessage.cs>) | Changed `Payload` from `JsonElement` to `string`; added `TenantId` field | ✅ Fixes serialization issue; enables tenant-scoped routing |
| [src/BookingEngine.Worker/OutboxProcessorWorker.cs](</src/BookingEngine.Worker/OutboxProcessorWorker.cs>) | Removed JSON parsing; pass payload as string; include tenant ID in SQL and broadcast | ✅ Aligns with new message format; enables proper routing |
| [src/BookingEngine.Api/Hubs/BookingsHub.cs](</src/BookingEngine.Api/Hubs/BookingsHub.cs>) | Added comprehensive documentation | ✅ Clarifies RPC vs client method distinction |

## Why This Works

### SignalR Serialization Contract
- SignalR uses `System.Text.Json` to serialize method arguments
- Simple types (string, Guid, int, bool, etc.) are natively supported
- Complex types must be serializable by `System.Text.Json`
- `JsonElement` is not marked as `[JsonSerializable]`, so it fails

### Multi-Tenant Routing
- The worker now includes `TenantId` in the message
- `BookingsHub.PublishBookingUpdate()` routes to `tenant:{tenantId}` group
- Clients subscribe to their tenant group via `JoinTenantGroup()`
- Cross-tenant broadcasts are impossible

### Process Boundary Communication
- Worker → API communication is now fully JSON-compatible
- Payload remains as a JSON string, avoiding in-memory object marshaling
- The API hub can send the exact message to clients without re-serialization

## Post-Fix Verification Checklist

- [ ] Project builds without errors: `dotnet build`
- [ ] Worker connects to hub: Check logs for "Successfully connected to SignalR hub"
- [ ] Outbox messages are processed: Check database `processed_at_utc` column
- [ ] No HubException errors in worker logs
- [ ] Clients receive BookingUpdated events on correct tenant group
- [ ] Cross-tenant messages are not routed (security check)

## Troubleshooting

### If "Method does not exist" still occurs:
1. **Verify hub registration:** Check `Program.cs` has `app.MapHub<BookingsHub>("/hubs/bookings")`
2. **Check assembly versions:** EF Core version mismatches can cause issues (warnings in build output are OK)
3. **Restart services:** Ensure both API and Worker are updated and restarted
4. **Check certificates:** If using HTTPS, ensure certificate is valid or `AllowSelfSignedCertificates` is true in appsettings

### If outbox messages aren't processing:
1. Check worker connection: Logs should show "Successfully connected to SignalR hub"
2. Verify database: Ensure `outbox_messages` table exists and has data
3. Check EF Core query: Run the SQL query manually to verify it returns results
4. Verify tenant context: Worker includes tenant ID in outbox selection

## References

- [SignalR Type Support](https://learn.microsoft.com/en-us/aspnet/core/signalr/supported-platforms#net-client-5)
- [System.Text.Json Serialization](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/)
- [SignalR Hub Communication](https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs)
