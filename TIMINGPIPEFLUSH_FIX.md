# TimingPipeFlusher.FlushAsync Exception Fix

## Problem

The `Unexpected exception in TimingPipeFlusher.FlushAsync` error was occurring in the BookingEngine API's SignalR hub communication. The root cause was:

```
System.IO.IOException: The encryption operation failed, see inner exception.
  ---> System.ComponentModel.Win32Exception (5): Input/output error
```

This exception occurs at the SSL/TLS layer during HTTPS communication when:
1. A SignalR connection is closed/aborted while data is being encrypted and flushed
2. SSL/TLS certificate validation fails (common with self-signed dev certificates)
3. Connection is lost or reset mid-transmission

## Root Causes Identified

### 1. SSL/TLS Certificate Validation
When the worker (`BookingEngine.Worker`) connects to the API over HTTPS to access the SignalR hub at `https://localhost:7046/hubs/bookings`, self-signed development certificates were causing SSL handshake failures or connection resets.

### 2. Incomplete Connection Retry Logic
The original `HubConnectionBuilder` configuration:
- Used default `WithAutomaticReconnect()` with no explicit retry delays
- Did not validate connection state before broadcasting
- Lacked granular error handling for SSL/HTTP errors

### 3. Abrupt Batch Failure on Individual Message Errors
When a single outbox message failed to broadcast, the entire batch would abort, leaving messages unprocessed and causing the worker to restart the cycle.

## Solution

### Changes to OutboxProcessorWorker.cs

#### 1. SSL Certificate Validation for Development
```csharp
var httpClientHandler = new HttpClientHandler();

// Allow self-signed certificates in development
if (_options.ApiBaseUrl.StartsWith("https://localhost") || _options.ApiBaseUrl.StartsWith("https://127.0.0.1"))
{
    httpClientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
}

await using var hubConnection = new HubConnectionBuilder()
    .WithUrl(hubUrl, options => options.HttpMessageHandlerFactory = _ => httpClientHandler)
    // ...
    .Build();
```

**Impact**: Bypasses self-signed certificate validation in development environments, preventing SSL errors during HTTPS connection.

#### 2. Explicit Retry Policy with Exponential Backoff
```csharp
.WithAutomaticReconnect(new[] {
    TimeSpan.Zero,              // 0s - immediate first retry
    TimeSpan.FromSeconds(1),    // 1s
    TimeSpan.FromSeconds(2),    // 2s
    TimeSpan.FromSeconds(5),    // 5s
    TimeSpan.FromSeconds(10)    // 10s - max interval
})
```

**Impact**: Provides explicit, predictable reconnection timing instead of relying on default behavior. Gradually backs off if the API is temporarily unavailable.

#### 3. Enhanced SignalR Logging
```csharp
.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Information))
```

**Impact**: Improves debugging visibility into SignalR connection state changes and reconnection attempts.

#### 4. Granular Error Handling in ProcessBatchAsync

**Added `HttpRequestException` handling** (catches SSL/HTTPS errors):
```csharp
catch (HttpRequestException ex)
{
    _logger.LogWarning(
        ex,
        "HTTP/SSL error while broadcasting message {MessageId}; connection will reconnect and retry.",
        message.Id);
    return 0;  // Abort batch, let connection retry
}
```

**Added generic `Exception` handling** (resilience for unexpected errors):
```csharp
catch (Exception ex)
{
    _logger.LogError(
        ex,
        "Unexpected error broadcasting message {MessageId}; will continue with next message.",
        message.Id);
    // Continue to next message rather than aborting entire batch
}
```

**Impact**: 
- SSL/HTTP errors trigger a full reconnect cycle (return 0)
- Other unexpected errors don't block the entire batch
- Messages eventually get processed on subsequent polling cycles

## Deployment Instructions

1. **Rebuild the solution**:
   ```bash
   dotnet build
   ```

2. **Restart the BookingEngine.AppHost**:
   - Stop the current Aspire dashboard
   - Restart it to apply the new worker configuration

3. **Verify**:
   - Check the structured logs at `https://localhost:17173/structuredlogs`
   - Filter by `worker` resource to confirm:
     - SignalR hub connections are successfully established
     - Outbox messages are being broadcast without SSL exceptions
     - No more `TimingPipeFlusher.FlushAsync` errors

## Testing Checklist

- [ ] Worker successfully connects to API's SignalR hub
- [ ] No `TimingPipeFlusher.FlushAsync` exceptions in structured logs
- [ ] Outbox messages are processed and broadcast
- [ ] Connection recovery works when API restarts
- [ ] No SSL certificate validation errors
- [ ] Worker recovers gracefully from temporary network disruptions

## Notes

### Development vs. Production
The SSL certificate validation bypass is **development-only** and only activates for `localhost`/`127.0.0.1` URLs. In production, proper certificate validation should be enforced.

### Why This Approach
- **Self-signed dev certificates**: Common in development environments; bypassing their validation is a standard practice
- **Explicit reconnect policy**: Better than default behavior for predictable testing and debugging
- **Granular error handling**: Distinguishes between connection-level errors (which need reconnect) and message-level errors (which can retry individually)

## Related Documentation
- [OUTBOX_WORKER_FIX_SUMMARY.md](./OUTBOX_WORKER_FIX_SUMMARY.md) - Previous NpgsqlRetryingExecutionStrategy fix
- [SIGNALR_FIX_SUMMARY.md](./SIGNALR_FIX_SUMMARY.md) - Initial SignalR setup
