# SignalR Multi-Tenant Security Implementation Guide

## Overview

This document describes the security measures implemented in `BookingsHub` to enforce tenant isolation and prevent unauthorized cross-tenant access via SignalR WebSocket connections.

## Key Security Improvements

### 1. Authentication Requirement: `[Authorize]` Attribute

The `BookingsHub` now requires all clients to authenticate before connecting:

```csharp
[Authorize]
public sealed class BookingsHub : Hub
{
    // ...
}
```

**Why:** Unauthenticated users cannot establish connections to the hub, preventing anonymous access to any tenant groups.

### 2. Tenant Resolution from Claims (Not Query Parameters)

The hub no longer trusts a `?tenantId=...` query parameter. Instead, it reads the tenant ID from the authenticated user's claims:

```csharp
private static Guid? GetAuthorizedTenantId(ClaimsPrincipal? user)
{
    if (user == null)
        return null;

    // Try multiple claim type variations for compatibility
    var tenantIdClaim = user.FindFirstValue(TenantClaimType)
        ?? user.FindFirstValue("tenantId")
        ?? user.FindFirstValue("tenant");

    if (string.IsNullOrWhiteSpace(tenantIdClaim))
        return null;

    if (Guid.TryParse(tenantIdClaim.Trim(), out var tenantId) && tenantId != Guid.Empty)
        return tenantId;

    return null;
}
```

**Why:** Claims are signed by the identity provider and cannot be forged by clients. This is the source of truth for which tenant a user belongs to.

### 3. Automatic Group Join on Connection

When a client connects, they are automatically added to their tenant's group based on their authenticated claims:

```csharp
public override async Task OnConnectedAsync()
{
    var tenantId = GetAuthorizedTenantId(Context.User);
    if (tenantId.HasValue)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(tenantId.Value));
    }

    await base.OnConnectedAsync();
}
```

**Why:** This ensures each authenticated connection is immediately scoped to their tenant without requiring manual group joins.

### 4. Explicit Tenant Validation on Manual Joins

If a client attempts to manually join a group via `JoinTenantGroup()`, the hub validates that the requested tenant matches their authenticated tenant claim:

```csharp
public async Task JoinTenantGroup(Guid tenantId)
{
    var authorizedTenantId = GetAuthorizedTenantId(Context.User);
    
    if (!authorizedTenantId.HasValue)
    {
        throw new HubException("Unauthorized: No tenant claim found.");
    }

    // Enforce exact match against the authenticated user's tenant claim
    if (authorizedTenantId.Value != tenantId)
    {
        throw new HubException("Unauthorized: Cannot join a group for a different tenant.");
    }

    await Groups.AddToGroupAsync(Context.ConnectionId, GetTenantGroupName(authorizedTenantId.Value));
}
```

**Why:** Even if a malicious client tries to forge a tenant ID in the method call, the hub rejects it because it doesn't match their claims.

### 5. JWT Bearer Authentication with Query String Token Support

`Program.cs` now configures JWT Bearer authentication to read tokens from the query string for SignalR connections:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // For SignalR WebSocket connections, browsers cannot set custom Authorization headers,
                // so we read the token from the query string instead
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
        // ... token validation configuration
    });
```

**Why:** Browser WebSocket connections cannot set custom `Authorization` headers, so we accept the token on the query string *only* for the SignalR hub path, then validate it like any other JWT.

---

## Configuration: Identity Provider & Claims

### Setting Up Your Identity Provider

The JWT Bearer authentication middleware needs to validate tokens signed by your identity provider. Update the `AddJwtBearer()` configuration in `Program.cs` with your provider's details:

#### Example: Auth0

```csharp
options.Authority = builder.Configuration["Auth:Authority"]; // e.g., "https://your-tenant.auth0.com/"
options.Audience = builder.Configuration["Auth:Audience"];    // e.g., "https://api.yourapp.com"
options.TokenValidationParameters = new TokenValidationParameters
{
    NameClaimType = "name",
    RoleClaimType = "role"
};
```

Add these to `appsettings.json`:
```json
{
  "Auth": {
    "Authority": "https://your-tenant.auth0.com/",
    "Audience": "https://api.yourapp.com"
  }
}
```

#### Example: Azure AD

```csharp
options.Authority = $"https://login.microsoftonline.com/{builder.Configuration["Auth:TenantId"]}/v2.0";
options.Audience = builder.Configuration["Auth:ClientId"];
options.TokenValidationParameters = new TokenValidationParameters
{
    ValidIssuers = new[] { $"https://login.microsoftonline.com/{builder.Configuration["Auth:TenantId"]}/v2.0" }
};
```

#### Example: Custom OIDC Provider

```csharp
options.Authority = builder.Configuration["Auth:Authority"];
options.Audience = builder.Configuration["Auth:Audience"];
// Other standard OIDC configuration
```

### Tenant Claim Configuration

Ensure your identity provider includes a tenant ID claim in issued tokens. Common claim names:
- `tenant_id` (most common)
- `tenantId`
- `tenant`
- `org_id` (some providers)
- Custom URI claims like `https://yourapp.com/tenant`

The `BookingsHub.GetAuthorizedTenantId()` method checks multiple common variations. If your provider uses a different claim name, update `TenantClaimType`:

```csharp
public const string TenantClaimType = "your_custom_claim_name";
```

---

## Development/Testing Setup

For development without a full identity provider setup, the template includes a placeholder that skips token validation:

```csharp
if (builder.Environment.IsDevelopment())
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = false,
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = false,
        SignatureValidator = (token, parameters) => new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(token)
    };
}
```

**Warning:** This skips all token validation and should **never** be used in production. For development, you can generate test tokens locally or use your identity provider's test endpoints.

### Creating Test Tokens

For development testing with realistic token structures, use a tool like:
- [JWT.io](https://jwt.io/) - for manual token creation
- [Auth0 Token Generator](https://auth0.com/resources/whitepapers/jwt-handbook) - if using Auth0
- Your identity provider's test/development tools

A minimal test token payload might look like:
```json
{
  "sub": "user123",
  "tenant_id": "12345678-1234-1234-1234-123456789012",
  "iat": 1234567890,
  "exp": 9999999999
}
```

Pass this token to your WebSocket client via the query string:
```
wss://localhost:5001/hubs/bookings?access_token=eyJhbGc...
```

---

## Client-Side Implementation

### Browser/JavaScript Client

Use the SignalR client library with token support:

```javascript
import * as signalR from "@microsoft/signalr";

async function connectToBookingsHub(accessToken) {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("wss://your-api.com/hubs/bookings", {
            accessTokenFactory: () => accessToken
        })
        .withAutomaticReconnect()
        .build();

    connection.on("BookingUpdated", (message) => {
        console.log("Booking update:", message);
    });

    try {
        await connection.start();
        console.log("Connected to BookingsHub");
        
        // Subscribe to tenant's group (optional - automatic on connect)
        await connection.invoke("JoinTenantGroup", yourTenantId);
    } catch (err) {
        console.error("Connection failed:", err);
    }
}

// Get token from your identity provider's login flow
const token = await getAccessTokenFromIdentityProvider();
await connectToBookingsHub(token);
```

The SignalR client automatically passes `accessTokenFactory()` as a query parameter to the WebSocket connection.

### .NET Client (Server-to-Server)

For server-side clients (e.g., the outbox worker connecting to the hub):

```csharp
using Microsoft.AspNetCore.SignalR.Client;

var connection = new HubConnectionBuilder()
    .WithUrl("https://your-api.com/hubs/bookings", options =>
    {
        options.AccessTokenProvider = async () =>
        {
            // Fetch or generate a token for the outbox worker
            var token = await GetTokenForOutboxWorker();
            return token;
        };
    })
    .WithAutomaticReconnect()
    .Build();

connection.On<BookingUpdateMessage>("PublishBookingUpdate", async message =>
{
    // Handle the message
});

await connection.StartAsync();
```

---

## Validation Checklist for Multi-Tenant Safety

- [ ] **`[Authorize]` attribute is on `BookingsHub`:** Prevents unauthenticated connections.
- [ ] **Tenant ID is read from `Context.User.Claims`**, not from `?tenantId=...` query parameter.
- [ ] **`JoinTenantGroup()`** validates that the requested tenant matches the authenticated user's claim.
- [ ] **`LeaveTenantGroup()`** only allows leaving the user's own authenticated tenant's group.
- [ ] **JWT Bearer authentication is configured** in `Program.cs` with your identity provider.
- [ ] **`OnMessageReceived`** event handler reads tokens from query string *only for `/hubs/bookings` path*.
- [ ] **Claim name matches your identity provider:** Adjust `TenantClaimType` if needed.
- [ ] **Group names are consistent:** `BookingsHub.GetTenantGroupName()` is used everywhere (Hubs and `/internal/outbox/broadcast` endpoint).
- [ ] **Token validation is enabled in production:** Never use the development placeholder in production.
- [ ] **HTTPS is enforced:** WebSocket connections should use `wss://` (secure) in production.

---

## Common Issues & Troubleshooting

### Issue: "Unauthorized" Error on Hub Connection

**Causes:**
1. Token is not being passed to the WebSocket connection.
2. Token is invalid or expired.
3. Token does not contain a tenant claim.

**Solutions:**
- Verify the client passes `accessTokenFactory` or the query string `?access_token=...`.
- Check token validity using JWT.io or your identity provider's token inspection tool.
- Confirm your identity provider is issuing the expected tenant claim.
- Enable debug logging: `LogLevel: Trace` in `appsettings.Development.json`.

### Issue: "Unauthorized: Cannot join a group for a different tenant"

**Cause:** A client is attempting to join a tenant group that doesn't match their authenticated tenant claim.

**Solution:** 
- Verify the client is passing the correct tenant ID.
- Confirm the token contains the correct tenant claim.
- Check that the claim name matches `TenantClaimType`.

### Issue: Broadcasting to Group Not Working

**Cause:** Group name mismatch between the hub and your broadcast endpoint.

**Solution:**
- Ensure `/internal/outbox/broadcast` uses `BookingsHub.GetTenantGroupName(tenantId)`.
- Verify group names are identical: `$"tenant:{tenantId}"`.

### Issue: Development Token Validation Skipped

**Cause:** Development placeholder is bypassing token validation.

**Solution:**
- This is intentional for development without an identity provider.
- For realistic testing, configure a real identity provider or use a tool to generate test tokens.
- **Never commit development placeholder to production configurations.**

---

## Migration Guide: From Query String to Claims

If you have existing clients using `?tenantId=...`, migrate them to use authenticated tokens:

### Old Flow (Insecure)
```
wss://api.example.com/hubs/bookings?tenantId=12345678-1234-1234-1234-123456789012
```
✗ No authentication, tenant ID is unverified.

### New Flow (Secure)
```
wss://api.example.com/hubs/bookings?access_token=eyJhbGc...
```
✓ Token is signed by identity provider, tenant ID is in claims.

### Client Migration Steps

1. **Obtain access token** from your identity provider's login endpoint.
2. **Pass token to SignalR client** via `accessTokenFactory` or query string.
3. **Remove query string `tenantId` parameter** from connection URLs.
4. **Test connection** with the new token-based method.

---

## References

- [SignalR Authentication & Authorization](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz)
- [JWT Bearer Authentication in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/jwt-bearer)
- [SignalR Security Considerations](https://learn.microsoft.com/en-us/aspnet/core/signalr/security)
- [Multi-Tenant Applications with SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups#scalability)
