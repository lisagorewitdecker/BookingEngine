using BookingEngine.Api.Common.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace BookingEngine.IntegrationTests.Infrastructure;

public sealed class BookingEngineWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "ConnectionStrings__bookingdb";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("bookingdb")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        // 1. Start PostgreSQL and expose ConnectionStrings:bookingdb via env var
        //    before building the WebApplicationFactory host/Services.
        await _postgres.StartAsync();
        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, ConnectionString);

        // 2. Ensure host is created with the database connection string set
        _ = this.Services;

        // 3. Create the database schema directly from DbContext model
        // (instead of using migrations, which have compatibility issues)
        using var scope = this.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        try
        {
            // Drop any existing schema and recreate it
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();
            
            // Verify tables exist
            await dbContext.AvailabilitySchedules.CountAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Database initialization failed: {ex.Message}", ex);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        if (_postgres.State == DotNet.Testcontainers.Containers.TestcontainersStates.Running)
        {
            builder.UseSetting("ConnectionStrings:bookingdb", ConnectionString);
        }

        // For testing, disable DbContext pooling to ensure ITenantContext is fresh per scope
        builder.ConfigureServices(services =>
        {
            // Remove any existing DbContext registration (Aspire may have registered it)
            var dbContextDescriptors = services
                .Where(d => d.ServiceType == typeof(AppDbContext) || d.ServiceType.Name.Contains("DbContextFactory"))
                .ToList();
            
            foreach (var descriptor in dbContextDescriptors)
            {
                services.Remove(descriptor);
            }

            // Register DbContext WITHOUT pooling for tests
            services.AddDbContext<AppDbContext>((serviceProvider, options) =>
            {
                options
                    .UseNpgsql(ConnectionString)
                    .UseSnakeCaseNamingConvention();
            }, ServiceLifetime.Scoped, ServiceLifetime.Singleton);
        });
    }

    public new async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(ConnectionStringEnvVar, null);
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}