# SignalR Fix Verification Steps

## Status: ✅ AppHost Started Successfully

### Services Running
- **AppHost**: PID 29872 (Aspire orchestrator)
- **Worker**: PID 29946 (OutboxProcessorWorker with SignalR connection)
- **API**: Listening on 7046 (HTTPS) & 5011 (HTTP)
- **Dashboard**: Listening on 17173
- **PostgreSQL**: Running on 55432

## How to Verify the Fix

### 1. Open the Aspire Dashboard
```
https://localhost:17173/
```
You should see:
- ✓ `api` service (green/running)
- ✓ `worker` service (green/running)
- ✓ `postgres` service (green/running)

### 2. Check Worker Connection Status
In the AppHost/Worker terminal output, look for:
```
INFO: Connected to SignalR hub at ...
```

This means the Worker successfully established the SignalR connection with the API without the "not in Connected State" error.

### 3. Create a Test Booking to Verify Broadcasting
```bash
curl -X POST https://localhost:7046/api/bookings \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: $(uuidgen)" \
  -d '{
    "availabilityScheduleId": "...",
    "startTime": "2026-11-21T14:00:00Z",
    "endTime": "2026-11-21T15:00:00Z",
    "customerName": "Test Customer",
    "customerEmail": "test@example.com"
  }'
```

### 4. Verify Message Was Broadcast
In the Worker logs, you should see:
```
INFO: Broadcast and marked 1 outbox messages as processed.
```

This confirms:
- ✓ Message was queued in the outbox
- ✓ Worker retrieved it successfully
- ✓ Worker broadcast it via SignalR without connection errors
- ✓ Message was marked as processed

## Understanding the Fix

The error **"Cannot send data if the connection is not in the 'Connected' State"** has been fixed with two key changes:

### Change 1: Pre-Broadcast Verification
Before processing any batch of messages, the worker now checks if the connection is in "Connected" state:
```csharp
if (hubConnection.State != HubConnectionState.Connected)
{
    await transaction.RollbackAsync(stoppingToken);
    _logger.LogWarning(
        "Hub connection is not connected (state: {State}); deferring batch processing.",
        hubConnection.State);
    return 0;
}
```

**Why this matters:** This catches connection issues early and prevents attempting to send on a closed connection.

### Change 2: Per-Message Exception Handling
Each message send is wrapped in a try-catch that specifically catches the "not in Connected State" error:
```csharp
catch (InvalidOperationException ex) 
    when (ex.Message.Contains("not in the 'Connected' State"))
{
    await transaction.RollbackAsync(stoppingToken);
    _logger.LogWarning(
        ex,
        "Connection lost while broadcasting message {MessageId}; connection will reconnect and retry.",
        message.Id);
    return 0;
}
```

**Why this matters:** Even if the connection drops mid-broadcast, the transaction is rolled back so the message isn't lost—it stays in the outbox for the next retry attempt.

## Recovery Behavior

If the connection is lost:
1. Worker logs the event with specific message ID
2. Database transaction is rolled back
3. Worker waits 2 seconds
4. Automatic reconnection begins with exponential backoff
5. Messages remain in outbox table (not marked as processed)
6. Next batch processing cycle will retry

**Result:** No messages are lost, and the connection will recover automatically.

## Expected Log Output

### Success Scenario
```
INFO: Connected to SignalR hub at ...
INFO: Broadcast and marked 50 outbox messages as processed.
INFO: Broadcast and marked 25 outbox messages as processed.
```

### Connection Recovery Scenario
```
WARN: SignalR hub connection attempt failed; retrying in 2s.
WARN: SignalR hub connection attempt failed; retrying in 2s.
INFO: Connected to SignalR hub at ...
INFO: Broadcast and marked 50 outbox messages as processed.
```

### Connection Lost During Broadcast (Edge Case)
```
WARN: Connection lost while broadcasting message {guid}; connection will reconnect and retry.
WARN: SignalR hub connection attempt failed; retrying in 2s.
INFO: Connected to SignalR hub at ...
INFO: Broadcast and marked 1 outbox messages as processed.
```

## Common Checks

### Check 1: Is the API actually running?
```bash
# Should show output
lsof -nP -iTCP:7046 -sTCP:LISTEN
```

### Check 2: Is the Worker connected?
```bash
# Check AppHost/Worker logs for:
# "Connected to SignalR hub at"
# or
# "retrying in 2s" (if still attempting)
```

### Check 3: Is the database accessible?
```bash
# Should show the outbox_messages table
docker exec $(docker ps -q -f "ancestor=postgres:16") \
  psql -U postgres -d bookingdb -c "\dt outbox_messages"
```

### Check 4: Are there unprocessed messages?
```bash
# Should return 0 (messages processed) or count of pending
docker exec $(docker ps -q -f "ancestor=postgres:16") \
  psql -U postgres -d bookingdb \
  -c "SELECT COUNT(*) FROM outbox_messages WHERE processed_at_utc IS NULL"
```

## Next Steps

1. **Monitor for a few seconds** - Watch the terminal output for "Connected to SignalR hub" message
2. **Create a test booking** - Use the API endpoints to trigger an outbox message
3. **Verify broadcast** - Look for "Broadcast and marked N messages" in logs
4. **Test reconnection** (optional) - Stop and restart the API to test recovery

## Troubleshooting

If you still see connection errors:

1. **Check both API and Worker are running**
   ```bash
   ps aux | grep "dotnet.*Worker"
   ps aux | grep "dotnet.*Api"
   ```

2. **Verify port availability**
   ```bash
   lsof -nP -iTCP:7046,5011,17173 -sTCP:LISTEN
   ```

3. **Check Worker configuration**
   - Verify `ApiBaseUrl` in `appsettings.Development.json` matches the actual API URL
   - Should be `https://localhost:7046` or `http://localhost:5011`

4. **Enable debug logging**
   Update `appsettings.Development.json`:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "Default": "Debug",
         "Microsoft.AspNetCore.SignalR": "Debug"
       }
     }
   }
   ```

## Files Changed

- **src/BookingEngine.Worker/OutboxProcessorWorker.cs**
  - Added pre-broadcast connection state verification
  - Added per-message exception handling for connection errors
  - Improved logging for connection status changes

## Build Status
✅ Solution builds successfully
✅ No compilation errors
✅ All projects included in build
