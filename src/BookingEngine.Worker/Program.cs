using Microsoft.Extensions.Hosting;
using BookingEngine.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Add Aspire ServiceDefaults
builder.AddServiceDefaults();

// Add BackgroundService
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

await host.RunAsync();

// Expose Program class for potential test factories
public partial class Program;