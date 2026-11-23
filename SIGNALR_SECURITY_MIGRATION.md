# SignalR Security Implementation - Migration Guide

## Overview

This document guides you through migrating from an insecure query-string-based tenant resolution to a secure claims-based approach using JWT authentication.

## What Was Insecure?

The previous implementation had these security vulnerabilities:

1. **No authentication required:** Any client could connect to `BookingsHub` without credentials.
2. **Unverified tenant ID:** Clients passed `?tenantId=...` and the hub trusted it without validation.
3. **No claim validation:** There was no way to verify a user actually belonged to the requested tenant.
4. **Cross-tenant access possible:** A malicious client could pass any tenant ID and receive updates for that tenant.

### Previous Code
```csharp
// INSECURE - Don't use this anymore!
public override async Task OnConnectedAsync()
{
    var httpContext = Context.GetHttpContext();
    if (httpContext?.Request.Query.TryGetValue("tenantId", out var tenantIdValue) == true &&
        Guid.TryParse(tenantIdValue.ToString(), out var tenantId))
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantId));
    }

    await base.OnConnectedAsync();
}

public async Task JoinTenantGroup(Guid tenantId)
{
    // No validation - any tenant ID accepted!
    await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantId));
}
```

## What's Fixed

The new implementation secures the hub by:

1. **Requiring authentication:** `[Authorize]` attribute blocks unauthenticated connections.
2. **Reading tenant from JWT claims:** Tenant ID comes from signed tokens, not user input.
3. **Validating on every operation:** Each group join/leave validates the tenant matches the user's claim.
4. **Isolating by tenant:** Groups are truly tenant-scoped because tenant ID is verified.

### New Code
```csharp
// SECURE
[Authorize]
public sealed class BookingsHub : Hub
{
    public const string TenantClaimType = "tenant_id";
    
    private static Guid? GetAuthorizedTenantId(ClaimsPrincipal? user)
    {
        // Extract tenant from signed JWT claim
        var tenantIdClaim = user?.FindFirstValue(TenantClaimType);
        if (string.IsNullOrWhiteSpace(tenantIdClaim))
            return null;
        
        if (Guid.TryParse(tenantIdClaim.Trim(), out var tenantId) && tenantId != Guid.Empty)
            return tenantId;
        
        return null;
    }

    public override async Task OnConnectedAsync()
    {
        // Automatically add to tenant's group based on claims
        var tenantId = GetAuthorizedTenantId(Context.User);
        if (tenantId.HasValue)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantId.Value));
        }

        await base.OnConnectedAsync();
    }

    public async Task JoinTenantGroup(Guid tenantId)
    {
        var authorizedTenantId = GetAuthorizedTenantId(Context.User);
        
        // Validate tenant matches user's claim
        if (!authorizedTenantId.HasValue || authorizedTenantId.Value != tenantId)
        {
            throw new HubException("Unauthorized: Cannot join a group for a different tenant.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(authorizedTenantId.Value));
    }
}
```

## Step-by-Step Migration

### Step 1: Add Authentication to Program.cs

**Before:**
```csharp
builder.Services.AddSignalR();
// No authentication!
```

**After:**
```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
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
        
        // Configure your identity provider
        options.Authority = builder.Configuration["Auth:Authority"];
        options.Audience = builder.Configuration["Auth:Audience"];
    });

builder.Services.AddAuthorization();
builder.Services.AddSignalR();
```

### Step 2: Update Pipeline Middleware Order

**Before:**
```csharp
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseMiddleware<TenantResolutionMiddleware>();
app.MapHub<BookingsHub>("/hubs/bookings");
```

**After:**
```csharp
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();      // Add this
app.UseAuthorization();        // Add this
app.UseMiddleware<TenantResolutionMiddleware>();
app.MapHub<BookingsHub>("/hubs/bookings");
```

### Step 3: Update BookingsHub

Replace the entire `BookingsHub` class with the new secure implementation. See [BookingsHub.cs](./src/BookingEngine.Api/Hubs/BookingsHub.cs).

### Step 4: Configure Identity Provider

Add authentication configuration to `appsettings.json`:

**Example for Auth0:**
```json
{
  "Auth": {
    "Authority": "https://your-tenant.auth0.com/",
    "Audience": "https://api.yourapp.com"
  }
}
```

**Example for Azure AD:**
```json
{
  "Auth": {
    "Authority": "https://login.microsoftonline.com/your-tenant-id/v2.0",
    "ClientId": "your-client-id"
  }
}
```

### Step 5: Update Client Code

**Old client code:**
```javascript
// INSECURE - Don't use this anymore!
const connection = new signalR.HubConnectionBuilder()
    .withUrl("wss://api.example.com/hubs/bookings", {
        // Pass tenant as query parameter (unverified!)
    })
    .build();

await connection.start();
await connection.invoke("JoinTenantGroup", tenantId);
```

**New client code:**
```javascript
// SECURE
const connection = new signalR.HubConnectionBuilder()
    .withUrl("wss://api.example.com/hubs/bookings", {
        // Pass authentication token
        accessTokenFactory: async () => {
            // Get token from identity provider
            const token = await getTokenFromIdentityProvider();
            return token;
        }
    })
    .withAutomaticReconnect()
    .build();

await connection.start();
// Optional: JoinTenantGroup still works, but tenant is validated
await connection.invoke("JoinTenantGroup", yourTenantId);
```

## Identity Provider Integration

### Auth0 Integration

1. Create an API in Auth0 dashboard with identifier `https://api.yourapp.com`
2. Create a machine-to-machine (M2M) application for your BookingEngine.Api
3. Configure custom rule or action to add tenant claim:
   ```javascript
   // In Auth0 Post-Login Action
   exports.onExecutePostLogin = async (event, api) => {
       api.idToken.setCustomClaim("tenant_id", event.user.user_metadata.tenant_id);
       api.accessToken.setCustomClaim("tenant_id", event.user.user_metadata.tenant_id);
   };
   ```

### Azure AD Integration

1. Create an app registration in Azure AD
2. Add an application role for the API
3. Configure claims mapping in Token configuration
4. Add tenant claim via Groups or custom claims

### Generic OIDC Provider

1. Ensure token endpoint returns `tenant_id` or similar claim
2. Update `BookingsHub.TenantClaimType` to match your provider's claim name
3. Configure `AddJwtBearer()` with your provider's metadata endpoint

## Testing the Migration

### Pre-Migration Testing

Before deploying to production, test the new implementation:

1. **Create test tokens** at [jwt.io](https://jwt.io) with tenant claims
2. **Test authenticated connection:**
   ```bash
   # With token (should succeed)
   ws://localhost:5001/hubs/bookings?access_token=your.jwt.token
   
   # Without token (should fail with 401 Unauthorized)
   ws://localhost:5001/hubs/bookings
   ```

3. **Test cross-tenant protection:**
   - Connect with token containing `tenant_id: "tenant-a"`
   - Try to join group for `tenant-b`
   - Should receive error: "Unauthorized: Cannot join a group for a different tenant."

### .NET Integration Test Example

```csharp
[Fact]
public async Task CrossTenantAccessIsBlocked()
{
    // Create token for tenant A
    var tokenA = CreateTestToken(tenantId: "12345678-1234-1234-1234-123456789012");
    
    // Connect to hub
    var connection = new HubConnectionBuilder()
        .WithUrl("https://localhost:5001/hubs/bookings", options =>
        {
            options.AccessTokenProvider = () => Task.FromResult(tokenA);
        })
        .Build();
    
    await connection.StartAsync();
    
    // Try to join group for different tenant (should fail)
    var tenantB = "87654321-4321-4321-4321-210987654321";
    var ex = await Assert.ThrowsAsync<HubException>(
        () => connection.InvokeAsync("JoinTenantGroup", Guid.Parse(tenantB))
    );
    
    Assert.Contains("Unauthorized", ex.Message);
}
```

## Rollout Strategy

### Phase 1: Deploy with Backward Compatibility
- Deploy new code but make `[Authorize]` optional (comment it out)
- Support both old (query string) and new (claims) methods
- Allows clients time to upgrade

### Phase 2: Warn on Deprecated Method
- Keep `[Authorize]` optional
- Add logging/monitoring for query-string-based connections
- Notify clients to migrate
- Set deprecation date

### Phase 3: Enforce Authentication
- Enable `[Authorize]` to require tokens
- Old query-string connections will fail with 401 Unauthorized
- All clients must have migrated

### Phase 4: Monitor & Optimize
- Monitor for authentication failures
- Adjust token expiration and refresh strategy
- Fine-tune identity provider configuration

## Troubleshooting During Migration

### Issue: Existing clients stop connecting

**Cause:** `[Authorize]` now requires authentication, old clients don't send tokens.

**Solution:** Phase the rollout. See rollout strategy above.

### Issue: Token validation fails

**Cause:** Identity provider configuration is incorrect.

**Solutions:**
- Verify `Authority` and `Audience` match your provider
- Check that tokens include required claims
- Confirm token signing keys are accessible

### Issue: Tenant claim not found

**Cause:** Identity provider doesn't issue the claim, or claim name doesn't match.

**Solutions:**
- Verify claim is in token: decode at jwt.io
- Confirm claim name matches `TenantClaimType`
- Ask your identity provider to add the claim

## Verification Checklist

Before deploying to production:

- [ ] All clients updated to send authentication tokens
- [ ] Identity provider configured and issuing tokens with tenant claims
- [ ] `AddJwtBearer()` configured with correct authority/audience
- [ ] `OnMessageReceived` reads tokens from `/hubs/bookings` query string
- [ ] Middleware order: `UseAuthentication()` before `UseAuthorization()` before hub mapping
- [ ] BookingsHub has `[Authorize]` attribute
- [ ] Tenant claim name matches identity provider
- [ ] Cross-tenant access is blocked (test with tokens for different tenants)
- [ ] Broadcasting still works (outbox group names match)
- [ ] HTTPS enforced in production (`wss://` not `ws://`)

## Rollback Plan

If issues arise post-deployment:

1. **Temporarily disable `[Authorize]`** in BookingsHub
   ```csharp
   // [Authorize]  // Temporarily commented
   public sealed class BookingsHub : Hub
   ```

2. **Keep query-string fallback** in `OnConnectedAsync()`:
   ```csharp
   public override async Task OnConnectedAsync()
   {
       var tenantId = GetAuthorizedTenantId(Context.User);
       
       // Fallback for emergency situations
       if (!tenantId.HasValue)
       {
           var httpContext = Context.GetHttpContext();
           if (httpContext?.Request.Query.TryGetValue("tenantId", out var value) == true &&
               Guid.TryParse(value.ToString(), out var fallbackTenantId))
           {
               tenantId = fallbackTenantId;
           }
       }
       
       if (tenantId.HasValue)
       {
           await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantId.Value));
       }

       await base.OnConnectedAsync();
   }
   ```

3. **Monitor and analyze issues** before reattempting deployment.

## Resources

- [Full Implementation Guide](./SIGNALR_MULTITENANT_SECURITY.md)
- [Quick Reference](./SIGNALR_SECURITY_QUICK_REFERENCE.md)
- [BookingsHub Implementation](./src/BookingEngine.Api/Hubs/BookingsHub.cs)
- [Program.cs Authentication Setup](./src/BookingEngine.Api/Program.cs)
