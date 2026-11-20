using BookingEngine.Api.Common.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BookingEngine.Api.Common.Persistence;

public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__bookingdb") 
            ?? "Host=localhost;Port=5432;Database=bookingdb;Username=postgres;Password=postgres";
        
        optionsBuilder.UseNpgsql(connectionString)
                      .UseSnakeCaseNamingConvention();

        // Create a dummy ITenantContext for design-time scenarios
        var tenantContext = new TenantContext();
        
        return new AppDbContext(optionsBuilder.Options, tenantContext);
    }
}
