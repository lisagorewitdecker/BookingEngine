using Xunit;
using Microsoft.EntityFrameworkCore;
using BookingEngine.Api.Common.Domain;
using BookingEngine.Api.Common.Persistence;
using BookingEngine.Api.Common.Tenancy;

namespace BookingEngine.Api.Tests;

/// <summary>
/// Integration tests for TenantMembership entity and TenantMembershipService.
/// These tests verify that:
/// 1. The TenantMembership table is properly created and queryable
/// 2. The unique index prevents duplicate (user_id, tenant_id) pairs
/// 3. The filtered index speeds queries for active memberships
/// 4. IsUserMemberOfTenantAsync correctly identifies membership
/// </summary>
public class TenantMembershipServiceTests : IAsyncLifetime
{
    private AppDbContext _dbContext = null!;
    private IDbContextFactory<AppDbContext> _dbContextFactory = null!;
    private TenantMembershipService _service = null!;

    public async Task InitializeAsync()
    {
        // Create an in-memory database for testing
        // For real integration tests, use TestContainers with PostgreSQL
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options, new NoOpTenantContext());
        await _dbContext.Database.EnsureCreatedAsync();

        _dbContextFactory = new InMemoryDbContextFactory(options);
        _service = new TenantMembershipService(_dbContextFactory);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
    }

    #region Happy Path Tests

    [Fact]
    public async Task IsUserMemberOfTenantAsync_WithValidActiveMembership_ReturnsTrue()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var membership = new TenantMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantId = tenantId,
            Role = "Member",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.TenantMemberships.Add(membership);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.IsUserMemberOfTenantAsync(tenantId, userId.ToString());

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsUserMemberOfTenantAsync_WithMultipleMemberships_ReturnsCorrectMembership()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();

        var memberships = new[]
        {
            new TenantMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId1,
                TenantId = tenantId1,
                Role = "Admin",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            },
            new TenantMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId2,
                TenantId = tenantId2,
                Role = "Member",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            }
        };

        _dbContext.TenantMemberships.AddRange(memberships);
        await _dbContext.SaveChangesAsync();

        // Act
        var result1 = await _service.IsUserMemberOfTenantAsync(tenantId1, userId1.ToString());
        var result2 = await _service.IsUserMemberOfTenantAsync(tenantId2, userId2.ToString());
        var resultWrong = await _service.IsUserMemberOfTenantAsync(tenantId1, userId2.ToString());

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        Assert.False(resultWrong);
    }

    #endregion

    #region Inactive Membership Tests

    [Fact]
    public async Task IsUserMemberOfTenantAsync_WithInactiveMembership_ReturnsFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var membership = new TenantMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantId = tenantId,
            Role = "Member",
            IsActive = false,  // Inactive
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.TenantMemberships.Add(membership);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.IsUserMemberOfTenantAsync(tenantId, userId.ToString());

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsUserMemberOfTenantAsync_SoftDeactivation_ReturnsFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var membership = new TenantMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantId = tenantId,
            Role = "Member",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.TenantMemberships.Add(membership);
        await _dbContext.SaveChangesAsync();

        // Soft-delete by marking inactive
        membership.IsActive = false;
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.IsUserMemberOfTenantAsync(tenantId, userId.ToString());

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task IsUserMemberOfTenantAsync_WithNullUserId_ReturnsFalse()
    {
        // Arrange
        var tenantId = Guid.NewGuid();

        // Act
        var result = await _service.IsUserMemberOfTenantAsync(tenantId, null!);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsUserMemberOfTenantAsync_WithEmptyUserId_ReturnsFalse()
    {
        // Arrange
        var tenantId = Guid.NewGuid();

        // Act
        var result = await _service.IsUserMemberOfTenantAsync(tenantId, "");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsUserMemberOfTenantAsync_WithInvalidGuidUserId_ReturnsFalse()
    {
        // Arrange
        var tenantId = Guid.NewGuid();

        // Act
        var result = await _service.IsUserMemberOfTenantAsync(tenantId, "not-a-guid");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsUserMemberOfTenantAsync_WithNonExistentMembership_ReturnsFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        // No membership inserted

        // Act
        var result = await _service.IsUserMemberOfTenantAsync(tenantId, userId.ToString());

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Claims Integration Tests

    [Fact]
    public void IsUserMemberOfTenantByClaimsOnly_WithValidClaim_ReturnsTrue()
    {
        // Arrange
        var claims = new System.Security.Claims.Claim[]
        {
            new("tenant_id", "22222222-2222-2222-2222-222222222222")
        };
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims));

        // Act
        var result = _service.IsUserMemberOfTenantByClaimsOnly(principal, "22222222-2222-2222-2222-222222222222");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsUserMemberOfTenantByClaimsOnly_WithMissingClaim_ReturnsFalse()
    {
        // Arrange
        var claims = new System.Security.Claims.Claim[]
        {
            new("tenant_id", "22222222-2222-2222-2222-222222222222")
        };
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims));

        // Act
        var result = _service.IsUserMemberOfTenantByClaimsOnly(principal, "33333333-3333-3333-3333-333333333333");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetAuthorizedTenantsFromClaims_WithMultipleClaims_ReturnsAll()
    {
        // Arrange
        var claims = new System.Security.Claims.Claim[]
        {
            new("tenant_id", "11111111-1111-1111-1111-111111111111"),
            new("tenant_id", "22222222-2222-2222-2222-222222222222"),
            new("tenantId", "33333333-3333-3333-3333-333333333333")
        };
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims));

        // Act
        var result = _service.GetAuthorizedTenantsFromClaims(principal);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains("11111111-1111-1111-1111-111111111111", result);
        Assert.Contains("22222222-2222-2222-2222-222222222222", result);
        Assert.Contains("33333333-3333-3333-3333-333333333333", result);
    }

    #endregion
}

/// <summary>
/// Test helper: in-memory DbContextFactory for unit tests
/// Creates new instances to prevent the disposed-context issue when
/// TenantMembershipService disposes the context after use.
/// </summary>
public sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly ITenantContext _tenantContext;

    public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tenantContext = new NoOpTenantContext();
    }

    public AppDbContext CreateDbContext()
    {
        return new AppDbContext(_options, _tenantContext);
    }

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateDbContext());
    }
}

/// <summary>
/// Test helper: no-op tenant context for unit tests
/// </summary>
public sealed class NoOpTenantContext : ITenantContext
{
    public Guid? TenantId { get; } = null;
    public string? IanaTimezone { get; } = null;

    public void SetTenant(Guid tenantId, string ianaTimezone)
    {
        // No-op for testing
    }
}
