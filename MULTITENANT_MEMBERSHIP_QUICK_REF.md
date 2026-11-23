# Multi-Tenant Membership Service - Quick Reference

## Injecting the Service

```csharp
// In your class (SignalR hub, controller, etc.)
private readonly ITenantMembershipService _membershipService;

public MyClass(ITenantMembershipService membershipService)
{
    _membershipService = membershipService ?? throw new ArgumentNullException(nameof(membershipService));
}
```

---

## Common Operations

### 1. Get All Authorized Tenants from Claims (Fast)
```csharp
var authorizedTenants = _membershipService.GetAuthorizedTenantsFromClaims(Context.User);
// Returns: HashSet<string> e.g., { "550e8400-e29b-41d4-a716-446655440000", "..." }
```

**Use when:** 
- Building a list of available workspaces for the user
- Initial connection validation
- Quick authorization checks

**Performance:** O(1) - no database query

---

### 2. Check Membership by Claims (Fast)
```csharp
string tenantId = "550e8400-e29b-41d4-a716-446655440000";
bool isMember = _membershipService.IsUserMemberOfTenantByClaimsOnly(Context.User, tenantId);

if (!isMember)
    throw new HubException("User is not a member of this tenant.");
```

**Use when:**
- Quick permission checks without database overhead
- Initial connection validation
- API endpoint authorization

**Performance:** O(1) - no database query

---

### 3. Verify Membership in Database (Dynamic)
```csharp
var tenantId = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

bool isMember = await _membershipService.IsUserMemberOfTenantAsync(tenantId, userId);

if (!isMember)
    throw new HubException("User membership was revoked.");
```

**Use when:**
- User switches workspaces (might have been removed)
- Sensitive operations requiring fresh membership state
- Administrative tenant removal needs immediate effect
- Membership changes frequently

**Performance:** O(n) database query - avoid calling repeatedly

---

## SignalR Hub Integration

### Initial Connection Setup
```csharp
[Authorize]
public sealed class BookingsHub : Hub
{
    private readonly ITenantMembershipService _membershipService;

    public BookingsHub(ITenantMembershipService membershipService)
    {
        _membershipService = membershipService;
    }

    public override async Task OnConnectedAsync()
    {
        // Automatically validated by membership service
        await base.OnConnectedAsync();
    }
}
```

### Switching Workspaces with Optional Database Verification
```csharp
public async Task JoinTenantGroup(string tenantId, bool verifyWithDatabase = false)
{
    // Step 1: Validate claims (always, fast)
    if (!_membershipService.IsUserMemberOfTenantByClaimsOnly(Context.User, tenantId))
        throw new HubException("Not authorized.");

    // Step 2: Optional database check
    if (verifyWithDatabase)
    {
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var isMember = await _membershipService.IsUserMemberOfTenantAsync(Guid.Parse(tenantId), userId);
        if (!isMember)
            throw new HubException("Membership not found in database.");
    }

    await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(Guid.Parse(tenantId)));
}
```

---

## Client-Side Usage

### Connecting to Initial Tenant
```typescript
const connection = new signalR.HubConnectionBuilder()
    .withUrl(`https://api.example.com/hubs/bookings?access_token=${token}&tenantId=${tenantId}`)
    .withAutomaticReconnect()
    .build();

await connection.start();
```

### Switching Tenants
```typescript
// Leave old tenant
await connection.invoke("LeaveTenantGroup", oldTenantId);

// Join new tenant (with dynamic verification if membership might change)
await connection.invoke("JoinTenantGroup", newTenantId, true);
```

### Listening for Booking Updates
```typescript
connection.on("BookingUpdated", (message: BookingUpdateMessage) => {
    console.log("Booking updated:", message);
    updateUI(message);
});
```

---

## Error Handling

### Server-Side
```csharp
public async Task JoinTenantGroup(string tenantId)
{
    try
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new HubException("Tenant ID is required.");

        if (!_membershipService.IsUserMemberOfTenantByClaimsOnly(Context.User, tenantId))
            throw new HubException("User is not a member of this tenant.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(Guid.Parse(tenantId)));
    }
    catch (FormatException)
    {
        throw new HubException("Invalid tenant ID format.");
    }
}
```

### Client-Side
```typescript
try {
    await connection.invoke("JoinTenantGroup", newTenantId, true);
} catch (error) {
    if (error.message.includes("not a member")) {
        // User no longer has access
        redirectToTenantSelection();
    } else if (error.message.includes("Invalid tenant ID")) {
        // Client error - bad tenant ID
        showError("Invalid tenant selection");
    } else {
        // Connection error - retry
        showError("Connection failed, retrying...");
    }
}
```

---

## Claim Formats

The service supports multiple claim formats automatically:

### Single Tenant (Standard)
```json
{
  "tenant_id": "550e8400-e29b-41d4-a716-446655440000"
}
```

### JSON Array (Multi-Tenant)
```json
{
  "tenant_id": "[\"t-1-id\", \"t-2-id\", \"t-3-id\"]"
}
```

### Comma-Delimited
```json
{
  "tenant_id": "t-1-id, t-2-id, t-3-id"
}
```

### Space-Delimited
```json
{
  "tenant_id": "t-1-id t-2-id t-3-id"
}
```

### Multiple Claims
```json
{
  "tenant_id": "t-1-id",
  "tenant_id": "t-2-id",
  "tenant_id": "t-3-id"
}
```

---

## Performance Tips

| Operation | Performance | When to Use |
|-----------|-----------|-----------|
| `GetAuthorizedTenantsFromClaims` | O(1) | List available workspaces |
| `IsUserMemberOfTenantByClaimsOnly` | O(1) | Quick validation |
| `IsUserMemberOfTenantAsync` | O(n) | Membership might have changed |

**Rule of thumb:**
- Use claims-based checks for **permission checks** (they don't change during the request)
- Use database checks for **state verification** (they might have changed)

---

## Common Mistakes

❌ **Don't:** Call `IsUserMemberOfTenantAsync` on every SignalR method call
```csharp
public async Task UpdateBooking(UpdateBookingRequest request)
{
    // ❌ WRONG: Database query on every method call = slow
    var isMember = await _membershipService.IsUserMemberOfTenantAsync(request.TenantId, userId);
}
```

✅ **Do:** Validate once at join time, trust the group membership thereafter
```csharp
// Once at join time
await JoinTenantGroup(tenantId, verifyWithDatabase: true);

// Then client receives messages routed by tenant group
// No need to re-verify on every message
```

---

## Troubleshooting

| Problem | Cause | Solution |
|---------|-------|----------|
| Connection aborted on connect | Tenant not in JWT claims | Add tenant to JWT claims or authenticate again |
| `JoinTenantGroup` fails | Claims check failed | Verify user has `tenant_id` claim with correct value |
| Database check fails | User removed from tenant | Trigger full token refresh or accept `HubException` |
| Multiple tenant IDs in claim not parsed | Wrong claim format | Ensure it's JSON array `["a","b"]` or comma/space-delimited |
| Cannot get user ID for database check | Missing claims | Ensure JWT includes `sub`, `NameIdentifier`, or `user_id` claim |

---

## Related Files

- **Service Interface:** [ITenantMembershipService.cs](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Common/Tenancy/ITenantMembershipService.cs)
- **Implementation:** [TenantMembershipService.cs](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs)
- **Updated Hub:** [BookingsHub.cs](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Hubs/BookingsHub.cs)
- **DI Configuration:** [Program.cs](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/src/BookingEngine.Api/Program.cs) (lines 97-98)
- **Full Documentation:** [MULTITENANT_MEMBERSHIP_SERVICE.md](/Users/lmgorewit/Documents/Documents\ -\ Lisa\ M\ Gorewit-Decker\ MacBook\ Pro/lisagorewitdecker/BookingEngine/MULTITENANT_MEMBERSHIP_SERVICE.md)
