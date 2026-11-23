# OutboxProcessorWorker Malformed JSON Payload Resilience

## Overview

This document describes the implementation of malformed JSON payload handling in the `OutboxProcessorWorker` background service, ensuring that the worker survives corrupted outbox messages without crashing and continues processing subsequent messages.

## Problem

The `OutboxProcessorWorker` polls the `outbox_messages` table for unprocessed events and broadcasts them via SignalR. If a message payload contains malformed JSON, the original implementation would:
1. Attempt to parse/serialize the JSON in the broadcast
2. Throw an exception and potentially crash the entire background worker loop
3. Never recover, leaving the bad message unprocessed forever or causing infinite retry loops

## Solution

### 1. JSON Validation & Error Handling

**File**: [OutboxProcessorWorker.cs](/Users/lmgorewit/Documents/Documents - Lisa M Gorewit-Decker MacBook Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Worker/OutboxProcessorWorker.cs)

The `ProcessBatchAsync` method now wraps JSON parsing in a try-catch block:

```csharp
try
{
    // Validate JSON payload to detect malformed JSON early
    try
    {
        using var _ = JsonDocument.Parse(payload);
    }
    catch (JsonException jsonEx)
    {
        _logger.LogError(
            jsonEx,
            "Malformed JSON payload in outbox message {MessageId}; marking as processed with error logged.",
            message.Id);
        
        // Mark as processed but record the error; skip broadcasting
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE "outbox_messages"
            SET "processed_at_utc" = @p0, "error" = @p1
            WHERE "id" = @p2
            """,
            [DateTimeOffset.UtcNow, $"Malformed JSON: {jsonEx.Message}", message.Id],
            ct);
        
        continue;  // Skip to next message in batch
    }

    // ... broadcast valid JSON message
}
catch (Exception ex)
{
    _logger.LogError(ex, "Unexpected error broadcasting message {MessageId}...", message.Id);
    // Continue to next message rather than aborting entire batch
}
```

**Key behaviors**:
- Detects malformed JSON **before** attempting SignalR broadcast
- Marks the bad message as `processed_at_utc = NOW()` with an error description
- Logs the error for operator visibility
- **Continues to the next message** without interrupting the batch or worker loop
- Never re-processes the same bad message (it's marked processed)

### 2. Testability via IOutboxBroadcaster Interface

**File**: [IOutboxBroadcaster.cs](/Users/lmgorewit/Documents/Documents - Lisa M Gorewit-Decker MacBook Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Worker/IOutboxBroadcaster.cs)

A new interface abstracts the SignalR broadcast mechanism for easier testing:

```csharp
public interface IOutboxBroadcaster
{
    Task BroadcastAsync(BookingUpdateMessage message, CancellationToken cancellationToken);
}
```

**File**: [SignalROutboxBroadcaster.cs](/Users/lmgorewit/Documents/Documents - Lisa M Gorewit-Decker MacBook Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Worker/SignalROutboxBroadcaster.cs)

The implementation wraps the HubConnection:

```csharp
public sealed class SignalROutboxBroadcaster : IOutboxBroadcaster
{
    private readonly HubConnection _hubConnection;

    public async Task BroadcastAsync(BookingUpdateMessage message, CancellationToken cancellationToken)
    {
        await _hubConnection.InvokeAsync(
            BookingsHub.PublishMethodName,
            message,
            cancellationToken);
    }
}
```

This allows unit tests to mock the broadcaster and verify error handling without a real SignalR connection.

### 3. Comprehensive Unit Tests

**File**: [OutboxProcessorWorkerMalformedPayloadTests.cs](/Users/lmgorewit/Documents/Documents - Lisa M Gorewit-Decker MacBook Pro/lisagorewitdecker/BookingEngine/tests/BookingEngine.UnitTests/OutboxProcessorWorkerMalformedPayloadTests.cs)

#### Test Coverage

1. **ProcessBatchAsync_MalformedJsonPayload_LogsErrorAndMarksProcessedWithError**
   - Single malformed message is marked processed with error description
   - No exception escapes the worker loop

2. **ProcessBatchAsync_MalformedPayloadAndValidPayload_ProcessesBothSafely**
   - Mixed batch: one malformed, one valid message
   - Malformed message is logged and marked with error
   - Valid message is successfully broadcast
   - Worker continues without aborting

3. **ProcessBatchAsync_AllMalformedMessages_MarkAllAsProcessedWithErrors**
   - Batch of 3 all-malformed messages
   - All marked as processed with errors
   - Worker completes the batch safely

4. **ProcessBatchAsync_VariousMalformedFormats_AllHandledGracefully** (5 test cases)
   - Unclosed strings: `{ "unclosed": "`
   - Invalid literals: `{ "key": undefined }`
   - Unclosed arrays: `[1, 2, `
   - Missing closing braces: `{ "key": "value" `
   - Each case logs error and marks message as processed

5. **ProcessBatchAsync_EmptyPayload_TreatsAsValidEmptyJson**
   - Empty string `{}` is valid JSON
   - Attempts broadcast without error

6. **ProcessBatchAsync_NullPayload_DefaultsToEmptyJson**
   - Null/empty payload defaults to `{}`
   - Handled gracefully without crash

#### Test Framework
- **xUnit** for test organization
- **NSubstitute** for mocking `IOutboxBroadcaster`
- **EF Core InMemory** for database simulation
- **Custom TestLogger** to capture log messages

#### All Tests Pass ✅
```
Test Run Successful.
Total tests: 10
Passed: 10
Total time: 1.4359 Seconds
```

## Operational Impact

### For Operators

**Monitoring**:
- Check `outbox_messages.error` column for messages marked processed with malformed JSON
- Review logs for entries like: `"Malformed JSON payload in outbox message {Id}..."`
- Alert on sustained error patterns

**Remediation**:
- Investigate source system generating malformed payloads
- Delete/correct bad messages in the database (they're marked processed, so won't retry)
- Restart the worker if needed (should handle gracefully now anyway)

### For Developers

**Data Quality**:
- Ensure event publishers validate JSON before inserting into `outbox_messages`
- Use OutboxPayloadExtractor to safely extract tenant ID from payloads
- Test event schemas end-to-end (include malformed cases)

**Logging**:
- Error column (`outbox_messages.error`) provides audit trail
- Logs include the specific JSON exception message for debugging
- Correlate message IDs between logs and database

## Database Changes

No schema migrations required. The existing `error` column in `outbox_messages` is used to record malformed JSON descriptions:

```sql
-- View malformed messages
SELECT id, event_type, payload, error, processed_at_utc 
FROM outbox_messages 
WHERE error LIKE 'Malformed JSON%'
ORDER BY processed_at_utc DESC
LIMIT 100;
```

## Example Scenarios

### Scenario 1: Single Bad Message, Then Good Messages
```
Batch fetches 3 messages:
  ID: 123, Payload: { "valid": "json" }        → ✅ Broadcast OK
  ID: 124, Payload: { "broken                  → ❌ Logged error, marked processed
  ID: 125, Payload: { "valid": "json" }        → ✅ Broadcast OK

Result: All processed, worker continues polling
```

### Scenario 2: All Bad Messages
```
Batch fetches 2 messages:
  ID: 200, Payload: unclosed }                 → ❌ Logged, marked processed
  ID: 201, Payload: NaN }                      → ❌ Logged, marked processed

Result: Batch completes, worker continues polling
```

### Scenario 3: Connection Lost + Malformed
```
Batch fetches 2 messages:
  ID: 300, Payload: (malformed)                → ❌ Logged, marked processed
  ID: 301, Payload: { "valid": "json" }        → ⚠️ Connection lost → Batch aborted
  
Result: Message 300 stays processed, 301 retried next poll
```

## Future Enhancements

1. **Dead-Letter Queue**: After N malformed errors, move message to a DLQ table
2. **Metrics**: Expose Prometheus metrics for malformed message rates
3. **Alerting**: Automated alerts if malformed rate exceeds threshold
4. **Transformation Pipeline**: Allow custom transformations before JSON validation
5. **Replay**: Manual UI to retry/fix/delete bad messages

## References

- [OutboxPayloadExtractor](OutboxPayloadExtractor.cs) - Existing JSON extraction utility
- [OutboxMessage Model](BookingEngine.Api/Common/Persistence/OutboxMessage.cs)
- [BookingsHub](BookingEngine.Api/Hubs/BookingsHub.cs) - SignalR broadcasting
- [System.Text.Json Documentation](https://docs.microsoft.com/en-us/dotnet/api/system.text.json)
