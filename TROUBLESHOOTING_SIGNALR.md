# SignalR Connection Troubleshooting Guide

## Error: "Cannot send data if the connection is not in the 'Connected' State"

### What This Means
The OutboxProcessorWorker tried to send a message via SignalR to the API hub, but the connection was in a disconnected, reconnecting, or disconnected state.

### Common Causes & Solutions

#### 1. API Server is Down or Not Responding
**Symptoms:**
- Error happens immediately when worker starts
- Logs show repeated connection attempts failing

**Solution:**
```bash
# Terminal 1: Start the API
cd src/BookingEngine.Api
dotnet run

# Terminal 2: Start the Worker
cd src/BookingEngine.Worker
dotnet run
```

Or use the AppHost to orchestrate both:
```bash
cd src/BookingEngine.AppHost
dotnet run
```

#### 2. Wrong API URL in Configuration
**Check:** `src/BookingEngine.Worker/appsettings.Development.json`

```json
{
  "OutboxWorker": {
    "ApiBaseUrl": "https://localhost:7046"  // ← Verify this matches your API's actual URL
  }
}
```

**Find your API's correct URL:**
```bash
# Check API's launchSettings.json
cat src/BookingEngine.Api/Properties/launchSettings.json | grep -E '"applicationUrl"'
```

#### 3. Network/Firewall Issues
**Symptoms:**
- Connection fails with timeout errors
- Worker logs show "retrying in 2s" repeatedly

**Solution:**
- Verify the API port is accessible: `curl -k https://localhost:7046/health`
- Check firewall rules allow localhost traffic
- If using Docker/containers, ensure networking is configured

#### 4. HTTPS Certificate Issues
**Symptoms:**
- Error mentioning certificate validation
- Connection fails when using `https://` URLs

**Solution:**
In development, you can test with HTTP URLs first or configure certificate validation.

#### 5. Database Connection Issues
**Symptoms:**
- Worker can't query outbox messages
- Worker crashes or hangs during batch processing

**Solution:**
```bash
# Verify PostgreSQL is running
docker ps | grep postgres

# Check connection string in appsettings.Development.json
cat src/BookingEngine.Worker/appsettings.Development.json | grep -A2 ConnectionStrings

# Test connection
psql -h localhost -U postgres -d bookingdb -c "SELECT COUNT(*) FROM outbox_messages;"
```

#### 6. Rate Limiting or Backpressure
**Symptoms:**
- Worker processes few messages then stops
- Logs show "deferring batch processing"

**Solution:**
Adjust worker settings in `appsettings.Development.json`:
```json
{
  "OutboxWorker": {
    "BatchSize": 50,                      // Reduce if rate-limited
    "PollIntervalMilliseconds": 1000      // Increase delay between polls
  }
}
```

---

## Diagnostic Checklist

### Quick Verification
```bash
# 1. API is running and healthy
curl -k https://localhost:7046/health

# 2. SignalR hub is accessible
curl -k https://localhost:7046/hubs/bookings

# 3. Database is running
docker ps | grep postgres

# 4. Database migrations applied
docker exec <postgres-container> psql -U postgres -d bookingdb -c "\dt"

# 5. Outbox table exists
docker exec <postgres-container> psql -U postgres -d bookingdb -c "SELECT COUNT(*) FROM outbox_messages;"
```

### Log Analysis
Watch these log locations:

**API Logs:**
```bash
# Real-time API logs (if running standalone)
dotnet run --configuration Debug

# Or check for recent errors
# Look for: "Hubs/bookings" endpoint errors
```

**Worker Logs:**
```
INFO: Connected to SignalR hub at ...          ✓ Connection successful
WARN: SignalR hub connection attempt failed    ✗ Connection problem
WARN: Hub connection is not connected          ✗ Disconnected mid-batch
INFO: Broadcast and marked N messages          ✓ Messages sent successfully
```

---

## Connection State Transitions

```
┌─────────────────┐
│   Disconnected  │
│    (Initial)    │
└────────┬────────┘
         │ StartAsync()
         ▼
┌─────────────────────────────────────┐
│          Connected                  │◄─────────┐
│    (Ready to send messages)         │          │
└────────┬────────────────────────────┘          │
         │                                       │
         │ Network issue,                        │
         │ timeout, server down                  │
         ▼                                       │
┌─────────────────────────────────────┐         │
│       Reconnecting                  │─────────┘
│  (Automatic retry with backoff)     │
└─────────────────────────────────────┘

✗ Error: "not in 'Connected' State" can occur if:
  • Attempting to send while in Disconnected state
  • Attempting to send while in Reconnecting state
  • Connection drops between state check and send
```

---

## Recovery Mechanism

The worker has built-in recovery:

```
1. Detect Connection Lost
   ↓
2. Log warning (connection details, message ID)
   ↓
3. Rollback database transaction
   ↓
4. Wait 2 seconds
   ↓
5. Attempt reconnect with exponential backoff
   ↓
6. Resume batch processing
   ↓
7. Unprocessed messages reprocessed on next iteration
```

**Result:** Messages are never lost; they remain in the outbox until successfully broadcast.

---

## Performance Tuning

If you see warnings about connection issues, adjust these settings:

```json
{
  "OutboxWorker": {
    "ApiBaseUrl": "https://localhost:7046",
    "PollIntervalMilliseconds": 1000,     // ↑ Increase for high-latency networks
    "BatchSize": 50                        // ↓ Decrease if messages aren't processed
  }
}
```

**Guidance:**
- **High message throughput?** Keep BatchSize large (50-100)
- **Network latency?** Increase PollInterval (1000-5000ms)
- **Low latency, responsive UI?** Keep PollInterval small (500-1000ms)

---

## Testing the Fix

### Scenario 1: Normal Operation
```bash
# Terminal 1
cd src/BookingEngine.Api && dotnet run

# Terminal 2
cd src/BookingEngine.Worker && dotnet run

# Terminal 3: Create a test message
# Use the API endpoints to create a booking
curl -X POST https://localhost:7046/api/bookings \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"...","data":"..."}'

# Worker should broadcast it immediately
# Check Worker logs for: "Broadcast and marked 1 outbox messages as processed"
```

### Scenario 2: Connection Recovery
```bash
# Terminal 1: Start API
cd src/BookingEngine.Api && dotnet run

# Terminal 2: Start Worker (should connect successfully)
cd src/BookingEngine.Worker && dotnet run

# Terminal 3: Stop API (Ctrl+C)
# Watch Worker logs - should see reconnection attempts

# Restart API in Terminal 1
# Worker should reconnect and resume processing
```

---

## Still Having Issues?

**Enable verbose logging:**

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

**Common error messages and what they mean:**
- `"not in 'Connected' State"` → Connection lost, will recover automatically
- `"retrying in 2s"` → Initial connection failed, trying again
- `"timeout"` → API not responding, check if it's running
- `"Certificate validation failed"` → HTTPS cert issue

**Next steps if still failing:**
1. Check both API and Worker are running: `ps aux | grep dotnet`
2. Verify network connectivity: `ping localhost`
3. Check database: `docker ps`
4. Review recent git changes to SignalR configuration
5. Clear build artifacts: `dotnet clean && dotnet build`
