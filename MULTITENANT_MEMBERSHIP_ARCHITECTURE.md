# Multi-Tenant Membership Service - Architecture Diagram

## System Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                           Client Browser                             │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │ 1. Authenticate & Get JWT Token                               │ │
│  │    Token contains: tenant_id claims (single or multi-tenant)   │ │
│  │                                                                 │ │
│  │ 2. Connect to SignalR Hub                                      │ │
│  │    WebSocket: wss://api/hubs/bookings                          │ │
│  │    Query params: ?access_token=<jwt>&tenantId=<guid>          │ │
│  │                                                                 │ │
│  │ 3. Listen for BookingUpdated events                            │ │
│  │    connection.on("BookingUpdated", handler)                    │ │
│  └────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
                              ║
                              ║ WebSocket
                              ║
┌─────────────────────────────────────────────────────────────────────┐
│                        ASP.NET Core Server                           │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                      BookingsHub                               │ │
│  │  ┌──────────────────────────────────────────────────────────┐ │ │
│  │  │  OnConnectedAsync()                                      │ │ │
│  │  │  - Extract tenantId from query string                   │ │ │
│  │  │  - Get authorized tenants from JWT claims (FAST)        │ │ │
│  │  │  - Validate: tenantId ∈ authorizedTenants              │ │ │
│  │  │  - If invalid → Context.Abort()                         │ │ │
│  │  │  - If valid → Groups.AddToGroupAsync(tenant:guid)       │ │ │
│  │  └──────────────────────────────────────────────────────────┘ │ │
│  │                          ║                                      │ │
│  │                          ║ Uses                                 │ │
│  │                          ▼                                      │ │
│  │  ┌──────────────────────────────────────────────────────────┐ │ │
│  │  │  ITenantMembershipService                               │ │ │
│  │  │  ┌──────────────────────────────────────────────────────┤ │ │
│  │  │  │ GetAuthorizedTenantsFromClaims()                    │ │ │
│  │  │  │  - Parse JWT claims                                │ │ │
│  │  │  │  - Support: single, JSON array, delimited formats  │ │ │
│  │  │  │  - Return: HashSet<string> of tenant IDs           │ │ │
│  │  │  │  - Performance: O(1) - no database                 │ │ │
│  │  │  └──────────────────────────────────────────────────────┤ │ │
│  │  │  │ IsUserMemberOfTenantByClaimsOnly()                 │ │ │
│  │  │  │  - Quick check: tenantId ∈ claims                 │ │ │
│  │  │  │  - Performance: O(1) - no database                 │ │ │
│  │  │  └──────────────────────────────────────────────────────┤ │ │
│  │  │  │ IsUserMemberOfTenantAsync()                        │ │ │
│  │  │  │  - Query database for user-tenant relationship    │ │ │
│  │  │  │  - Performance: O(n) - database query              │ │ │
│  │  │  │  - Only called if verifyWithDatabase=true          │ │ │
│  │  │  └──────────────────────────────────────────────────────┤ │ │
│  │  └──────────────────────────────────────────────────────────┘ │ │
│  │                          ║                                      │ │
│  │                          ║ Uses                                 │ │
│  │                          ▼                                      │ │
│  │  ┌──────────────────────────────────────────────────────────┐ │ │
│  │  │  IDbContextFactory<AppDbContext>                        │ │ │
│  │  │  - Creates isolated database contexts                  │ │ │
│  │  │  - Avoids scoping conflicts                            │ │ │
│  │  │  - Used only by membership verification                │ │ │
│  │  └──────────────────────────────────────────────────────────┘ │ │
│  │                          ║                                      │ │
│  │  ┌────────────────────────▼──────────────────────────────────┐ │ │
│  │  │  JoinTenantGroup(tenantId, verifyWithDatabase=false)     │ │ │
│  │  │  - Validate claims first (FAST)                         │ │ │
│  │  │  - If verifyWithDatabase=true:                          │ │ │
│  │  │    - Extract userId from claims                        │ │ │
│  │    - Query database for active membership                  │ │ │
│  │  │  - Add to tenant group if validated                     │ │ │
│  │  │  - Throw HubException if unauthorized                  │ │ │
│  │  └──────────────────────────────────────────────────────────┘ │ │
│  │                          ║                                      │ │
│  │  ┌────────────────────────▼──────────────────────────────────┐ │ │
│  │  │  LeaveTenantGroup(tenantId)                             │ │ │
│  │  │  - Remove from tenant group                            │ │ │
│  │  │  - Silently ignore invalid tenant IDs                  │ │ │
│  │  └──────────────────────────────────────────────────────────┘ │ │
│  │                          ║                                      │ │
│  │  ┌────────────────────────▼──────────────────────────────────┐ │ │
│  │  │  PublishBookingUpdate(message)                          │ │ │
│  │  │  - Route to: Clients.Group(tenant:guid)               │ │ │
│  │  │  - Broadcast BookingUpdated event                      │ │ │
│  │  │  - Prevents cross-tenant leakage                       │ │ │
│  │  └──────────────────────────────────────────────────────────┘ │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
                              ║
                              ║ Query
                              ║
┌─────────────────────────────────────────────────────────────────────┐
│                       PostgreSQL Database                            │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │  tenants                  │  user_tenants  (future)           │ │
│  │  ├─ id                    │  ├─ id                             │ │
│  │  ├─ name                  │  ├─ user_id                        │ │
│  │  └─ slug                  │  ├─ tenant_id                      │ │
│  │                           │  ├─ joined_at_utc                 │ │
│  │  bookings                 │  └─ removed_at_utc                │ │
│  │  ├─ id                    │                                    │ │
│  │  ├─ tenant_id (filtered)  │  Query: SELECT * FROM user_tenants│ │
│  │  ├─ resource_id           │    WHERE tenant_id = ? AND user_id = ?│
│  │  ├─ customer_email        │    AND removed_at_utc IS NULL     │ │
│  │  └─ status                │                                    │ │
│  │                           │                                    │ │
│  │  outbox_messages          │  Returns: bool (member or not)    │ │
│  │  ├─ id                    │                                    │ │
│  │  ├─ tenant_id             │                                    │ │
│  │  ├─ event_type            │                                    │ │
│  │  ├─ payload               │                                    │ │
│  │  └─ processed_at_utc      │                                    │ │
│  └────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Connection Flow Sequence

```
Client                    Server (BookingsHub)              Service            Database
  │                              │                            │                  │
  ├──GET /hubs/bookings?         │                            │                  │
  │   access_token=...           │                            │                  │
  │   tenantId=550e8400...       │                            │                  │
  ├──────────────────────────────>                            │                  │
  │                              │                            │                  │
  │                    OnConnectedAsync()                      │                  │
  │                              │                            │                  │
  │                              ├──GET tenantId from query───>                   │
  │                              │                            │                  │
  │                              ├──GetAuthorizedTenantsFromClaims() [FAST]       │
  │                              ├────────────────────────────>                   │
  │                              │<─────────────────────────── HashSet{...}       │
  │                              │                            │                  │
  │                              ├──Validate tenantId ∈ claims                    │
  │                              │                            │                  │
  │   WebSocket Upgrade          │ PASS                       │                  │
  │   ✓ Connected ════════════════>                           │                  │
  │   (now in tenant:550e8400 group)                          │                  │
  │                              │                            │                  │
  │   invoke("JoinTenantGroup",   │                            │                  │
  │   "another-tenant", true)     │                            │                  │
  ├──────────────────────────────>  JoinTenantGroup()         │                  │
  │                              │                            │                  │
  │                              ├──IsUserMemberOfTenantByClaimsOnly() [FAST]     │
  │                              ├────────────────────────────>                   │
  │                              │<────────────────────────── true                │
  │                              │                            │                  │
  │                              ├──if (verifyWithDatabase)    │                  │
  │                              │   IsUserMemberOfTenantAsync() [SLOW]           │
  │                              ├────────────────────────────>                   │
  │                              │                            │ Query user_tenants
  │                              │                            ├─────────────────>
  │                              │                            │<─────────────────✓
  │                              │<───────────────────────────true               │
  │                              │                            │                  │
  │   invoke callback             │ ADD to tenant group        │                  │
  │   "Successfully joined" <──────────────────────────────────                   │
  │   (now in both groups)        │                           │                  │
  │                              │                            │                  │
  │   booking updated             │  PublishBookingUpdate()   │                  │
  │   (from outbox worker) <──────────────────────────────────                   │
  │                              │                            │                  │
```

---

## Key Decision Points

### When to Use Claims-Only Verification

✅ **Fast path** - no database overhead
- Initial connection setup
- API endpoint authorization
- Quick permission checks within a request

```csharp
bool isMember = _membershipService.IsUserMemberOfTenantByClaimsOnly(user, tenantId);
```

**Performance:** O(1)  
**Use case:** Default for all authorization checks

---

### When to Use Database Verification

✅ **Dynamic path** - authoritative state
- User switches workspaces
- Administrative tenant removal needs immediate effect
- Membership changes frequently

```csharp
bool isMember = await _membershipService.IsUserMemberOfTenantAsync(tenantId, userId, cancellationToken);
```

**Performance:** O(n) database query  
**Use case:** `JoinTenantGroup(..., verifyWithDatabase: true)`

---

## Security Boundaries

```
┌──────────────────────────────────────────┐
│         HTTP Request/WebSocket            │
│         (May be untrusted)                │
└────────────────┬─────────────────────────┘
                 │
                 ▼
┌──────────────────────────────────────────┐
│    Membership Service Validation         │
│  - Parse JWT claims (trusted)            │
│  - Query database if requested           │
└────────────────┬─────────────────────────┘
                 │
                 ▼
         ┌──────────────┐
         │ ✓ SAFE       │ ← Authorized users only
         │              │
         │ Tenant Groups│    BookingUpdated broadcasts
         │ (SignalR)    │    routed by tenant group
         └──────────────┘
                 │
                 ▼
         ┌──────────────┐
         │  Database    │    Tenant-scoped queries
         │  Queries     │    via AppDbContext.TenantId
         │ (EF Core     │    
         │  Filters)    │
         └──────────────┘
```

**Key Security Features:**
1. ✅ Initial connection validated against JWT claims
2. ✅ Group join validated against claims + optional database
3. ✅ Broadcasts routed through tenant groups (no cross-tenant leakage)
4. ✅ Database queries filtered by AppDbContext tenant filter
5. ✅ Invalid tenant IDs rejected with `Context.Abort()` or `HubException`

---

## Performance Characteristics

| Operation | Time Complexity | Database Query | Recommended Frequency |
|-----------|-----------------|---------------|-----------------------|
| GetAuthorizedTenantsFromClaims | O(1) | No | Every connection |
| IsUserMemberOfTenantByClaimsOnly | O(1) | No | Every method call |
| IsUserMemberOfTenantAsync | O(n) | Yes | Selective (workspace switch) |
| PublishBookingUpdate | O(1) | No | Every booking update |

**Rule:** Use claims-based checks for permission verification; use database checks for state verification.

---

## Related Documentation

- [Complete Implementation Guide](MULTITENANT_MEMBERSHIP_SERVICE.md)
- [Quick Reference for Developers](MULTITENANT_MEMBERSHIP_QUICK_REF.md)
- [Implementation Summary](MULTITENANT_MEMBERSHIP_IMPLEMENTATION.md)
- [Service Interface](src/BookingEngine.Api/Common/Tenancy/ITenantMembershipService.cs)
- [Service Implementation](src/BookingEngine.Api/Common/Tenancy/TenantMembershipService.cs)
- [Updated BookingsHub](src/BookingEngine.Api/Hubs/BookingsHub.cs)
