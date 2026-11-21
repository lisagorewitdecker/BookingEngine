# Quick Start: Verifying the Outbox Worker Fix

## What Was Fixed
The BookingEngine worker now properly processes outbox messages by:
1. ✅ Registering a background tenant context in the dependency injection container
2. ✅ Using `IgnoreQueryFilters()` to bypass tenant isolation when querying outbox messages
3. ✅ Building successfully with no errors

## Files Modified
- **NEW:** `src/BookingEngine.Worker/BackgroundOutboxTenantContext.cs` - Background tenant context implementation
- **UPDATED:** `src/BookingEngine.Worker/Program.cs` - Added ITenantContext registration
- **UPDATED:** `src/BookingEngine.Worker/OutboxProcessorWorker.cs` - Use IgnoreQueryFilters() in ProcessBatchAsync()

## Verification Steps

### 1. Build Verification (Already Done ✅)
```bash
cd BookingEngine
dotnet build --no-restore
# Expected: Build succeeded with 0 errors
```

### 2. Runtime Verification
When you run the Aspire host:

```bash
dotnet run --project src/BookingEngine.AppHost
```

Then navigate to the **Structured Logs** at:
- https://localhost:17173/structuredlogs

Look for the `worker` service logs and verify:
- ❌ NO errors saying "Error while polling and broadcasting outbox messages"
- ✅ SUCCESS logs like "Broadcast and marked N outbox messages as processed"
- ✅ Connection messages like "Connected to SignalR hub at..."

### 3. Expected Log Pattern
```
[Information] Connected to SignalR hub at [hub_url]
[Information] Broadcast and marked 5 outbox messages as processed.
[Information] Broadcast and marked 3 outbox messages as processed.
...
```

## Key Implementation Details

### BackgroundOutboxTenantContext
Returns `null` for `TenantId` which:
- Allows `AppDbContext` to be constructed in the worker
- Makes tenant query filters evaluate as "allow all" (null == null)
- Explicitly bypassed with `IgnoreQueryFilters()` in the query

### Registration in Program.cs
```csharp
builder.Services.AddSingleton<ITenantContext, BackgroundOutboxTenantContext>();
```

**Critical:** Must be registered BEFORE `AppDbContext` is added.

### Query with IgnoreQueryFilters()
```csharp
var batch = await dbContext.OutboxMessages
    .IgnoreQueryFilters()  // Explicitly bypass tenant filters
    .Where(m => m.ProcessedAtUtc == null)
    .OrderBy(m => m.OccurredAtUtc)
    .Take(_options.BatchSize)
    .ToListAsync(stoppingToken);
```

## Troubleshooting

If the worker still has issues after these changes:

1. **Verify the solution builds:**
   ```bash
   dotnet clean
   dotnet restore
   dotnet build
   ```

2. **Check the registration order** in `Program.cs`:
   - `ITenantContext` registration must come BEFORE `AppDbContext` setup

3. **Review Aspire logs** for detailed error messages in the structured logs dashboard

4. **Verify database connection** in appsettings.json under "ConnectionStrings"

## Architecture Overview

```
BookingEngine.AppHost (runs local services)
  ├── BookingEngine.Api (HTTP service)
  │   └── Requires: ITenantContext (from HTTP middleware)
  │
  └── BookingEngine.Worker (Background service)
      ├── Requires: ITenantContext (now BackgroundOutboxTenantContext)
      └── Processes outbox messages across all tenants
```

Both services use the same `AppDbContext` but with different tenant contexts:
- **API:** Uses `TenantContext` from HTTP middleware (specific tenant)
- **Worker:** Uses `BackgroundOutboxTenantContext` (all tenants)
