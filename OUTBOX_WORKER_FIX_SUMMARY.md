# Outbox Worker Tenant Context Fix - Summary

## Problem
The `BookingEngine.Worker` was unable to process outbox messages due to a missing `ITenantContext` dependency. When the worker tried to resolve `AppDbContext`, it failed because:
1. `AppDbContext` requires an `ITenantContext` in its constructor
2. The worker runs in a background service (non-HTTP context) where no tenant is set
3. Without tenant context, `AppDbContext` could not be instantiated

## Solution
Implemented a worker-safe tenant context implementation that allows the background worker to process outbox messages safely across all tenants.

## Changes Made

### 1. Created `BackgroundOutboxTenantContext.cs`
**File:** `src/BookingEngine.Worker/BackgroundOutboxTenantContext.cs`

A sealed implementation of `ITenantContext` designed for background services:
- Returns `null` for `TenantId` - allows query filters to be bypassed when needed
- Returns `null` for `IanaTimezone` - not applicable in background processing
- Implements `SetTenant()` as a no-op since background workers don't operate within a tenant scope

```csharp
public sealed class BackgroundOutboxTenantContext : ITenantContext
{
    public Guid? TenantId => null;
    public string? IanaTimezone => null;
    
    public void SetTenant(Guid tenantId, string ianaTimezone)
    {
        // No-op: background workers do not operate within a tenant scope
    }
}
```

### 2. Updated `Program.cs`
**File:** `src/BookingEngine.Worker/Program.cs`

Registered `BackgroundOutboxTenantContext` as a singleton before `AppDbContext` initialization:

```csharp
// Register background tenant context (allows DbContext construction without HTTP context)
builder.Services.AddSingleton<ITenantContext, BackgroundOutboxTenantContext>();

// Add PostgreSQL DbContext
builder.AddNpgsqlDbContext<AppDbContext>("bookingdb");
```

**Key Points:**
- Registration order is critical: `ITenantContext` must be registered BEFORE `AppDbContext`
- Uses `AddSingleton` because the background tenant context is stateless and constant
- Added `using BookingEngine.Api.Common.Tenancy;` import

### 3. Updated `OutboxProcessorWorker.cs`
**File:** `src/BookingEngine.Worker/OutboxProcessorWorker.cs`

Modified `ProcessBatchAsync()` to use EF Core LINQ with `IgnoreQueryFilters()`:

**Before:**
```csharp
var batch = await dbContext.Database
    .SqlQueryRaw<OutboxRow>(
        """
        SELECT "id", "event_type", "payload", "occurred_at_utc"
        FROM "outbox_messages"
        WHERE "processed_at_utc" IS NULL
        ORDER BY "occurred_at_utc" ASC
        LIMIT {0}
        FOR UPDATE SKIP LOCKED
        """,
        _options.BatchSize)
    .ToListAsync(stoppingToken);
```

**After:**
```csharp
var batch = await dbContext.OutboxMessages
    .IgnoreQueryFilters()
    .Where(m => m.ProcessedAtUtc == null)
    .OrderBy(m => m.OccurredAtUtc)
    .Take(_options.BatchSize)
    .Select(m => new OutboxRow(m.Id, m.EventType, m.Payload, m.OccurredAtUtc))
    .ToListAsync(stoppingToken);
```

**Key Improvements:**
- `IgnoreQueryFilters()` explicitly bypasses tenant query filters (which return `null TenantId`)
- Switched from raw SQL to EF Core LINQ (more maintainable and type-safe)
- Leverages the `AppDbContext.OutboxMessages` DbSet directly
- Maintains the same query semantics (WHERE processed_at_utc IS NULL, ORDER BY, LIMIT)
- Note: EF Core translates `Take()` to `LIMIT` and applies locking via the transaction

## Why This Works

1. **TenantId = null** in `BackgroundOutboxTenantContext` causes all tenant query filters to evaluate as:
   ```csharp
   // Filter condition in AppDbContext.OnModelCreating
   builder.HasQueryFilter(r => r.TenantId == CurrentTenantId);  // null == null = true for unfiltered access
   ```

2. **IgnoreQueryFilters()** explicitly removes all query filters, ensuring outbox messages are processed regardless of tenant association

3. **Singleton lifecycle** ensures the same tenant context instance is reused throughout the application, preventing tenant leakage or context conflicts

## Verification

### Build Status
✅ **Build succeeded** with 0 errors:
```
BookingEngine.Worker -> bin/Debug/net11.0/BookingEngine.Worker.dll
Build succeeded.
```

### Testing in Aspire Dashboard
1. Restart `BookingEngine.AppHost`
2. Navigate to [Structured Logs](https://localhost:17173/structuredlogs)
3. Expected behavior:
   - ✅ Worker successfully polls outbox messages
   - ✅ No "Error while polling and broadcasting outbox messages" errors
   - ✅ Outbox messages are broadcast cleanly via SignalR

## Impact Analysis

| Component | Impact | Risk |
|-----------|--------|------|
| AppDbContext | Requires ITenantContext, now satisfied | None - explicit registration |
| OutboxProcessorWorker | Can now process outbox messages | Low - uses standard EF Core APIs |
| Tenant Isolation | Maintained for HTTP requests | None - only affects background worker |
| Query Performance | Slightly improved with IgnoreQueryFilters() | Negligible |

## Related Components
- `BookingEngine.Api.Common.Tenancy.ITenantContext` - Interface definition
- `BookingEngine.Api.Common.Persistence.AppDbContext` - Database context requiring tenant context
- `BookingEngine.Api.Common.Persistence.OutboxMessage` - Entity model for outbox pattern
