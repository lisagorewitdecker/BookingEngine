# Multi-Tenant Membership Service - Setup & Next Steps Checklist

## ✅ What's Already Done

- [x] Created `ITenantMembershipService` interface
- [x] Implemented `TenantMembershipService` with:
  - [x] Claim parsing (single, JSON array, comma/space-delimited formats)
  - [x] Claims-based authorization check
  - [x] Database verification method (placeholder, ready for implementation)
- [x] Registered service in dependency injection (`Program.cs`)
- [x] Added `DbContextFactory` for isolated database contexts
- [x] Updated `BookingsHub` with:
  - [x] Dependency injection of membership service
  - [x] Claims-based verification in `OnConnectedAsync`
  - [x] Enhanced `JoinTenantGroup` with optional database verification
  - [x] Service usage in `OnDisconnectedAsync`
- [x] Removed duplicate claim parsing logic from hub
- [x] Verified build (0 errors)
- [x] Verified tests (9/9 passing)
- [x] Created comprehensive documentation
  - [x] Architecture guide
  - [x] Implementation guide
  - [x] Quick reference guide
  - [x] Architecture diagram

---

## 🔄 Immediate Next Steps (Before Production)

### 1. Add UserTenant Entity and Database Schema
**Effort:** 1-2 hours | **Priority:** HIGH

```csharp
// File: src/BookingEngine.Api/Common/Domain/UserTenant.cs
public class UserTenant
{
    public Guid Id { get; set; }
    
    public string UserId { get; set; }  // From JWT claims (NameIdentifier, sub, or user_id)
    
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; }
    
    public DateTime JoinedAtUtc { get; set; }
    public DateTime? RemovedAtUtc { get; set; }  // Soft delete for audit trail
}
```

**Add to AppDbContext:**
```csharp
public DbSet<UserTenant> UserTenants => Set<UserTenant>();

// In OnModelCreating:
modelBuilder.Entity<UserTenant>(builder =>
{
    builder.HasKey(ut => ut.Id);
    builder.HasIndex(ut => new { ut.TenantId, ut.UserId }).IsUnique();
    builder.HasIndex(ut => ut.UserId);
    
    builder.HasOne(ut => ut.Tenant)
        .WithMany()
        .HasForeignKey(ut => ut.TenantId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

**Create migration:**
```bash
cd src/BookingEngine.Api
dotnet ef migrations add AddUserTenant
dotnet ef database update
```

### 2. Implement IsUserMemberOfTenantAsync
**Effort:** 30 minutes | **Priority:** HIGH

Replace placeholder in `TenantMembershipService.cs`:

```csharp
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

### 3. Update JWT Token Generation
**Effort:** 1-2 hours | **Priority:** HIGH

Ensure your identity provider includes tenant IDs in the JWT token:

**For Auth0:**
```json
{
  "tenant_id": ["550e8400-e29b-41d4-a716-446655440000", "..."],
  "sub": "auth0|user123",
  ...
}
```

**For Azure AD:**
Add custom claims extension or use app roles

**For custom identity provider:**
Include in token claims when issuing JWT

### 4. Test Claims Extraction
**Effort:** 30 minutes | **Priority:** MEDIUM

Add unit tests for claim parsing:

```csharp
[TestClass]
public class TenantMembershipServiceTests
{
    [TestMethod]
    public void GetAuthorizedTenantsFromClaims_WithMultipleTenants_ParsesCorrectly()
    {
        var claims = new[]
        {
            new Claim("tenant_id", "[\"t1\", \"t2\", \"t3\"]")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
        
        var service = new TenantMembershipService(...);
        var result = service.GetAuthorizedTenantsFromClaims(principal);
        
        Assert.AreEqual(3, result.Count);
        Assert.IsTrue(result.Contains("t1"));
    }
}
```

---

## 🚀 Production Setup (Before Deploying)

### 5. Enable Database Verification for Sensitive Operations
**Effort:** 1-2 hours | **Priority:** MEDIUM

Update client code to use database verification for workspace switches:

```typescript
// Client: Enable for workspace switching
async function switchWorkspace(newTenantId: string) {
    await connection.invoke("LeaveTenantGroup", currentTenantId);
    
    // Note: Set second parameter to true for database verification
    await connection.invoke("JoinTenantGroup", newTenantId, true);
}
```

### 6. Add Audit Logging
**Effort:** 2-3 hours | **Priority:** MEDIUM

Log all membership changes and authorization events:

```csharp
// In BookingsHub
public async Task JoinTenantGroup(string tenantId, bool verifyWithDatabase = false)
{
    var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    var connectionId = Context.ConnectionId;
    
    try
    {
        // ... validation code ...
        
        await Groups.AddToGroupAsync(connectionId, GetTenantGroupName(Guid.Parse(tenantId)));
        
        // Log successful join
        _logger.LogInformation(
            "User {UserId} joined tenant group {TenantId} from connection {ConnectionId}",
            userId, tenantId, connectionId);
    }
    catch (HubException ex)
    {
        // Log failed authorization attempt
        _logger.LogWarning(
            "User {UserId} failed to join tenant {TenantId}: {Reason}",
            userId, tenantId, ex.Message);
        throw;
    }
}
```

### 7. Add Configuration Options
**Effort:** 1 hour | **Priority:** LOW

Add to `appsettings.json`:

```json
{
  "SignalR": {
    "EnableDatabaseMembershipVerification": true,
    "MembershipVerificationCacheDurationMinutes": 5,
    "MaxTenantsPerUser": 100,
    "EnableAuditLogging": true
  }
}
```

### 8. Add Health Checks
**Effort:** 2 hours | **Priority:** MEDIUM

Monitor membership service health:

```csharp
// In Program.cs
builder.Services.AddHealthChecks()
    .AddCheck("TenantMembership", async (ct) =>
    {
        var service = scope.ServiceProvider.GetRequiredService<ITenantMembershipService>();
        var testTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        
        try
        {
            await service.IsUserMemberOfTenantAsync(testTenantId, "test-user", ct);
            return HealthCheckResult.Healthy();
        }
        catch
        {
            return HealthCheckResult.Unhealthy("Database query failed");
        }
    });
```

---

## 📊 Monitoring & Scaling (After Production)

### 9. Monitor Performance Metrics
**Effort:** 2-3 hours | **Priority:** MEDIUM

Track in your APM (Application Insights, Datadog, etc.):

```csharp
// In TenantMembershipService
public async Task<bool> IsUserMemberOfTenantAsync(Guid tenantId, string userId, CancellationToken cancellationToken = default)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var result = await dbContext.UserTenants.AnyAsync(...);
        
        return result;
    }
    finally
    {
        _telemetry.TrackDependency(
            "PostgreSQL",
            "UserTenant.Any",
            stopwatch.Elapsed,
            success: true);
    }
}
```

**Key Metrics to Monitor:**
- Database query latency for `IsUserMemberOfTenantAsync`
- Failed authorization attempts per hour
- Number of active SignalR connections per tenant
- Tenant group membership changes per minute
- P95/P99 latency for group operations

### 10. Implement Caching (Optional for Large Systems)
**Effort:** 2-3 hours | **Priority:** LOW (for >1000 users)

```csharp
// Cache membership check results for 5 minutes
public class CachingTenantMembershipService : ITenantMembershipService
{
    private readonly ITenantMembershipService _inner;
    private readonly IDistributedCache _cache;
    
    public async Task<bool> IsUserMemberOfTenantAsync(Guid tenantId, string userId, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"tenant_membership:{tenantId}:{userId}";
        var cached = await _cache.GetAsync(cacheKey, cancellationToken);
        
        if (cached != null)
            return cached[0] == 1;
        
        var result = await _inner.IsUserMemberOfTenantAsync(tenantId, userId, cancellationToken);
        
        await _cache.SetAsync(cacheKey, new[] { (byte)(result ? 1 : 0) }, 
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) },
            cancellationToken);
        
        return result;
    }
}
```

---

## 🧪 Testing Checklist

Before deploying to production, verify:

### Unit Tests
- [ ] Claims parsing handles all formats (single, JSON array, delimited)
- [ ] `IsUserMemberOfTenantByClaimsOnly` returns correct results
- [ ] Service methods handle null/empty inputs gracefully
- [ ] Exceptions are thrown/caught appropriately

### Integration Tests
- [ ] Database membership check returns correct results
- [ ] Connection with unauthorized tenant is aborted
- [ ] `JoinTenantGroup` succeeds with valid tenant
- [ ] `JoinTenantGroup` fails with invalid tenant
- [ ] `LeaveTenantGroup` handles invalid tenant IDs
- [ ] Multiple concurrent connections work correctly

### Manual Testing (SignalR)
- [ ] User connects to authorized tenant → success
- [ ] User connects to unauthorized tenant → connection rejected
- [ ] User switches workspaces → receives updates only for new tenant
- [ ] User removed from tenant during session → next operation fails
- [ ] Booking updates broadcast only to correct tenant

### Load Testing
- [ ] 100+ concurrent connections per tenant
- [ ] Database membership checks under load
- [ ] Group broadcast performance with 1000+ users
- [ ] No memory leaks with long-lived connections

### Security Testing
- [ ] No cross-tenant data leakage in broadcasts
- [ ] Invalid/forged JWT tokens rejected
- [ ] Tampered tenant IDs rejected
- [ ] User cannot join unauthorized tenants
- [ ] User cannot access other tenant's data

---

## 📋 Deployment Checklist

Before deploying to staging/production:

- [ ] All unit tests passing
- [ ] All integration tests passing
- [ ] Load tests completed successfully
- [ ] Security review completed
- [ ] Database migration tested in staging
- [ ] JWT token generation includes tenant IDs
- [ ] Audit logging configured and working
- [ ] Health checks implemented
- [ ] Monitoring/APM dashboard created
- [ ] Runbook for common issues created
- [ ] Rollback plan documented
- [ ] On-call team trained on new feature
- [ ] User documentation updated

---

## 🆘 Troubleshooting Reference

### Problem: User not in `GetAuthorizedTenantsFromClaims` result
**Solution:** 
1. Verify JWT token includes `tenant_id` claim
2. Check claim format (must be one of: single value, JSON array, or delimited)
3. Add logging to see what claims are present

### Problem: Database membership check always returns false
**Solution:**
1. Verify `UserTenant` table has data
2. Check `RemovedAtUtc` is null for active memberships
3. Verify user ID in JWT matches database value
4. Test query manually: `SELECT * FROM user_tenants WHERE tenant_id = ? AND user_id = ?`

### Problem: Performance degradation with many tenants
**Solution:**
1. Avoid calling `IsUserMemberOfTenantAsync` on every method
2. Consider caching strategy for frequently verified users
3. Add database index on (TenantId, UserId)
4. Monitor database query times with APM

### Problem: Cross-tenant data appearing in broadcasts
**Solution:**
1. Verify tenant groups are created correctly: `tenant:550e8400-...`
2. Verify `PublishBookingUpdate` routes to correct group
3. Check database query filters are applied (AppDbContext.TenantId)
4. Add logging to trace message routing

---

## 📚 Documentation Index

| Document | Purpose |
|----------|---------|
| [MULTITENANT_MEMBERSHIP_SERVICE.md](MULTITENANT_MEMBERSHIP_SERVICE.md) | Complete architecture & usage guide |
| [MULTITENANT_MEMBERSHIP_QUICK_REF.md](MULTITENANT_MEMBERSHIP_QUICK_REF.md) | Quick reference for developers |
| [MULTITENANT_MEMBERSHIP_ARCHITECTURE.md](MULTITENANT_MEMBERSHIP_ARCHITECTURE.md) | System diagrams & data flows |
| [MULTITENANT_MEMBERSHIP_IMPLEMENTATION.md](MULTITENANT_MEMBERSHIP_IMPLEMENTATION.md) | Implementation summary & status |
| This file | Setup checklist & next steps |

---

## 🎯 Success Criteria

Your implementation is production-ready when:

1. ✅ All tests passing (unit, integration, load)
2. ✅ Claims parsing works with your identity provider's format
3. ✅ Database membership verification implemented
4. ✅ Audit logging captures all authorization events
5. ✅ Zero cross-tenant data leakage in SignalR broadcasts
6. ✅ Performance metrics within SLA (P95 < 100ms for claims checks)
7. ✅ Security review completed with 0 critical findings
8. ✅ Monitoring dashboard active
9. ✅ Team trained and runbooks documented
10. ✅ Successful staging deployment

---

## 📞 Support

For questions or issues:
1. Review the documentation above
2. Check the troubleshooting section
3. Run the included test cases
4. Add logging to understand data flow
5. Contact your security/database team for sensitive operations

**Current Status:** ✅ **Ready for development & testing**
