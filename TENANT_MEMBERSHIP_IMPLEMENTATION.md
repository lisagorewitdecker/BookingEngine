# Tenant Membership Implementation

## Overview

This document describes the implementation of **fast, unambiguous tenant membership checks** using EF Core and PostgreSQL, following the patterns established in your BookingEngine project.

## What Was Implemented

### 1. TenantMembership Entity
**File:** [`src/BookingEngine.Api/Common/Domain/TenantMembership.cs`](src/BookingEngine.Api/Common/Domain/TenantMembership.cs)

Represents membership of a user in a tenant with:
- `Id` (Guid): Primary key
- `UserId` (Guid): The user's ID
- `TenantId` (Guid): The tenant's ID
- `Role` (string): User's role in tenant (defaults to "Member")
- `IsActive` (bool): Soft-deactivation flag (defaults to true)
- `CreatedAtUtc` (DateTime): Audit timestamp

```csharp
public sealed class TenantMembership
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string Role { get; set; } = "Member";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
```

### 2. EF Core Configuration in AppDbContext
**File:** [`src/BookingEngine.Api/Common/Persistence/AppDbContext.cs`](src/BookingEngine.Api/Common/Persistence/AppDbContext.cs)

The entity is configured with:
- **Table name:** `tenant_memberships` (snake_case per PostgreSQL conventions)
- **Column names:** All snake_case (`id`, `user_id`, `tenant_id`, `role`, `is_active`, `created_at_utc`)
- **Composite unique index:** `(user_id, tenant_id)` prevents duplicate memberships
- **Filtered index:** `(user_id, tenant_id, is_active)` WHERE `is_active = true` for fast lookups

**Key Configuration:**
```csharp
entity.HasIndex(m => new { m.UserId, m.TenantId })
    .IsUnique()
    .HasDatabaseName("ix_tenant_memberships_user_id_tenant_id");

entity.HasIndex(m => new { m.UserId, m.TenantId, m.IsActive })
    .HasFilter("\"is_active\" = true")
    .HasDatabaseName("ix_tenant_memberships_user_tenant_active");
```

### 3. Database Query in TenantMembershipService
**File:** [`src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs`](src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs)

Updated `IsUserMemberOfTenantAsync` to perform indexed database lookups:

```csharp
public async Task<bool> IsUserMemberOfTenantAsync(Guid tenantId, string userId, CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(userId))
        return false;

    // Parse the user ID string to Guid for clean parameterization by Npgsql
    if (!Guid.TryParse(userId, out var userGuid))
        return false;

    using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

    // Query the TenantMembership table for an active membership record
    return await dbContext.TenantMemberships
        .IgnoreQueryFilters()
        .AsNoTracking()
        .AnyAsync(
            m => m.UserId == userGuid && m.TenantId == tenantId && m.IsActive,
            cancellationToken);
}
```

**Key Details:**
- Parses `userId` to `Guid` before the query so Npgsql parameterizes Guid values cleanly
- Uses `.IgnoreQueryFilters()` to bypass any global tenant filters
- Uses `.AsNoTracking()` for performance since we only need a boolean result
- Uses `.AnyAsync()` to short-circuit as soon as a match is found

### 4. EF Core Migration
**File:** [`src/BookingEngine.Api/Migrations/20261122203749_AddTenantMemberships.cs`](src/BookingEngine.Api/Migrations/20261122203749_AddTenantMemberships.cs)

Generated migration creates:
- `tenant_memberships` table with proper PostgreSQL types (uuid, boolean, text)
- Composite unique index on `(user_id, tenant_id)`
- Filtered index on `(user_id, tenant_id, is_active)` for active-only lookups

### 5. BookingsHub Integration
**File:** [`src/BookingEngine.Api/Hubs/BookingsHub.cs`](src/BookingEngine.Api/Hubs/BookingsHub.cs)

The hub already supports optional database membership verification via the `verifyWithDatabase` flag:

```csharp
public async Task JoinTenantGroup(string tenantId, bool verifyWithDatabase = false)
{
    var normalized = NormalizeTenantId(tenantId);
    
    // Always check claims first (fast)
    if (!_membershipService.IsUserMemberOfTenantByClaimsOnly(Context.User, normalized))
        throw new HubException("User is not a member of the requested tenant.");

    // If requested, perform additional database verification
    if (verifyWithDatabase)
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? Context.User?.FindFirst("sub")?.Value
            ?? Context.User?.FindFirst("user_id")?.Value;

        if (string.IsNullOrEmpty(userId))
            throw new HubException("Unable to identify user for membership verification.");

        if (!Guid.TryParse(normalized, out var tenantGuid))
            throw new HubException("Invalid tenant ID format.");

        var isMember = await _membershipService.IsUserMemberOfTenantAsync(tenantGuid, userId);
        if (!isMember)
            throw new HubException("User is not a member of the requested tenant (verified against database).");
    }

    await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(Guid.Parse(normalized)));
}
```

Clients can now call:
```javascript
// Without database verification (claims-only, fast)
connection.invoke("JoinTenantGroup", tenantId);

// With database verification (dynamic membership checks)
connection.invoke("JoinTenantGroup", tenantId, true);
```

## How to Apply the Migration

### Prerequisites
- PostgreSQL running on `localhost:5432` (or update connection string in `appsettings.Development.json`)
- Database user `postgres` with password `postgres`
- Database `bookingdb` exists (created by PostgreSQL initialization)

### Apply Migration

```bash
cd /path/to/BookingEngine

# Using Development environment
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update \
  --project src/BookingEngine.Api \
  --startup-project src/BookingEngine.Api
```

**Expected output:**
```
Build succeeded.
Done. Applied all migrations.
```

### Via Docker Compose (Recommended)

If using Docker Compose with the included `docker-compose.yml`:

```bash
# Start PostgreSQL
docker-compose up -d postgres

# Wait for PostgreSQL to be ready (~10 seconds)
sleep 10

# Apply migrations
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update \
  --project src/BookingEngine.Api \
  --startup-project src/BookingEngine.Api
```

## Verification

### 1. Verify Table Structure

Connect to PostgreSQL and verify the schema:

```sql
-- Connect to bookingdb
psql -h localhost -U postgres -d bookingdb

-- Check table structure
\d tenant_memberships

-- Should show:
--   id              | uuid                        | not null
--   user_id         | uuid                        | not null
--   tenant_id       | uuid                        | not null
--   role            | character varying(64)      | not null
--   is_active       | boolean                     | not null
--   created_at_utc  | timestamp with time zone   | not null
```

### 2. Verify Indexes

```sql
-- List indexes on tenant_memberships
\d tenant_memberships_id_idx

-- Should see:
--   ix_tenant_memberships_user_id_tenant_id (UNIQUE)
--   ix_tenant_memberships_user_tenant_active (FILTERED)
```

### 3. Test Query Performance

```sql
-- Insert test data
INSERT INTO tenant_memberships (id, user_id, tenant_id, role, is_active, created_at_utc)
VALUES 
  (gen_random_uuid(), '11111111-1111-1111-1111-111111111111'::uuid, '22222222-2222-2222-2222-222222222222'::uuid, 'Admin', true, NOW()),
  (gen_random_uuid(), '33333333-3333-3333-3333-333333333333'::uuid, '44444444-4444-4444-4444-444444444444'::uuid, 'Member', true, NOW());

-- Test lookup (should use ix_tenant_memberships_user_tenant_active)
EXPLAIN ANALYZE
SELECT EXISTS(
  SELECT 1 FROM tenant_memberships 
  WHERE user_id = '11111111-1111-1111-1111-111111111111'::uuid 
    AND tenant_id = '22222222-2222-2222-2222-222222222222'::uuid 
    AND is_active = true
);

-- Should show "Index Scan" or "Index Only Scan" (not Seq Scan)
```

### 4. Test from Application

Create a simple unit or integration test:

```csharp
[Test]
public async Task IsUserMemberOfTenantAsync_WithValidMembership_ReturnsTrue()
{
    // Arrange
    var userId = new Guid("11111111-1111-1111-1111-111111111111");
    var tenantId = new Guid("22222222-2222-2222-2222-222222222222");
    
    var membership = new TenantMembership
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TenantId = tenantId,
        Role = "Admin",
        IsActive = true,
        CreatedAtUtc = DateTime.UtcNow
    };

    using var dbContext = await _dbContextFactory.CreateDbContextAsync();
    dbContext.TenantMemberships.Add(membership);
    await dbContext.SaveChangesAsync();

    var service = new TenantMembershipService(_dbContextFactory);

    // Act
    var result = await service.IsUserMemberOfTenantAsync(tenantId, userId.ToString());

    // Assert
    Assert.IsTrue(result);
}

[Test]
public async Task IsUserMemberOfTenantAsync_WithInactiveMembership_ReturnsFalse()
{
    // Arrange
    var userId = new Guid("11111111-1111-1111-1111-111111111111");
    var tenantId = new Guid("22222222-2222-2222-2222-222222222222");
    
    var membership = new TenantMembership
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TenantId = tenantId,
        Role = "Admin",
        IsActive = false,  // Inactive
        CreatedAtUtc = DateTime.UtcNow
    };

    using var dbContext = await _dbContextFactory.CreateDbContextAsync();
    dbContext.TenantMemberships.Add(membership);
    await dbContext.SaveChangesAsync();

    var service = new TenantMembershipService(_dbContextFactory);

    // Act
    var result = await service.IsUserMemberOfTenantAsync(tenantId, userId.ToString());

    // Assert
    Assert.IsFalse(result);
}
```

## Performance Characteristics

### Query Plan
The indexed `(user_id, tenant_id, is_active)` query executes as:
- **Cost:** O(log n) index lookup + equality check
- **Execution:** Index Scan on `ix_tenant_memberships_user_tenant_active` → Boolean result
- **Typical latency:** <1ms for tables up to millions of rows

### Index Strategy
- **Composite unique index** prevents duplicate rows and serves equality lookups
- **Filtered index** (is_active = true) reduces index size ~50% if deactivations are common
- Both indexes are automatically used by PostgreSQL query planner for the WHERE predicate

### Scaling
For O(10,000,000+) active memberships:
- Table scan would be O(n) → ~seconds
- Indexed lookup stays O(log n) → <1ms guaranteed
- Filtered index keeps working set smaller

## Configuration & Soft Deactivation

### Soft-Delete Pattern
Instead of removing records, set `IsActive = false`:

```csharp
// Deactivate membership
var membership = await dbContext.TenantMemberships
    .FirstAsync(m => m.UserId == userId && m.TenantId == tenantId);
membership.IsActive = false;
await dbContext.SaveChangesAsync();

// User is now rejected: IsUserMemberOfTenantAsync returns false
```

### Why This Design
1. **Audit trail:** Keep historical records for compliance
2. **Fast queries:** Filtered index speeds active-only lookups
3. **Reversible:** Can reactivate without re-creating relationships
4. **Multi-tenancy safe:** Works with tenant-scoped queries

## Next Steps

1. **Apply the migration** to your development and production databases
2. **Seed test data** using the entity:
   ```csharp
   new TenantMembership 
   { 
       Id = Guid.NewGuid(),
       UserId = userId, 
       TenantId = tenantId, 
       Role = "Admin", 
       IsActive = true,
       CreatedAtUtc = DateTime.UtcNow
   }
   ```
3. **Enable database verification** in BookingsHub by passing `verifyWithDatabase: true` to `JoinTenantGroup`
4. **Monitor index usage** in production via PostgreSQL logs/query plan analysis
5. **Add retention policy** (optional): Archive soft-deleted memberships after N days

## Related Files

- Entity: [TenantMembership.cs](src/BookingEngine.Api/Common/Domain/TenantMembership.cs)
- Configuration: [AppDbContext.cs](src/BookingEngine.Api/Common/Persistence/AppDbContext.cs) (see OnModelCreating)
- Service: [TenantMembershipService.cs](src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs)
- Migration: [20261122203749_AddTenantMemberships.cs](src/BookingEngine.Api/Migrations/20261122203749_AddTenantMemberships.cs)
- Consumer: [BookingsHub.cs](src/BookingEngine.Api/Hubs/BookingsHub.cs) (see JoinTenantGroup method)

## Troubleshooting

### Migration won't apply

**Problem:** `Connection refused` or `Database does not exist`

**Solution:**
```bash
# Start PostgreSQL in Docker
docker run -d \
  --name postgres \
  -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=bookingdb \
  -p 5432:5432 \
  postgres:16

# Wait and retry
sleep 10
dotnet ef database update --project src/BookingEngine.Api
```

### Index not being used

**Problem:** Query plan shows "Seq Scan" instead of "Index Scan"

**Solution:**
```sql
-- Analyze the table to update statistics
ANALYZE tenant_memberships;

-- Re-run EXPLAIN ANALYZE to verify index usage
EXPLAIN ANALYZE
SELECT EXISTS(
  SELECT 1 FROM tenant_memberships 
  WHERE user_id = $1 AND tenant_id = $2 AND is_active = true
);
```

### Duplicate key violation on insert

**Problem:** `Duplicate key value violates unique constraint`

**Solution:** The unique index ensures only one active membership per (user_id, tenant_id) pair. 
Before inserting, check for existing membership or use an upsert pattern:
```csharp
var existing = await dbContext.TenantMemberships
    .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId);

if (existing != null)
{
    existing.IsActive = true;
    existing.Role = "Admin";
}
else
{
    dbContext.TenantMemberships.Add(new TenantMembership 
    { 
        Id = Guid.NewGuid(),
        UserId = userId,
        TenantId = tenantId,
        Role = "Admin",
        IsActive = true,
        CreatedAtUtc = DateTime.UtcNow
    });
}
await dbContext.SaveChangesAsync();
```
