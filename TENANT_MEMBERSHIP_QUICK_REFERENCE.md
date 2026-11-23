# Tenant Membership Implementation - Quick Reference

## 🎯 What Was Implemented

✅ **TenantMembership entity** - Domain model for user-tenant relationships  
✅ **EF Core configuration** - Snake_case schema with composite & filtered indexes  
✅ **Database query method** - Indexed lookup in `TenantMembershipService.IsUserMemberOfTenantAsync`  
✅ **Migration** - PostgreSQL schema with optimized indexes  
✅ **BookingsHub integration** - Optional database verification for SignalR connections  

## 📋 Files Modified/Created

| File | Change | Why |
|------|--------|-----|
| `src/BookingEngine.Api/Common/Domain/TenantMembership.cs` | **Created** | Entity for user-tenant membership |
| `src/BookingEngine.Api/Common/Persistence/AppDbContext.cs` | **Updated** | Added DbSet + EF mapping + indexes |
| `src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs` | **Updated** | Implements actual DB query instead of placeholder |
| `src/BookingEngine.Api/Migrations/20261122203749_AddTenantMemberships.cs` | **Created** | Creates `tenant_memberships` table |
| `TENANT_MEMBERSHIP_IMPLEMENTATION.md` | **Created** | Complete reference & guide |

## 🚀 To Apply the Migration

```bash
cd BookingEngine

# Ensure PostgreSQL is running (port 5432, postgres/postgres)
# Then:
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update \
  --project src/BookingEngine.Api \
  --startup-project src/BookingEngine.Api
```

**Expected output:**
```
Build succeeded.
Done. Applied all migrations.
```

## 🔍 Verify It Worked

```sql
-- Connect to bookingdb
psql -h localhost -U postgres -d bookingdb

-- Check table exists
SELECT table_name FROM information_schema.tables 
WHERE table_name = 'tenant_memberships';

-- Check indexes
SELECT indexname FROM pg_indexes 
WHERE tablename = 'tenant_memberships';

-- Should see:
--   ix_tenant_memberships_user_id_tenant_id
--   ix_tenant_memberships_user_tenant_active
```

## 💡 Key Design Details

### Entity Structure
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

### Indexes (PostgreSQL)
```sql
-- Unique composite index - prevents duplicates, enables fast equality lookups
CREATE UNIQUE INDEX ix_tenant_memberships_user_id_tenant_id 
  ON tenant_memberships (user_id, tenant_id);

-- Filtered index - speeds active-only lookups, reduces index size
CREATE INDEX ix_tenant_memberships_user_tenant_active 
  ON tenant_memberships (user_id, tenant_id, is_active) 
  WHERE is_active = true;
```

### Query Performance
```csharp
// This queries the filtered index - O(log n), <1ms typical
var isMember = await dbContext.TenantMemberships
    .IgnoreQueryFilters()
    .AsNoTracking()
    .AnyAsync(m => m.UserId == userId && m.TenantId == tenantId && m.IsActive);
```

## 📡 BookingsHub Usage

### Client Code (JavaScript)
```javascript
// Join tenant group with claims-only verification (fast)
connection.invoke("JoinTenantGroup", tenantId);

// Join tenant group with database verification (dynamic)
connection.invoke("JoinTenantGroup", tenantId, true);
```

### Server Code (C#)
```csharp
public async Task JoinTenantGroup(string tenantId, bool verifyWithDatabase = false)
{
    // ... validation ...
    
    if (verifyWithDatabase)
    {
        // Extract user ID from claims
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        // Query TenantMembership table (uses index)
        var isMember = await _membershipService.IsUserMemberOfTenantAsync(
            Guid.Parse(tenantId), userId);
        
        if (!isMember)
            throw new HubException("Not a member of this tenant");
    }
    
    await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(Guid.Parse(tenantId)));
}
```

## ✅ Verification Checklist

- [ ] Migration generated successfully: `20261122203749_AddTenantMemberships.cs`
- [ ] Project builds without errors
- [ ] PostgreSQL running on `localhost:5432`
- [ ] Migration applied with `dotnet ef database update`
- [ ] Table `tenant_memberships` exists in `bookingdb`
- [ ] Indexes `ix_tenant_memberships_user_id_tenant_id` and `ix_tenant_memberships_user_tenant_active` exist
- [ ] `TenantMembership` entity loads in IDE (no compilation errors)
- [ ] `AppDbContext` has `DbSet<TenantMembership> TenantMemberships`
- [ ] `TenantMembershipService.IsUserMemberOfTenantAsync` queries the table

## 🔧 Test Data

```sql
-- Insert a test membership
INSERT INTO tenant_memberships (id, user_id, tenant_id, role, is_active, created_at_utc)
VALUES 
  (gen_random_uuid(), 
   '11111111-1111-1111-1111-111111111111'::uuid, 
   '22222222-2222-2222-2222-222222222222'::uuid, 
   'Admin', 
   true, 
   NOW());

-- Query it (uses filtered index)
SELECT EXISTS(
  SELECT 1 FROM tenant_memberships 
  WHERE user_id = '11111111-1111-1111-1111-111111111111'::uuid 
    AND tenant_id = '22222222-2222-2222-2222-222222222222'::uuid 
    AND is_active = true
);
-- Should return: true
```

## 🛠️ Common Tasks

### Deactivate a user from a tenant (soft-delete)
```csharp
var membership = await dbContext.TenantMemberships
    .FirstAsync(m => m.UserId == userId && m.TenantId == tenantId);
membership.IsActive = false;
await dbContext.SaveChangesAsync();
// User is now rejected by IsUserMemberOfTenantAsync
```

### Add a new membership
```csharp
dbContext.TenantMemberships.Add(new TenantMembership
{
    Id = Guid.NewGuid(),
    UserId = userId,
    TenantId = tenantId,
    Role = "Member",
    IsActive = true,
    CreatedAtUtc = DateTime.UtcNow
});
await dbContext.SaveChangesAsync();
```

### List all tenants a user is a member of
```csharp
var userTenants = await dbContext.TenantMemberships
    .Where(m => m.UserId == userId && m.IsActive)
    .Select(m => m.TenantId)
    .ToListAsync();
```

## 🐛 Troubleshooting

**Q: Migration fails with "Connection refused"**  
A: PostgreSQL isn't running. Start it and ensure connection string is correct.

**Q: "Duplicate key value violates unique constraint"**  
A: Can't insert two memberships for the same (user_id, tenant_id) pair. Use update or soft-delete first.

**Q: Index not being used (Seq Scan in EXPLAIN)**  
A: Run `ANALYZE tenant_memberships;` to update statistics, then retry.

**Q: IsUserMemberOfTenantAsync always returns false**  
A: Check that `is_active = true` and that (user_id, tenant_id) exist in the table.

## 📚 Related Documentation

- Full guide: [`TENANT_MEMBERSHIP_IMPLEMENTATION.md`](TENANT_MEMBERSHIP_IMPLEMENTATION.md)
- Entity: [`src/BookingEngine.Api/Common/Domain/TenantMembership.cs`](src/BookingEngine.Api/Common/Domain/TenantMembership.cs)
- Service: [`src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs`](src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs)
- Hub: [`src/BookingEngine.Api/Hubs/BookingsHub.cs`](src/BookingEngine.Api/Hubs/BookingsHub.cs)

---

**Last Updated:** 2026-11-22  
**Migration:** `20261122203749_AddTenantMemberships`  
**Status:** ✅ Ready to apply
