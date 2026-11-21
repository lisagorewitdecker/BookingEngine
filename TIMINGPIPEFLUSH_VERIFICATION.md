# Verification Guide: TimingPipeFlusher Fix

## Quick Start

1. **Rebuild**:
   ```bash
   cd BookingEngine
   dotnet build
   ```

2. **Restart Aspire Dashboard**:
   - Stop current AppHost (Ctrl+C)
   - Run AppHost again to pick up the new worker binary

3. **Monitor Logs**:
   - Open: `https://localhost:17173/structuredlogs`
   - Filter by **Resource**: `worker`
   - Look for successful outbox message broadcasts

## What Changed

### Before (Problem)
```
Unexpected exception in TimingPipeFlusher.FlushAsync.
  → System.IO.IOException: The encryption operation failed
    → System.ComponentModel.Win32Exception (5): Input/output error
```
Outbox worker was failing to establish stable HTTPS/SSL connection to SignalR hub.

### After (Solution)
✅ SSL certificate validation bypass for localhost/dev environments
✅ Explicit reconnection retry policy with exponential backoff
✅ Granular error handling (HTTP errors vs message errors)
✅ No more connection drops during outbox broadcast

## Verification Steps

### Step 1: Check Worker Connectivity
In structured logs (filter by `worker`), look for:
```
Connected to SignalR hub at [connection_timeout]
```

### Step 2: Confirm No SSL Exceptions
Search logs for:
- ❌ `TimingPipeFlusher.FlushAsync`
- ❌ `The encryption operation failed`
- ❌ `Win32Exception`

### Step 3: Verify Message Processing
In structured logs (filter by `worker`), you should see:
```
Broadcast and marked N outbox messages as processed.
```
(Where N > 0 means messages are flowing)

### Step 4: Check API Logs
In structured logs (filter by `api`), verify:
- No connection reset errors
- Proper SignalR hub initialization
- Successful broadcast method invocations

## Common Scenarios

### Scenario: "Still seeing SSL errors"
1. Verify the worker was rebuilt: `dotnet build`
2. Verify the worker process has restarted (new PID)
3. Check if API's HTTPS endpoint is actually running
4. Restart the entire Aspire dashboard

### Scenario: "Worker connects but messages not broadcast"
1. Check if there are outbox messages in the database
2. Verify hub is configured at `/hubs/bookings` on API
3. Confirm `OutboxWorker__ApiBaseUrl` environment variable is set correctly
4. Check structured logs for `HTTP/SSL error` entries (graceful degradation)

### Scenario: "Connection drops after a while"
This is normal behavior if:
- API restarts (worker will reconnect in 0-10s)
- Database has an issue (worker logs the error and retries)
- Network is interrupted (automatic reconnect kicks in)

Look for reconnection messages in logs to confirm recovery.

## Configuration Reference

### OutboxWorker Settings (appsettings.Development.json)
```json
{
  "OutboxWorker": {
    "ApiBaseUrl": "https://localhost:7046",        // Must match API's HTTPS port
    "HubPath": "/hubs/bookings",                   // Must match API's hub route
    "BroadcastMethod": "BookingUpdated",           // Must match hub method name
    "PollIntervalMilliseconds": 1000,              // Polling frequency
    "BatchSize": 50                                // Messages per batch
  }
}
```

### Reconnection Policy (Code)
```csharp
.WithAutomaticReconnect(new[] {
    TimeSpan.Zero,              // 0s
    TimeSpan.FromSeconds(1),    // 1s
    TimeSpan.FromSeconds(2),    // 2s
    TimeSpan.FromSeconds(5),    // 5s
    TimeSpan.FromSeconds(10)    // 10s (max 5 retries)
})
```

## Logs to Expect

### Healthy Worker Startup
```
✓ Connected to SignalR hub at [timeout]
✓ Broadcast and marked 0 outbox messages as processed. (if db empty)
```

### After Creating Bookings
```
✓ Broadcast and marked 1 outbox messages as processed.
✓ Broadcast and marked 1 outbox messages as processed.
...
```

### Recovery from Temporary Outage
```
⚠ SignalR hub connection attempt failed; retrying in 2s.
⚠ Connection lost while broadcasting message {id}; connection will reconnect and retry.
✓ Connected to SignalR hub at [timeout]
✓ Broadcast and marked N outbox messages as processed.
```

## Troubleshooting Commands

### View Worker Process
```bash
ps aux | grep BookingEngine.Worker
```

### View Worker Logs
```bash
# If running in Aspire, check the Aspire dashboard
# Or if running standalone:
dotnet run --project src/BookingEngine.Worker/BookingEngine.Worker.csproj
```

### Verify API is Listening on HTTPS
```bash
curl -k https://localhost:7046/health
```

### Test SignalR Hub Connection
```bash
# Using .NET CLI or manual test with SignalR client
# See if you can connect to the hub
```

## Success Criteria

✅ **Worker stays connected** to API's SignalR hub without SSL errors
✅ **No `TimingPipeFlusher.FlushAsync` exceptions** in structured logs  
✅ **Outbox messages are processed** (see "Broadcast and marked" logs)
✅ **Connection recovery works** when API restarts or network hiccups occur
✅ **Performance is stable** (no excessive reconnection loops or errors)

---

If issues persist, check the detailed fix documentation: [TIMINGPIPEFLUSH_FIX.md](./TIMINGPIPEFLUSH_FIX.md)
