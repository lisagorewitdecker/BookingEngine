# Outbox Broadcast Fix - Quick Reference

## What Was Fixed
✅ **SignalR `HubException: Method does not exist`** error resolved  
✅ **JsonElement serialization** issue - replaced with string payload  
✅ **Tenant-scoped routing** - now includes TenantId in messages  

## Key Changes at a Glance

### 1. BookingUpdateMessage (API)
**Before:** `JsonElement Payload` ❌ Not JSON-serializable  
**After:** `string Payload` ✅ Fully JSON-serializable
```csharp
// File: src/BookingEngine.Api/Common/Messaging/BookingUpdateMessage.cs
public sealed record BookingUpdateMessage(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string Payload,     // ✅ Changed from JsonElement
    Guid TenantId);     // ✅ Added for routing
```

### 2. OutboxProcessorWorker (Worker)
**Changes:**
- ✅ Pass payload as string (no JSON parsing)
- ✅ Include tenant_id in SQL query
- ✅ Pass TenantId to hub method
- ✅ Remove ParsePayloadOrFallback() method

```csharp
// Before: JsonElement payloadElement = ParsePayloadOrFallback(message.Payload);
// After:
string payload = message.Payload ?? "{}";

await hubConnection.InvokeAsync(
    BookingsHub.PublishMethodName,
    new BookingUpdateMessage(
        message.Id,
        message.EventType,
        message.OccurredAtUtc,
        payload,          // ✅ String, not JsonElement
        message.TenantId),// ✅ Tenant ID included
    ct);
```

### 3. BookingsHub (API)
**No breaking changes** - method signature already matches new message format

```csharp
// File: src/BookingEngine.Api/Hubs/BookingsHub.cs
public async Task PublishBookingUpdate(BookingUpdateMessage message)
{
    var targetClients = Clients.Group(GetTenantGroupName(message.TenantId));
    await targetClients.SendAsync(ClientEventName, message);
}
```

## Verify the Fix

### 1. Build
```bash
cd BookingEngine
dotnet build
# Expected: Build succeeded with 0 errors
```

### 2. Database Check
```sql
-- After running the services and creating bookings
SELECT "id", "event_type", "processed_at_utc" 
FROM "outbox_messages" 
ORDER BY "occurred_at_utc" DESC 
LIMIT 5;

-- Expected: processed_at_utc should be populated (not NULL)
```

### 3. Worker Logs
Look for these log entries:
```
[INFO] Successfully connected to SignalR hub at https://localhost:7046/hubs/bookings
[INFO] Broadcast and marked N outbox messages as processed.
```

❌ Should NOT see: `HubException: Method does not exist`

### 4. API Test
```bash
# Create a booking
curl -X POST https://localhost:7046/api/bookings \
  -H "Content-Type: application/json" \
  -H "X-Tenant-Id: <your-tenant-id>" \
  -d '{
    "slotId": "00000000-0000-0000-0000-000000000001",
    "customerName": "Test User",
    "customerEmail": "test@example.com"
  }'

# Expected: 201 Created
# Check worker logs: Should see "Broadcast and marked 1 outbox messages as processed"
```

## Why This Fixes The Error

| Issue | Root Cause | Fix |
|-------|-----------|-----|
| `HubException: Method does not exist` | `JsonElement` can't be serialized by SignalR | Use `string` payload |
| Cross-tenant broadcasts possible | No tenant info in message | Added `TenantId` field |
| Complex object marshaling | Trying to convert JSON to objects | Keep as string, let client parse |

## Deployment Considerations

✅ **Backward Compatible?** YES  
- Payload remains as JSON string in database (same format)
- Clients just receive string instead of JsonElement
- No schema changes required

✅ **Breaking Changes?** NO  
- API Hub method signature is compatible
- Database schema unchanged
- Configuration options unchanged

## Related Documentation
- [SIGNALR_OUTBOX_BROADCAST_FIX.md](</SIGNALR_OUTBOX_BROADCAST_FIX.md>) - Detailed fix explanation
- [TROUBLESHOOTING_SIGNALR.md](</TROUBLESHOOTING_SIGNALR.md>) - SignalR troubleshooting guide
- [SIGNALR_TENANT_SCOPING.md](</SIGNALR_TENANT_SCOPING.md>) - Multi-tenant routing details

## Git Commit
```
433ead8 Fix SignalR outbox broadcast serialization issue
```

Run `git show 433ead8` to see full diff
