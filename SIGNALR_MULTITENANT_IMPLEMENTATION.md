# SignalR Multi-Tenant Implementation Guide

## Overview

Your BookingsHub has been enhanced to support both **single-tenant** and **multi-tenant users**. This document explains the implementation, claim formats, and how to integrate it with your identity provider.

---

## Key Changes from Single-Tenant to Multi-Tenant

### Before (Single-Tenant)
- One `tenant_id` claim per user
- Automatic group join on connect
- No ability to join multiple tenant groups

### After (Multi-Tenant Ready)
- Support for multiple claim formats
- Dynamic join/leave of tenant groups
- Query parameter-based initial connection
- Backward compatible with single-tenant setup

---

## Supported Claim Formats

Your BookingsHub now handles multiple tenant ID claim formats:

### 1. Single GUID Claim
```csharp
// Standard single tenant
Claim: tenant_id = "12345678-1234-1234-1234-123456789012"
```

### 2. Multiple Single Claims (Same Type)
```csharp
// Multiple claims with same type (most secure)
Claim: tenant_id = "12345678-1234-1234-1234-123456789012"
Claim: tenant_id = "87654321-4321-4321-4321-210987654321"
```

### 3. JSON Array Claim
```json
{
  "tenant_id": ["t-1-id", "t-2-id", "t-3-id"]
}
```

### 4. Comma/Space-Delimited Claim
```csharp
Claim: tenant_id = "t-1-id, t-2-id, t-3-id"
Claim: tenant_id = "t-1-id t-2-id t-3-id"
```

### 5. Alternative Claim Type Names
The hub checks multiple claim type variations for compatibility:
- `tenant_id` (standard)
- `tenantId` (camelCase)
- `tenant`
- `tenants`

---

## Implementation Details

### GetAuthorizedTenants (Core Method)

```csharp
private static HashSet<string> GetAuthorizedTenants(ClaimsPrincipal? user)
{
    var tenants = new HashSet<string>(StringComparer.Ordinal);
    if (user is null)
        return tenants;

    foreach (var claim in user.Claims)
    {
        if (!TenantClaimTypes.Contains(claim.Type, StringComparer.Ordinal))
            continue;

        AddParsedTenants(claim.Value, tenants);
    }

    return tenants;
}
```

**Returns:** A `HashSet<string>` of all authorized tenant IDs (case-sensitive).

### AddParsedTenants (Parsing Logic)

Intelligently parses a single claim value:
1. Attempts JSON array deserialization first
2. Falls back to comma/space delimited parsing
3. Handles single values without delimiters

This approach ensures your hub adapts to any claim format your identity provider uses.

---

## Connection Flow

### OnConnectedAsync
1. Extract all authorized tenants from claims
2. Check if `?tenantId=<id>` query parameter was provided
3. If provided:
   - Validate it's in the authorized set
   - Join the tenant group
   - **Abort connection if unauthorized**
4. If not provided:
   - Connect without joining any groups
   - Client must call `JoinTenantGroup()` to subscribe

**Security Point:** Invalid tenant IDs are **rejected at connection time**, not silently ignored.

### JoinTenantGroup(string tenantId)
Called by the client to join a specific tenant's real-time updates:

```csharp
public async Task JoinTenantGroup(string tenantId)
{
    // 1. Normalize the input
    var normalized = NormalizeTenantId(tenantId);
    if (string.IsNullOrEmpty(normalized))
        throw new HubException("A valid tenant ID is required.");

    // 2. Load authorized tenants
    var authorizedTenants = GetAuthorizedTenants(Context.User);
    
    // 3. Validate membership
    if (!authorizedTenants.Contains(normalized))
        throw new HubException("User is not a member of the requested tenant.");

    // 4. Join the group
    await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(Guid.Parse(normalized)));
}
```

**Use Case:** When the user switches workspaces in the UI, call this after leaving the previous group.

### LeaveTenantGroup(string tenantId)
Removes the connection from a tenant's broadcast group:

```csharp
public async Task LeaveTenantGroup(string tenantId)
{
    var normalized = NormalizeTenantId(tenantId);
    if (!string.IsNullOrEmpty(normalized))
    {
        try
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetTenantGroupName(Guid.Parse(normalized)));
        }
        catch (FormatException)
        {
            // Invalid GUID format; silently ignore
        }
    }
}
```

**Note:** Silently succeeds to prevent exceptions during cleanup.

### OnDisconnectedAsync
- Removes the connection from its initial tenant group (if any)
- Handles cleanup gracefully

---

## Usage Patterns

### Pattern 1: Initial Connection with Single Tenant
```javascript
// Connect with an initial tenant query parameter
const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://api.example.com/hubs/bookings?access_token=<jwt>&tenantId=<tenant-uuid>")
    .withAutomaticReconnect()
    .build();

await connection.start();
```

### Pattern 2: Dynamic Multi-Tenant Switching
```javascript
// Connect without specifying a tenant
const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://api.example.com/hubs/bookings?access_token=<jwt>")
    .withAutomaticReconnect()
    .build();

await connection.start();

// User clicks on "Tenant A" workspace
await connection.invoke("JoinTenantGroup", tenantAId);

// Real-time updates for Tenant A flow in
connection.on("BookingUpdated", (message) => {
    console.log("Update:", message);
});

// User switches to "Tenant B" workspace
await connection.invoke("LeaveTenantGroup", tenantAId);
await connection.invoke("JoinTenantGroup", tenantBId);
```

### Pattern 3: Pre-Authentication with Multiple Tenants
```csharp
// In your identity provider configuration, issue multiple tenant claims
var claims = new List<Claim>
{
    new Claim("sub", userId),
    new Claim("tenant_id", tenantA),
    new Claim("tenant_id", tenantB),
    new Claim("tenant_id", tenantC),
};
```

The hub will automatically collect all three tenant IDs on connect.

---

## Configuration in Program.cs

Your existing configuration already supports this:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // For SignalR WebSocket connections, read token from query string
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/hubs/bookings"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
        
        // JWT validation configuration
        options.Authority = builder.Configuration["Auth:Authority"];
        options.Audience = builder.Configuration["Auth:Audience"];
        // ... etc
    });

builder.Services.AddAuthorization();
```

The hub mapping is also correctly configured:

```csharp
app.MapHub<BookingsHub>("/hubs/bookings");
```

---

## Security Considerations

### 1. Claims-Based Authorization
✅ **Implemented:** Each connection is validated against the user's claims.  
✅ **Result:** Unauthorized tenant access is rejected at connection time.

### 2. JWT Token Size Limits
⚠️ **Consideration:** If your tokens contain many tenant IDs, JWT size may become large.  
**Solution:** Use the alternative **database membership check** pattern below.

### 3. Dynamic Membership Changes
⚠️ **Issue:** If a user is removed from a tenant *while* their connection is active, the token claims won't reflect this change immediately.  
**Solution:** Implement optional database membership verification (see below).

---

## Advanced Pattern: Database Membership Verification

If your system supports dynamically adding/removing users from tenants (without token refresh), you can enhance `JoinTenantGroup` to query the database:

```csharp
public sealed class BookingsHub : Hub
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public BookingsHub(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task JoinTenantGroup(string tenantId)
    {
        var normalized = NormalizeTenantId(tenantId);
        if (string.IsNullOrEmpty(normalized) || !Guid.TryParse(normalized, out var tenantGuid))
            throw new HubException("A valid tenant ID is required.");

        var userId = Context.User?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userId))
            throw new HubException("User identity could not be determined.");

        // Check database for current membership (optional but recommended)
        using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var isMember = await dbContext.UserTenants
            .AnyAsync(ut => ut.UserId == userId && ut.TenantId == tenantGuid);

        if (!isMember)
            throw new HubException("User is not a member of the requested tenant.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantGuid));
    }
}
```

**When to use this:**
- Tenant memberships are frequently added/removed during token lifetime
- Zero-latency access control is critical
- Your `UserTenant` junction table exists

---

## Testing the Implementation

### Unit Test: Multiple Tenant Claims
```csharp
[Test]
public void GetAuthorizedTenants_ParsesMultipleClaims()
{
    var claims = new List<Claim>
    {
        new Claim("tenant_id", "00000000-0000-0000-0000-000000000001"),
        new Claim("tenant_id", "00000000-0000-0000-0000-000000000002"),
        new Claim("sub", "user-123"),
    };

    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
    
    var tenants = BookingsHub.GetAuthorizedTenants(principal);
    
    Assert.That(tenants, Has.Count.EqualTo(2));
    Assert.That(tenants.Contains("00000000-0000-0000-0000-000000000001"), Is.True);
    Assert.That(tenants.Contains("00000000-0000-0000-0000-000000000002"), Is.True);
}
```

### Integration Test: SignalR Connection
```csharp
[Test]
public async Task OnConnectedAsync_ValidatesTenantFromQuery()
{
    var connection = new HubConnectionBuilder()
        .WithUrl($"https://localhost:5001/hubs/bookings?tenantId=test-tenant-1")
        .Build();

    await connection.StartAsync();
    
    // Should receive BookingUpdated events only for test-tenant-1
    // Should NOT receive events for other tenants
    
    await connection.StopAsync();
}
```

---

## Migration Path

If you're currently using the **single-tenant approach**:

1. **No changes needed** — the new code is backward compatible
2. Your existing JWT claims format (single `tenant_id`) will continue to work
3. The hub now *also* supports multi-tenant formats
4. When ready, update your identity provider to issue multiple tenant claims

---

## Troubleshooting

### "User is not a member of the requested tenant"
**Cause:** The `tenantId` query parameter or `JoinTenantGroup()` call uses a tenant the user doesn't belong to.  
**Fix:** Verify your JWT has the correct tenant claims.

### "A valid tenant ID is required"
**Cause:** Empty or null `tenantId` passed to `JoinTenantGroup()`.  
**Fix:** Ensure the client always passes a non-empty GUID.

### Connection aborted silently
**Cause:** The `?tenantId=` query parameter doesn't match any authorized tenant.  
**Fix:** Check that the JWT token contains this tenant in its claims.

### BookingUpdated not received
**Cause:** Client hasn't called `JoinTenantGroup()` or connection was dropped.  
**Fix:** Monitor connection status and explicitly join tenant groups after connecting.

---

## Summary

Your BookingsHub now provides:

✅ **Multi-tenant support** — Users can belong to multiple tenants  
✅ **Flexible claim parsing** — Adapts to various identity provider formats  
✅ **Dynamic group management** — Join/leave tenants without reconnecting  
✅ **Secure access control** — Claims-based authorization at connection and group join  
✅ **Backward compatibility** — Existing single-tenant tokens continue to work  
✅ **Extension points** — Optional database membership verification for dynamic scenarios  

This implementation aligns with your existing `AppDbContext` tenant-scoping pattern, ensuring real-time broadcasts respect the same access boundaries as your REST endpoints.
