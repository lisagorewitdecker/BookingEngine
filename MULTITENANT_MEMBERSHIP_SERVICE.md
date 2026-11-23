# Multi-Tenant SignalR Membership Service Implementation

## Overview

A new **dynamic membership verification service** has been added to your BookingEngine project to support large-scale, multi-tenant SignalR implementations where user memberships may change frequently or JWT claims cannot reliably represent all authorized tenants.

## Key Files

### New Service Interface
**[ITenantMembershipService.cs](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Common/Tenancy/ITenantMembershipService.cs)**
- Defines the contract for membership verification
- Two verification modes: claims-based (fast) and database-based (dynamic)

### Service Implementation
**[TenantMembershipService.cs](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs)**
- Production implementation using EF Core and DbContextFactory
- Reusable tenant ID parsing logic (JSON arrays, comma/space-delimited values)

### Updated Hub
**[BookingsHub.cs](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Hubs/BookingsHub.cs)**
- Injected ITenantMembershipService for membership checks
- Enhanced `JoinTenantGroup` with optional database verification
- Maintains backward compatibility with existing claim-based access control

### Updated Configuration
**[Program.cs](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Program.cs)**
- Registered ITenantMembershipService as scoped service
- Added DbContextFactory for creating temporary database connections

---

## Architecture

### Two-Tier Verification

The implementation uses a two-tier approach to balance performance and security:

#### 1. **Claims-Based Verification (Fast Path)**
Used in `OnConnectedAsync` and during quick checks:
```csharp
// Fast: no database query
bool isMember = _membershipService.IsUserMemberOfTenantByClaimsOnly(user, tenantId);
```

**When to use:**
- Initial connection validation
- Membership checks during request pipeline
- Scenarios with stable, infrequent membership changes

**Supported claim formats:**
- Single claim: `tenant_id = "550e8400-e29b-41d4-a716-446655440000"`
- JSON array: `tenant_id = ["t-1-id","t-2-id"]`
- Delimited: `tenant_id = "t-1-id, t-2-id"` or `"t-1-id t-2-id"`
- Multiple claims: multiple `tenant_id` claims (combined into one set)

#### 2. **Database-Based Verification (Dynamic Path)**
Explicitly requested via `verifyWithDatabase=true` parameter:
```csharp
// Slower: queries database for authoritative membership state
bool isMember = await _membershipService.IsUserMemberOfTenantAsync(tenantId, userId);
```

**When to use:**
- Users can join/leave tenants without re-authenticating
- Membership changes frequently during active sessions
- Large multi-tenant scenarios where claims cannot be exhaustive
- Administrative tenant removal needs immediate effect

---

## Usage Patterns

### Pattern 1: Initial Connection (Claims-Only)

```typescript
// Client-side TypeScript
const tenantId = "550e8400-e29b-41d4-a716-446655440000";
const connection = new signalR.HubConnectionBuilder()
    .withUrl(`https://api.example.com/hubs/bookings?access_token=${token}&tenantId=${tenantId}`)
    .withAutomaticReconnect()
    .build();

await connection.start();
```

The hub validates the tenant against JWT claims:
```csharp
// Server-side C# (automatic in OnConnectedAsync)
public override async Task OnConnectedAsync()
{
    var authorizedTenants = _membershipService.GetAuthorizedTenantsFromClaims(Context.User);
    var requestedTenant = NormalizeTenantId(Context.GetHttpContext()?.Request.Query["tenantId"].ToString());
    
    if (!string.IsNullOrEmpty(requestedTenant) && !authorizedTenants.Contains(requestedTenant))
    {
        Context.Abort(); // Reject unauthorized tenant
        return;
    }
    
    await base.OnConnectedAsync();
}
```

**Result:** Fast rejection if tenant not in claims; connection proceeds if valid.

---

### Pattern 2: Switching Tenants (Claims + Optional Database)

```typescript
// Client-side: User switches workspace in the UI
async function switchWorkspace(newTenantId: string) {
    // Leave old tenant group
    await connection.invoke("LeaveTenantGroup", currentTenantId);
    
    // Join new tenant group (with optional dynamic verification)
    try {
        await connection.invoke("JoinTenantGroup", newTenantId, {
            verifyWithDatabase: true  // Enable if membership can change
        });
        currentTenantId = newTenantId;
    } catch (err) {
        console.error("Failed to switch tenant:", err);
    }
}
```

Server-side handling:
```csharp
// C#
public async Task JoinTenantGroup(string tenantId, bool verifyWithDatabase = false)
{
    // Step 1: Check claims (fast)
    if (!_membershipService.IsUserMemberOfTenantByClaimsOnly(Context.User, tenantId))
        throw new HubException("Not authorized by claims.");
    
    // Step 2: Optionally verify against database
    if (verifyWithDatabase)
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var isMember = await _membershipService.IsUserMemberOfTenantAsync(Guid.Parse(tenantId), userId);
        if (!isMember)
            throw new HubException("Membership revoked in database.");
    }
    
    await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(Guid.Parse(tenantId)));
}
```

**Result:** Fast path with claims; slower database check only when membership changes are expected.

---

## Implementation Notes

### 1. Add User-Tenant Mapping Table

The placeholder `IsUserMemberOfTenantAsync` currently only checks if the tenant exists. For production, implement a proper user-tenant relationship:

```csharp
// Example: UserTenant entity
public class UserTenant
{
    public Guid Id { get; set; }
    public string UserId { get; set; }  // From JWT claims
    public Guid TenantId { get; set; }
    public DateTime JoinedAtUtc { get; set; }
    public DateTime? RemovedAtUtc { get; set; }  // Soft delete
    
    public Tenant Tenant { get; set; }
}

// Update TenantMembershipService.IsUserMemberOfTenantAsync
public async Task<bool> IsUserMemberOfTenantAsync(Guid tenantId, string userId, CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(userId))
        return false;

    using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
    
    return await dbContext.UserTenants
        .AnyAsync(ut => ut.TenantId == tenantId 
                     && ut.UserId == userId 
                     && ut.RemovedAtUtc == null, 
                 cancellationToken);
}
```

### 2. Extract User ID from Claims

The hub looks for user ID in this order:
1. `ClaimTypes.NameIdentifier` (standard .NET claim)
2. `"sub"` (OpenID Connect standard)
3. `"user_id"` (custom claim)

Ensure your identity provider includes at least one of these claims in the token.

### 3. DbContextFactory Isolation

The service uses `IDbContextFactory<AppDbContext>` to create isolated database contexts, avoiding scoping conflicts with the request-scoped `ITenantContext`. This is critical for:
- Background operations without HTTP context
- Parallel queries within the same request
- Avoiding tenant ID leakage between simultaneous requests

---

## Scaling Considerations

### For Small Multi-Tenant Systems (< 50 tenants per user)
- **Use claims-only verification** (default behavior)
- Include all authorized tenant IDs in JWT claims at token issuance
- No database query overhead
- Fast connection establishment

**Configuration:**
```csharp
// In BookingsHub.JoinTenantGroup - claims already validated in OnConnectedAsync
// Leave verifyWithDatabase = false (default)
```

### For Large Multi-Tenant Systems (> 50 tenants per user)
- **Use claims for initial screening**, database for ongoing validation
- Store only a "tenant list version" in JWT claims
- When version changes, trigger full database membership refresh
- Or enable `verifyWithDatabase=true` selectively for group joins

**Configuration:**
```typescript
// Client-side: Enable DB verification for active workspace switches
await connection.invoke("JoinTenantGroup", newTenantId, {
    verifyWithDatabase: true
});
```

### For Frequently-Changing Memberships
- **Enable periodic verification** in client
- Refresh `IsUserMemberOfTenantAsync` checks every 5-10 minutes
- Immediately revoke access if membership check fails
- Trigger `LeaveTenantGroup` and UI redirect on failure

```typescript
// Client-side: Periodic verification
setInterval(async () => {
    const isValid = await validateMembership(currentTenantId);
    if (!isValid) {
        await connection.invoke("LeaveTenantGroup", currentTenantId);
        // Redirect to home or tenant selection screen
    }
}, 5 * 60 * 1000); // Every 5 minutes
```

---

## Security Best Practices

1. **Always validate on the server**, never trust client-side authorization
2. **Use `Context.Abort()` on unauthorized connection attempts** (implemented in `OnConnectedAsync`)
3. **Throw `HubException` on unauthorized method calls** (implemented in `JoinTenantGroup`)
4. **Store sensitive membership in the database**, not in JWT claims alone
5. **Monitor `BookingUpdated` broadcasts** to ensure they only reach authorized users (enforced by tenant group routing)
6. **Implement audit logging** when users join/leave tenant groups for compliance

---

## Testing Checklist

- [ ] User with valid JWT connects to correct tenant → connection succeeds
- [ ] User with valid JWT connects to unauthorized tenant → connection aborted
- [ ] User calls `JoinTenantGroup` with unauthorized tenant → `HubException` thrown
- [ ] User calls `LeaveTenantGroup` with invalid tenant ID → silently succeeds
- [ ] User receives `BookingUpdated` only for their subscribed tenants
- [ ] Database membership check prevents access after tenant removal
- [ ] Multiple simultaneous connections from same user to different tenants work correctly
- [ ] SignalR broadcasts respect tenant boundaries (no cross-tenant leakage)

---

## Migration Path from Old Hub

If you're migrating from a claims-only hub to this hybrid approach:

1. **No breaking changes** — existing code continues to work
2. **Gradually enable database verification** via the new `verifyWithDatabase` parameter
3. **Monitor logs** for `HubException` from `JoinTenantGroup` failures
4. **Implement UserTenant table** only when you need dynamic membership
5. **Update client** to call `JoinTenantGroup` with `verifyWithDatabase=true` for sensitive scenarios

---

## Summary

The multi-tenant membership service provides:
- ✅ **Fast path**: Claims-based verification for initial connections
- ✅ **Dynamic path**: Database verification for ongoing membership validation
- ✅ **Flexible**: Opt-in database checks only when needed
- ✅ **Secure**: Tenant isolation enforced at SignalR hub and broadcast level
- ✅ **Scalable**: Supports both small and large multi-tenant systems
- ✅ **Maintainable**: Clean separation of concerns between claims and database verification
