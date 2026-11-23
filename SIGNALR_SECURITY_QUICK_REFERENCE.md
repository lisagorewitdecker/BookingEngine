# SignalR Multi-Tenant Security - Quick Reference

## What Changed?

| Aspect | Before | After |
|--------|--------|-------|
| **Authentication** | Optional | **Required** (`[Authorize]`) |
| **Tenant Source** | Query parameter `?tenantId=` | **Authenticated user claims** |
| **Group Join** | Automatic, no validation | **Validated against user's claim** |
| **Security** | Trusts client input | **Trusts identity provider tokens** |

---

## Key Methods in `BookingsHub`

### `GetAuthorizedTenantId(ClaimsPrincipal? user)`
Extracts tenant ID from authenticated user's claims. Returns `null` if no valid tenant claim found.

```csharp
// Called automatically in OnConnectedAsync and manually validated in JoinTenantGroup
var tenantId = GetAuthorizedTenantId(Context.User); // Returns Guid? or null
```

### `OnConnectedAsync()`
Automatically adds authenticated connection to their tenant's group.

```csharp
// No manual call needed - runs when client connects
// User is added to group "tenant:{tenantId}" based on their claims
```

### `JoinTenantGroup(Guid tenantId)`
Client method that validates the requested tenant matches user's claim.

```csharp
// Client calls this to join a group
// Throws HubException if tenant mismatch or no claim found
await connection.invoke("JoinTenantGroup", yourTenantId);
```

---

## Configuration Checklist

### In `Program.cs`:
- [ ] `builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)`
- [ ] `OnMessageReceived` reads `access_token` from query string for `/hubs/bookings`
- [ ] `app.UseAuthentication();` before hub mapping
- [ ] `app.UseAuthorization();` before hub mapping

### In Identity Provider:
- [ ] Issue JWT tokens with tenant ID claim (`tenant_id`, `tenantId`, or custom)
- [ ] Tokens are signed and not forged by clients
- [ ] Clients send tokens via query string: `?access_token=eyJhbGc...`

### In `BookingsHub`:
- [ ] `[Authorize]` attribute present
- [ ] `TenantClaimType` matches your provider's claim name
- [ ] `GetAuthorizedTenantId()` correctly parses claims

---

## Testing with Manual Token

For development testing without an identity provider:

1. Create a test JWT manually at [jwt.io](https://jwt.io):
   ```json
   {
     "sub": "test-user",
     "tenant_id": "12345678-1234-1234-1234-123456789012"
   }
   ```

2. Set expiration far in the future (dev only)

3. Connect with token:
   ```javascript
   const connection = new HubConnectionBuilder()
       .withUrl("wss://localhost:5001/hubs/bookings", {
           accessTokenFactory: () => "your.jwt.token"
       })
       .build();
   ```

---

## Common Errors & Fixes

| Error | Cause | Fix |
|-------|-------|-----|
| "Unauthorized" on connect | No token or invalid token | Pass valid JWT via `accessTokenFactory` |
| "Cannot join group for different tenant" | Tenant mismatch | Ensure token's `tenant_id` claim matches requested tenant |
| Hub method not invoked | `[Authorize]` missing token | Provide access token in connection options |
| Token validation fails | Dev placeholder misconfigured | Update identity provider config in `AddJwtBearer()` |

---

## Token Flow Diagram

```
Client (Browser)
    ↓
1. Gets token from identity provider
    ↓
2. Passes token: wss://api/hubs/bookings?access_token=...
    ↓
Program.cs: OnMessageReceived
    ↓
3. Extracts token from query string
    ↓
4. Validates signature (identity provider's key)
    ↓
5. Reads tenant_id claim
    ↓
BookingsHub: OnConnectedAsync
    ↓
6. Gets tenant from Context.User.Claims
    ↓
7. Adds connection to group "tenant:{tenantId}"
    ↓
Client is now scoped to tenant ✓
```

---

## Claim Name Reference

**Default checked (in order):**
1. `tenant_id` (most common)
2. `tenantId`
3. `tenant`

**If your provider uses a different name:**
```csharp
// Update in BookingsHub.cs
public const string TenantClaimType = "org_id"; // or your custom claim name
```

---

## Broadcasting from Outbox Worker

Ensure the outbox broadcast endpoint uses the same group naming:

```csharp
// In /internal/outbox/broadcast endpoint
var groupName = BookingsHub.GetTenantGroupName(message.TenantId);
await Clients.Group(groupName).SendAsync("BookingUpdated", message);
```

Group names must match exactly: `"tenant:{tenantId}"`

---

## Security Guarantees

✓ **Unauthenticated users cannot connect** (`[Authorize]`)  
✓ **Tenant ID cannot be forged** (read from signed JWT claims)  
✓ **Cross-tenant access is impossible** (validated on every group operation)  
✓ **Groups are tenant-isolated** (group names include tenant ID)  
✓ **Token cannot be replayed** (signed and time-bound)

---

## Production Checklist

- [ ] Remove development placeholder from `AddJwtBearer()`
- [ ] Configure real identity provider (Auth0, Azure AD, etc.)
- [ ] Enable HTTPS everywhere (`wss://` not `ws://`)
- [ ] Set token expiration to reasonable duration (not far future)
- [ ] Rotate identity provider keys periodically
- [ ] Monitor failed authentication attempts
- [ ] Test token refresh/renewal flow

---

## Resources

- **Full Guide:** [SIGNALR_MULTITENANT_SECURITY.md](./SIGNALR_MULTITENANT_SECURITY.md)
- **Code:** [BookingEngine.Api/Hubs/BookingsHub.cs](./src/BookingEngine.Api/Hubs/BookingsHub.cs)
- **Config:** [BookingEngine.Api/Program.cs](./src/BookingEngine.Api/Program.cs)
