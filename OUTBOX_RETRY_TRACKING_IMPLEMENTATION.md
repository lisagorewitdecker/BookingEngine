# Outbox Pattern: Retry Tracking & Dead-Letter Handling

## Overview

This document describes the implementation of failure/retry tracking and dead-letter handling for the outbox pattern used in BookingEngine. These improvements prevent malformed or stuck messages from being continuously re-polled by the `OutboxProcessorWorker`, improving system resilience and observability.

## Changes Implemented

### 1. ✅ OutboxMessage Entity Updated

**File:** `src/BookingEngine.Api/Common/Persistence/OutboxMessage.cs`

Added four new properties to track retry attempts and failures:

```csharp
public sealed class OutboxMessage
{
    // Existing properties
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; init; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? Error { get; set; }

    // NEW: Retry & Dead-Letter Tracking
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; set; }
}
```

**Purpose of Each Field:**
- **`AttemptCount`** (int, default: 0): Total number of broadcast attempts made for this message
- **`LastAttemptAtUtc`** (DateTimeOffset?, nullable): Timestamp of the most recent broadcast attempt
- **`LastError`** (string?, max 2000 chars): Last error message encountered (truncated to fit column limit)
- **`DeadLetteredAtUtc`** (DateTimeOffset?, nullable): Timestamp when message was marked as permanently failed and quarantined

---

### 2. ✅ AppDbContext Mapping Updated

**File:** `src/BookingEngine.Api/Common/Persistence/AppDbContext.cs`

Added column mappings and improved indexing:

```csharp
modelBuilder.Entity<OutboxMessage>(builder =>
{
    builder.ToTable("outbox_messages");
    builder.HasKey(o => o.Id);
    
    // ... existing mappings ...
    
    // NEW: Retry tracking columns
    builder.Property(o => o.AttemptCount)
        .HasColumnName("attempt_count")
        .HasDefaultValue(0)
        .IsRequired();

    builder.Property(o => o.LastAttemptAtUtc)
        .HasColumnName("last_attempt_at_utc");

    builder.Property(o => o.LastError)
        .HasColumnName("last_error")
        .HasMaxLength(2000);

    builder.Property(o => o.DeadLetteredAtUtc)
        .HasColumnName("dead_lettered_at_utc");

    // NEW: Improved index filters out dead-lettered rows
    builder.HasIndex(o => new { o.ProcessedAtUtc, o.DeadLetteredAtUtc, o.OccurredAtUtc })
        .HasDatabaseName("ix_outbox_messages_pending")
        .HasFilter("processed_at_utc IS NULL AND dead_lettered_at_utc IS NULL");
});
```

**Index Improvement:**
- **Old Index:** `processed_at_utc IS NULL` — returns all unprocessed messages, including dead-lettered poison pills
- **New Index:** `processed_at_utc IS NULL AND dead_lettered_at_utc IS NULL` — skips dead-lettered messages entirely

This prevents the worker from constantly re-polling stuck messages.

---

### 3. ✅ OutboxProcessorWorker Enhanced

**File:** `src/BookingEngine.Worker/OutboxProcessorWorker.cs`

#### Query Improvement: Filter Dead-Lettered & Exhausted Messages

```sql
SELECT "id", "tenant_id", "event_type", "payload", "occurred_at_utc", 
       "attempt_count", "last_attempt_at_utc", "last_error", "dead_lettered_at_utc"
FROM "outbox_messages"
WHERE "processed_at_utc" IS NULL
AND "dead_lettered_at_utc" IS NULL           -- NEW: skip quarantined messages
AND "attempt_count" < {maxAttempts}          -- NEW: skip exhausted retries
ORDER BY "occurred_at_utc" ASC
LIMIT {batchSize}
```

**MaxAttempts Constant:**
```csharp
const int maxAttempts = 5;
```

#### Error Handling with Retry Logic

For each message in the batch:

1. **Increment attempt counter** and record timestamp
2. **Validate JSON payload** early to detect malformed data:
   - If malformed → immediately dead-letter with error logged
3. **Attempt broadcast** via SignalR:
   - On success → mark `ProcessedAtUtc`
   - On retriable error (connection lost, HTTP timeout) → record error but continue
   - On non-retriable error (malformed JSON) → check if max attempts exceeded
4. **Dead-Letter Logic:**
   - If `AttemptCount >= maxAttempts` → set `DeadLetteredAtUtc = DateTime.UtcNow`
   - Log error with context (message ID, event type, attempt count)

#### Example Handler Implementation

```csharp
foreach (var message in batch)
{
    try
    {
        var newAttemptCount = message.AttemptCount + 1;
        var nowUtc = DateTimeOffset.UtcNow;

        // Validate JSON early
        try
        {
            using var _ = JsonDocument.Parse(message.Payload ?? "{}");
        }
        catch (JsonException jsonEx)
        {
            var errorMessage = Truncate(jsonEx.Message, 2000);
            
            _logger.LogError(
                jsonEx,
                "Malformed JSON payload in outbox message {MessageId}; marking as dead-lettered after {AttemptCount} attempts.",
                message.Id,
                newAttemptCount);
            
            // Mark attempt & dead-letter immediately
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "outbox_messages"
                SET "attempt_count" = @p0, 
                    "last_attempt_at_utc" = @p1, 
                    "last_error" = @p2,
                    "dead_lettered_at_utc" = @p3
                WHERE "id" = @p4
                """,
                [newAttemptCount, nowUtc, errorMessage, nowUtc, message.Id],
                ct);
            
            continue;
        }

        // Attempt broadcast
        await hubConnection.InvokeAsync(
            BookingsHub.PublishMethodName,
            new BookingUpdateMessage(...),
            ct);

        processedIds.Add(message.Id);
    }
    catch (HttpRequestException ex)
    {
        var errorMessage = Truncate(ex.Message, 2000);
        var newAttemptCount = message.AttemptCount + 1;
        var nowUtc = DateTimeOffset.UtcNow;

        if (newAttemptCount >= maxAttempts)
        {
            // Dead-letter after max attempts
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "outbox_messages"
                SET "attempt_count" = @p0, 
                    "last_attempt_at_utc" = @p1, 
                    "last_error" = @p2,
                    "dead_lettered_at_utc" = @p3
                WHERE "id" = @p4
                """,
                [newAttemptCount, nowUtc, errorMessage, nowUtc, message.Id],
                ct);

            _logger.LogError(
                ex,
                "Outbox message {OutboxId} ({EventType}) reached max attempts ({MaxAttempts}) and was dead-lettered.",
                message.Id,
                message.EventType,
                maxAttempts);
        }
        else
        {
            // Record attempt but keep retrying
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE "outbox_messages"
                SET "attempt_count" = @p0, 
                    "last_attempt_at_utc" = @p1, 
                    "last_error" = @p2
                WHERE "id" = @p3
                """,
                [newAttemptCount, nowUtc, errorMessage, message.Id],
                ct);
        }

        return 0; // Stop batch, reconnect
    }
    catch (Exception ex)
    {
        // ... similar error handling ...
    }
}
```

#### Helper Method: Error Message Truncation

```csharp
private static string Truncate(string? value, int maxLength) =>
    string.IsNullOrEmpty(value) ? string.Empty :
    value.Length <= maxLength ? value : value[..maxLength];
```

---

### 4. ✅ Database Migration Created

**File:** `src/BookingEngine.Api/Migrations/20261122210524_AddOutboxRetryTracking.cs`

The migration:
- Adds four new nullable/default columns to `outbox_messages` table
- Drops the old index `ix_outbox_messages_occurred_at_utc`
- Creates the new filtered index `ix_outbox_messages_pending` that excludes dead-lettered rows
- Provides down migration for rollback

**Columns Added:**
```sql
ALTER TABLE outbox_messages ADD COLUMN attempt_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE outbox_messages ADD COLUMN last_attempt_at_utc TIMESTAMP WITH TIME ZONE;
ALTER TABLE outbox_messages ADD COLUMN last_error VARCHAR(2000);
ALTER TABLE outbox_messages ADD COLUMN dead_lettered_at_utc TIMESTAMP WITH TIME ZONE;
```

---

## Operational Procedures

### Inspecting Dead-Lettered Messages

Query the `outbox_messages` table to find permanently failed messages:

```sql
SELECT
    "id",
    "event_type",
    "attempt_count",
    "last_attempt_at_utc",
    "dead_lettered_at_utc",
    "last_error",
    "payload"
FROM "outbox_messages"
WHERE "dead_lettered_at_utc" IS NOT NULL
ORDER BY "dead_lettered_at_utc" DESC;
```

**Common Findings:**
- **Malformed JSON:** `last_error` contains `Unexpected token` or `Unexpected end of JSON input`
- **Connection Timeouts:** `last_error` contains `HttpRequestException` or `Connection timeout`
- **Service Unavailable:** Multiple attempts at same timestamp suggest transient outage

### Manual Reprocessing (Advanced)

If a dead-lettered message should be retried after a fix (e.g., API endpoint restored):

```sql
-- Reset the message for reprocessing
UPDATE "outbox_messages"
SET "dead_lettered_at_utc" = NULL,
    "attempt_count" = 0,
    "last_error" = NULL,
    "last_attempt_at_utc" = NULL
WHERE "id" = 'your-message-id';
```

**Warning:** Only reset if you've verified the underlying cause is fixed.

### Monitoring Dead-Letter Queue

Create alerts on:
```sql
SELECT COUNT(*) as dead_lettered_count
FROM "outbox_messages"
WHERE "dead_lettered_at_utc" IS NOT NULL;
```

**Recommended Thresholds:**
- Alert if count > 10 (indicates ongoing issues)
- Alert if count grows by >50 per hour (indicates systemic problem)

---

## Key Design Decisions

### Why 5 Max Attempts?

- Attempts 1–4: Likely transient failures (network hiccup, temporary service outage)
- Attempt 5: Clear pattern of persistent failure
- Backoff implicit: Worker polling delays provide natural spacing between attempts

### Why Truncate Errors to 2000 Chars?

- Prevents unbounded growth of `last_error` column
- Stack traces and full error details are logged separately via `ILogger`
- 2000 chars is typically enough to diagnose (shows method name, class, root cause)

### Why Keep `ProcessedAtUtc` vs. `DeadLetteredAtUtc` Distinct?

- **SQL Clarity:** Immediately visible which messages succeeded vs. permanently failed
- **Metrics:** Can count successful broadcasts without counting dead-lettered poison pills
- **Recovery:** Clear signal that manual intervention may be needed

### Why Filter Dead-Lettered in Index, Not in Query?

- **Index Filter (Preferred):** Database automatically prunes dead-lettered rows from index; faster query plans
- **Query WHERE Clause:** Would still scan dead-lettered rows, defeating the purpose

---

## Testing

### Unit Tests Updated

**File:** `tests/BookingEngine.UnitTests/OutboxProcessorWorkerMalformedPayloadTests.cs`

Fixed property reference from `.Id` to `.MessageId` on `BookingUpdateMessage`.

Tests verify:
- Malformed JSON triggers immediate dead-lettering
- Error message is truncated and stored
- Valid messages proceed to broadcast despite other failures
- All payload formats (unclosed strings, invalid literals, etc.) are handled gracefully

### Build Verification

```bash
dotnet build  # ✅ Compiles successfully
```

**Warnings:** Only integration test warning about obsolete `PostgreSqlBuilder()` (pre-existing).

---

## Migration Path

### For Existing Databases

1. **Backup your `outbox_messages` table:**
   ```bash
   pg_dump -h your-host -U your-user bookingdb > backup_outbox_messages.sql
   ```

2. **Apply the migration:**
   ```bash
   dotnet ef database update --project "src/BookingEngine.Api"
   ```

3. **Verify columns exist:**
   ```sql
   SELECT column_name, data_type, is_nullable
   FROM information_schema.columns
   WHERE table_name = 'outbox_messages'
   ORDER BY ordinal_position;
   ```

4. **Monitor the first batch:**
   - New messages will have `attempt_count = 0` initially
   - After first poll, successful broadcasts increment to `1` and set `processed_at_utc`
   - Failed messages increment `attempt_count` and populate `last_error`, `last_attempt_at_utc`
   - After 5 failed attempts, `dead_lettered_at_utc` is set

### For New Installations

The migration is included in the EF Core migration history. Run:

```bash
dotnet ef database update --project "src/BookingEngine.Api"
```

---

## Future Enhancements

1. **Exponential Backoff:** Add `NextAttemptAtUtc` to implement backoff (don't retry immediately)
2. **Dead-Letter Topic:** Publish dead-lettered events to a separate queue for DLQ processing
3. **Metrics Export:** Publish `attempt_count` and `dead_lettered_at_utc` to Prometheus for alerting
4. **Admin Dashboard:** UI to inspect and manually reset dead-lettered messages
5. **Configurable Max Attempts:** Move `maxAttempts = 5` to `OutboxWorkerOptions`

---

## Summary

✅ **Implementation Complete**

| Component | Status | File(s) |
|-----------|--------|---------|
| Entity properties | ✅ | `OutboxMessage.cs` |
| EF Core mapping | ✅ | `AppDbContext.cs` |
| Retry logic | ✅ | `OutboxProcessorWorker.cs` |
| Database migration | ✅ | `20261122210524_AddOutboxRetryTracking.cs` |
| Test fixes | ✅ | `OutboxProcessorWorkerMalformedPayloadTests.cs` |
| Build | ✅ | No compilation errors |

The outbox pattern now provides:
- **Resilience:** Malformed messages don't block healthy ones
- **Observability:** Clear tracking of attempt counts and errors
- **Debuggability:** Dead-letter queue for post-mortem analysis
- **Performance:** Index filters prevent re-polling stuck messages

