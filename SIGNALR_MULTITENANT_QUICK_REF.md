# SignalR Multi-Tenant Quick Reference

## Connection Flow Diagram

```
┌─────────────────────────────────────────────────────────┐
│ 1. Client Connects with JWT Token + Query Params        │
│    URL: /hubs/bookings?access_token=<jwt>&tenantId=<id> │
└─────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────┐
│ 2. JWT Validated by Authentication Middleware           │
│    - Token extracted from query string                  │
│    - Signature & expiration verified                    │
└─────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────┐
│ 3. OnConnectedAsync Executes                            │
│    - GetAuthorizedTenants() extracts all tenant claims  │
│    - Checks if ?tenantId= query param was provided      │
└─────────────────────────────────────────────────────────┘
                              │
                    ┌─────────┴─────────┐
                    │                   │
                    ▼                   ▼
         ┌────────────────────┐  ┌──────────────────┐
         │ tenantId provided  │  │ No tenantId      │
         └────────────────────┘  └──────────────────┘
                    │                   │
              ┌─────▼─────┐             │
              │ In claims? │             │
              └─────┬─────┘             │
                    │                   │
         ┌──────────┴──────────┐        │
         │                     │        │
         ▼                     ▼        ▼
      ✅ YES                ❌ NO   Connected
      Join group         Abort conn   (waiting)
                                      │
                                      │ Client calls
                                      │ JoinTenantGroup()
                                      │
                                      ▼
                                  Join group
                                      │
                                      ▼
                        📡 Receive BookingUpdated
```

---

## Client Code Examples

### TypeScript/JavaScript (SignalR Client)

#### Single Tenant Connection
```typescript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://api.example.com/hubs/bookings", {
        accessTokenFactory: () => getAccessToken(),
        // Pass tenant ID as query parameter for initial connection
        skipNegotiation: false,
    })
    .withAutomaticReconnect()
    .build();

// Append tenant ID to URL
const tenantId = "12345678-1234-1234-1234-123456789012";
connection.baseUrl = `${connection.baseUrl}?tenantId=${tenantId}`;

connection.on("BookingUpdated", (message) => {
    console.log("Booking updated:", message);
});

await connection.start();
```

#### Multi-Tenant Switching
```typescript
let currentTenantId: string | null = null;

async function switchTenant(newTenantId: string) {
    // Leave previous tenant if any
    if (currentTenantId) {
        await connection.invoke("LeaveTenantGroup", currentTenantId);
    }

    // Join new tenant
    await connection.invoke("JoinTenantGroup", newTenantId);
    currentTenantId = newTenantId;
}

// User clicks on "Workspace A"
await switchTenant("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

// User clicks on "Workspace B"
await switchTenant("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
```

---

## C# Hub Methods Reference

### GetAuthorizedTenants
**What it does:** Collects all tenant IDs from user's claims  
**Input:** `ClaimsPrincipal` (from `Context.User`)  
**Returns:** `HashSet<string>` of tenant IDs  
**Throws:** Nothing (returns empty set if none found)

```csharp
var authorizedTenants = BookingsHub.GetAuthorizedTenants(Context.User);
// Result: { "tenant-1", "tenant-2", "tenant-3" }
```

### JoinTenantGroup
**What it does:** Subscribe to a tenant's real-time updates  
**Called by:** Client (via SignalR hub method)  
**Input:** `tenantId` (string)  
**Throws:** `HubException` if tenant not authorized  

```csharp
await connection.invoke("JoinTenantGroup", "tenant-uuid");
```

### LeaveTenantGroup
**What it does:** Unsubscribe from a tenant's updates  
**Called by:** Client (via SignalR hub method)  
**Input:** `tenantId` (string)  
**Throws:** Nothing (silent on error)

```csharp
await connection.invoke("LeaveTenantGroup", "tenant-uuid");
```

---

## Claim Format Detection

| Format | Example | Parsing Method |
|--------|---------|-----------------|
| **Single GUID** | `"550e8400-e29b-41d4-a716-446655440000"` | Parsed as-is |
| **Multiple Claims** | Claim repeated 3× | Loop through claims |
| **JSON Array** | `["t1", "t2", "t3"]` | `JsonSerializer.Deserialize` |
| **Comma-Delimited** | `"t1, t2, t3"` | `.Split(',')` |
| **Space-Delimited** | `"t1 t2 t3"` | `.Split(' ')` |

**Hub Behavior:** Automatically detects format and parses accordingly.

---

## Security Checklist

- [ ] JWT token contains `tenant_id` claim(s)
- [ ] Authentication middleware validates token signature
- [ ] `OnConnectedAsync` aborts if `?tenantId=` is unauthorized
- [ ] `JoinTenantGroup()` throws if tenant not in authorized set
- [ ] `AppDbContext` has global query filter for `TenantId`
- [ ] Outbox worker broadcasts only to authorized tenant group
- [ ] No plaintext secrets in connection strings
- [ ] HTTPS enforced in production
- [ ] SignalR hub requires `[Authorize]` attribute
- [ ] Test cross-tenant access prevention

---

## Deployment Checklist

- [ ] Update `Program.cs` JWT configuration with your identity provider
- [ ] Deploy updated `BookingsHub.cs`
- [ ] Verify JWT token includes tenant claims in correct format
- [ ] Test connection with query parameter `?tenantId=<id>`
- [ ] Test `JoinTenantGroup()` and `LeaveTenantGroup()` methods
- [ ] Verify outbox worker broadcasts to correct tenant groups
- [ ] Load test multi-tenant connections (many users across tenants)
- [ ] Monitor SignalR hub performance metrics
- [ ] Enable detailed logging for troubleshooting

---

## Identity Provider Configuration

### Auth0
Add custom claim to rules:
```javascript
context.idToken['tenant_id'] = user.app_metadata.tenants || [];
context.accessToken['tenant_id'] = user.app_metadata.tenants || [];
```

### Azure AD
Configure in app manifest:
```json
{
  "optionalClaims": {
    "idToken": [
      {
        "name": "tenant_id",
        "source": "user"
      }
    ]
  }
}
```

### Keycloak
Add mapper to client protocol mapper:
```
Name: tenant_id
Mapper Type: User Attribute
User Attribute: tenant_ids
Token Claim Name: tenant_id
```

---

## Common Issues & Fixes

| Issue | Cause | Fix |
|-------|-------|-----|
| Connection aborts silently | Invalid `?tenantId=` query param | Verify JWT contains this tenant in claims |
| "User is not a member" error | `JoinTenantGroup()` with unauthorized tenant | Check JWT claims match requested tenant |
| BookingUpdated not received | Never called `JoinTenantGroup()` | Client must explicitly join after connect |
| Stale updates after switching tenants | Didn't call `LeaveTenantGroup()` | Always leave old tenant before joining new one |
| FormatException on tenantId | Non-GUID string passed | Ensure tenantId is a valid GUID string |

---

## Performance Tips

1. **Batch Multiple Joins:** If user has many tenants, consider limiting initial subscriptions
   ```csharp
   const int MaxInitialSubscriptions = 5;
   ```

2. **Monitor Group Size:** Track connections per tenant group
   ```csharp
   var groupInfo = await Clients.Group(GetTenantGroupName(tenantId))
       .SendCoreAsync("GroupStats", new[] { groupSize });
   ```

3. **Lazy Load Tenants:** Don't join all authorized tenants automatically; wait for user action

4. **Connection Pooling:** Use `IDbContextFactory<AppDbContext>` for database lookups in hub methods

---

## References

- [BookingsHub.cs](/src/BookingEngine.Api/Hubs/BookingsHub.cs)
- [Program.cs](/src/BookingEngine.Api/Program.cs)
- [SIGNALR_MULTITENANT_IMPLEMENTATION.md](/SIGNALR_MULTITENANT_IMPLEMENTATION.md)
- [ASP.NET Core SignalR Groups](https://docs.microsoft.com/en-us/aspnet/core/signalr/groups)
- [JWT Claims Best Practices](https://tools.ietf.org/html/rfc7519)
