# SignalR Connection Error Fix

## Problem
The error **"Cannot send data if the connection is not in the 'Connected' State"** occurred when the OutboxProcessorWorker attempted to broadcast messages via SignalR.

### Root Cause
The connection state was checked in `EnsureConnectedAsync()`, but the connection could transition to a disconnected state *between that check and the actual `InvokeAsync()` call*. This race condition caused the error because:

1. `EnsureConnectedAsync()` verified the connection was Connected
2. Network hiccup or timeout occurred
3. Connection state changed to Disconnected/Reconnecting
4. `InvokeAsync()` attempted to send data on a non-connected connection

## Solution
Added robust connection state handling in the `ProcessBatchAsync` method:

### Changes Made (OutboxProcessorWorker.cs)

1. **Pre-broadcast Connection Verification**
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
   This check happens immediately before broadcasting, reducing the race condition window.

2. **Per-Message Exception Handling**
   ```csharp
   try
   {
       await hubConnection.InvokeAsync(
           _options.BroadcastMethod,
           new OutboxBroadcastMessage(...),
           stoppingToken);
       processedIds.Add(message.Id);
   }
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
   Catches the specific error if it still occurs and gracefully handles it by:
   - Rolling back the transaction (messages remain unprocessed)
   - Logging the event with the message ID for debugging
   - Returning 0 to trigger reconnection logic
   - The automatic reconnect mechanism kicks in on the next iteration

3. **Transaction Rollback on Failure**
   If the connection is not ready or becomes unavailable, the transaction is rolled back so messages aren't marked as processed when they haven't actually been sent.

## How It Works

**Normal Flow:**
1. Worker queries for unprocessed outbox messages
2. Begins transaction
3. **NEW:** Verifies connection is Connected before attempting broadcast
4. Broadcasts each message via SignalR
5. Updates `processed_at_utc` for all successfully broadcast messages
6. Commits transaction

**Error Recovery Flow:**
1. If connection is not Connected → Rollback, return 0, wait, reconnect
2. If connection drops during broadcast → Catch exception, rollback, return 0, wait, reconnect
3. The `ExecuteAsync()` main loop automatically handles reconnection via `ConnectHubWithRetryAsync()` with exponential backoff

## Benefits

✅ **Prevents "not in Connected state" errors** from reaching unhandled exceptions  
✅ **Atomic transaction behavior** - messages either fully broadcast or don't mark as processed  
✅ **Automatic recovery** - worker reconnects and retries unprocessed messages  
✅ **Better logging** - specific error details help diagnose connection issues  
✅ **Race condition resilience** - additional state check immediately before broadcast  

## Testing the Fix

### Scenario 1: Normal Operation
```
✓ Messages broadcast successfully
✓ Marked as processed
✓ No errors in logs
```

### Scenario 2: API Offline During Processing
```
✓ Worker detects disconnected state
✓ Transaction rolls back
✓ Messages remain unprocessed
✓ Worker waits 2 seconds
✓ Reconnects automatically
✓ Messages processed on next batch
```

### Scenario 3: Network Timeout Mid-Broadcast
```
✓ Exception caught in try-catch
✓ Transaction rolls back
✓ Message remains unprocessed
✓ Worker reconnects
✓ Message processed on next batch
```

## Configuration
The OutboxWorkerOptions (appsettings.Development.json) control retry behavior:

```json
{
  "OutboxWorker": {
    "ApiBaseUrl": "https://localhost:7046",
    "HubPath": "/hubs/bookings",
    "BroadcastMethod": "BookingUpdated",
    "PollIntervalMilliseconds": 1000,
    "BatchSize": 50
  }
}
```

**Key settings:**
- **PollIntervalMilliseconds**: Delay between polls when no messages (default: 1000ms)
- **BatchSize**: Max messages per batch (default: 50)
- **ApiBaseUrl**: Must match the actual API URL for the environment

## Monitoring

Watch for these log messages in development:

**Success:**
```
Broadcast and marked 5 outbox messages as processed.
```

**Connection Lost During Batch:**
```
WARN: Hub connection is not connected (state: Disconnected); deferring batch processing.
```

**Connection Lost During Message:**
```
WARN: Connection lost while broadcasting message {guid}; connection will reconnect and retry.
```

**Reconnection Attempts:**
```
WARN: SignalR hub connection attempt failed; retrying in 2s.
INFO: Connected to SignalR hub at ...
```

## Files Modified
- `src/BookingEngine.Worker/OutboxProcessorWorker.cs` - Added connection verification and exception handling

## Verification
Run the application with both API and Worker:
```bash
cd src/BookingEngine.AppHost
dotnet run
```

The AppHost will start both services and coordinate startup. Monitor logs for successful broadcasts without connection errors.
