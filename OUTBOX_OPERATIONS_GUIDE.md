# Outbox Dead-Letter Queue Operational Queries

## Quick Status Check

```sql
-- See a summary of outbox message processing status
SELECT
    CASE 
        WHEN processed_at_utc IS NOT NULL THEN 'Processed'
        WHEN dead_lettered_at_utc IS NOT NULL THEN 'Dead-Lettered'
        ELSE 'Pending'
    END as status,
    COUNT(*) as count,
    MIN(occurred_at_utc) as oldest_message,
    MAX(occurred_at_utc) as newest_message
FROM outbox_messages
GROUP BY 1
ORDER BY 2 DESC;
```

**Expected Output (Healthy System):**
```
     status      | count |      oldest_message      |     newest_message      
-----------------+-------+--------------------------+--------------------------
 Processed       |  2847 | 2026-11-20 10:30:00+00  | 2026-11-22 16:00:00+00
 Pending         |     3 | 2026-11-22 15:58:00+00  | 2026-11-22 16:02:00+00
 Dead-Lettered   |     0 | null                    | null
```

---

## Inspect Dead-Lettered Messages

```sql
-- Find all messages that could not be delivered after max attempts
SELECT
    id,
    event_type,
    tenant_id,
    attempt_count,
    last_error,
    last_attempt_at_utc,
    dead_lettered_at_utc,
    occurred_at_utc,
    EXTRACT(EPOCH FROM (dead_lettered_at_utc - occurred_at_utc)) as seconds_until_dead_lettered
FROM outbox_messages
WHERE dead_lettered_at_utc IS NOT NULL
ORDER BY dead_lettered_at_utc DESC
LIMIT 20;
```

**Key Columns to Examine:**
- **`last_error`** — Root cause (malformed JSON, timeout, etc.)
- **`attempt_count`** — Should be 5 (or your configured max)
- **`seconds_until_dead_lettered`** — Time from creation to failure

---

## Find Pending Messages Stuck in Retry Loop

```sql
-- Messages still being retried but have been pending for >1 hour
SELECT
    id,
    event_type,
    tenant_id,
    attempt_count,
    last_error,
    last_attempt_at_utc,
    occurred_at_utc,
    NOW() - last_attempt_at_utc as time_since_last_attempt,
    NOW() - occurred_at_utc as total_pending_time
FROM outbox_messages
WHERE processed_at_utc IS NULL
  AND dead_lettered_at_utc IS NULL
  AND NOW() - occurred_at_utc > INTERVAL '1 hour'
ORDER BY last_attempt_at_utc DESC;
```

**When to Investigate:**
- If `attempt_count` is still low (< 3) but message is very old → worker may be blocked
- If `last_attempt_at_utc` is stale (> 10 minutes ago) → worker may be stuck connecting to hub

---

## Diagnostic: Error Categories in Dead-Letter Queue

```sql
-- Group dead-lettered messages by error type to identify patterns
SELECT
    LEFT(last_error, 100) as error_prefix,
    COUNT(*) as count,
    MIN(dead_lettered_at_utc) as first_occurrence,
    MAX(dead_lettered_at_utc) as latest_occurrence,
    ARRAY_AGG(DISTINCT event_type) as affected_event_types
FROM outbox_messages
WHERE dead_lettered_at_utc IS NOT NULL
GROUP BY 1
ORDER BY 2 DESC;
```

**Common Patterns:**
- **"Unexpected token"** → Malformed JSON in payload
- **"HttpRequestException"** → Network/SSL issue (may retry after fix)
- **"not in the 'Connected' State"** → Hub connection lost (should eventually succeed)

---

## Recovery: Retry a Single Dead-Lettered Message

```sql
-- Use only after investigating and fixing the underlying issue!
UPDATE outbox_messages
SET 
    dead_lettered_at_utc = NULL,
    attempt_count = 0,
    last_error = NULL,
    last_attempt_at_utc = NULL
WHERE id = 'your-message-uuid-here'
  AND dead_lettered_at_utc IS NOT NULL;

-- Verify the update
SELECT id, event_type, attempt_count, dead_lettered_at_utc 
FROM outbox_messages 
WHERE id = 'your-message-uuid-here';
```

**After running this:**
- Next worker poll will pick up the message again
- Attempt counter resets to 0
- Message will be attempted up to 5 times before dead-lettering again

---

## Recovery: Bulk Retry Dead-Lettered Messages

```sql
-- Retry ALL dead-lettered messages (use with caution!)
-- This is useful after fixing a service issue that affected many messages

UPDATE outbox_messages
SET 
    dead_lettered_at_utc = NULL,
    attempt_count = 0,
    last_error = NULL,
    last_attempt_at_utc = NULL
WHERE dead_lettered_at_utc IS NOT NULL;

-- Check how many were reset
SELECT COUNT(*) as messages_reset FROM outbox_messages WHERE dead_lettered_at_utc IS NULL AND attempt_count = 0;
```

**When to Use:**
- After restoring a downstream service (SignalR hub offline, database restored, etc.)
- After fixing a critical bug in payload generation

**When NOT to Use:**
- For malformed JSON (fix the payload first, or delete the messages)
- Without understanding why they failed initially

---

## Monitoring: Dead-Letter Rate

```sql
-- Track how many messages were dead-lettered in the last 24 hours
SELECT
    DATE_TRUNC('hour', dead_lettered_at_utc) as hour,
    COUNT(*) as messages_dead_lettered,
    COUNT(DISTINCT event_type) as unique_event_types,
    ARRAY_AGG(DISTINCT LEFT(last_error, 50)) as error_samples
FROM outbox_messages
WHERE dead_lettered_at_utc >= NOW() - INTERVAL '24 hours'
GROUP BY 1
ORDER BY 1 DESC;
```

**Alert Thresholds (Recommended):**
- 🟡 **Warning:** >10 dead-lettered messages in 1 hour
- 🔴 **Critical:** >50 dead-lettered messages in 1 hour

---

## Maintenance: Archive Old Dead-Lettered Messages

```sql
-- Create a backup table for archival
CREATE TABLE outbox_messages_archive AS
SELECT * FROM outbox_messages
WHERE dead_lettered_at_utc IS NOT NULL
  AND dead_lettered_at_utc < NOW() - INTERVAL '30 days';

-- Then delete from main table (after backup!)
DELETE FROM outbox_messages
WHERE dead_lettered_at_utc IS NOT NULL
  AND dead_lettered_at_utc < NOW() - INTERVAL '30 days';

-- Verify archive
SELECT COUNT(*) FROM outbox_messages_archive;
SELECT COUNT(*) FROM outbox_messages WHERE dead_lettered_at_utc IS NOT NULL;
```

**Before archiving:**
1. Backup the entire database
2. Verify the archive table contains the expected rows
3. Test that a row in the archive can be restored if needed

---

## Performance: Index Usage

```sql
-- Check if the pending messages index is being used efficiently
EXPLAIN ANALYZE
SELECT id, tenant_id, event_type, payload, occurred_at_utc, 
       attempt_count, last_attempt_at_utc, last_error, dead_lettered_at_utc
FROM outbox_messages
WHERE processed_at_utc IS NULL
  AND dead_lettered_at_utc IS NULL
  AND attempt_count < 5
ORDER BY occurred_at_utc ASC
LIMIT 50;
```

**Expected Plan (Healthy):**
- Uses index `ix_outbox_messages_pending`
- Filter `processed_at_utc IS NULL AND dead_lettered_at_utc IS NULL` applied
- Low row count scanned (should be close to LIMIT 50)

**Warning Signs:**
- Seq Scan on outbox_messages (table scan instead of index)
- High "Rows" in plan (indicates filtering not happening at index level)
- If this happens, run `REINDEX TABLE outbox_messages;`

---

## Troubleshooting: Why Isn't My Message Processing?

```sql
-- For a specific message, trace its journey
WITH msg AS (
    SELECT 
        id, 
        event_type, 
        tenant_id,
        payload,
        occurred_at_utc,
        processed_at_utc,
        dead_lettered_at_utc,
        attempt_count,
        last_error,
        last_attempt_at_utc
    FROM outbox_messages 
    WHERE id = 'your-message-uuid'
)
SELECT 
    CASE 
        WHEN processed_at_utc IS NOT NULL THEN 'SUCCESS: Message was processed at ' || processed_at_utc
        WHEN dead_lettered_at_utc IS NOT NULL THEN 'DEAD-LETTERED: Failed at ' || dead_lettered_at_utc || ' after ' || attempt_count || ' attempts. Last error: ' || last_error
        WHEN last_attempt_at_utc IS NOT NULL THEN 'RETRYING: ' || attempt_count || ' attempts so far. Last attempt at ' || last_attempt_at_utc || '. Last error: ' || last_error
        ELSE 'PENDING: Never attempted (might be queued for next poll)'
    END as status,
    *
FROM msg;
```

**Example Output (Dead-Lettered):**
```
status: DEAD-LETTERED: Failed at 2026-11-22 15:45:30.123456+00 after 5 attempts. Last error: Unexpected end of JSON input
```

---

## Co-Authored-By
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
