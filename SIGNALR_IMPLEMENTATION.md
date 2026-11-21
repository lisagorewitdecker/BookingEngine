# SignalR Hub Integration for Booking Engine

## Overview
This document describes the implementation of SignalR integration for real-time broadcasting of booking updates from the outbox worker to connected clients.

## Architecture
- **SignalR Hub** (`BookingEngine.Api`): Handles client connections and broadcasts booking updates
- **Outbox Worker** (`BookingEngine.Worker`): Polls the outbox table and broadcasts messages via SignalR to connected clients
- **AppHost** (`BookingEngine.AppHost`): Orchestrates service startup and passes the API endpoint to the Worker

## Components Implemented

### 1. SignalR Hub (`src/BookingEngine.Api/Hubs/BookingsHub.cs`)
```csharp
public sealed class BookingsHub : Hub
{
    // Clients connect to /hubs/bookings and listen for the broadcast method
    // configured in OutboxWorkerOptions.BroadcastMethod (e.g., "BookingUpdated").
}
```

**Key Details:**
- Endpoint: `/hubs/bookings`
- Broadcast method: `BookingUpdated` (configurable via `OutboxWorkerOptions.BroadcastMethod`)
- Accepts `OutboxBroadcastMessage` objects from the worker

### 2. API Program Configuration (`src/BookingEngine.Api/Program.cs`)
**Changes:**
- Added `builder.Services.AddSignalR();` to register SignalR services
- Added `app.MapHub<BookingsHub>("/hubs/bookings");` to map the hub endpoint
- Added using statement: `using BookingEngine.Api.Hubs;`

### 3. OutboxMessage Entity Updates
**Updated:** `src/BookingEngine.Api/Common/Persistence/OutboxMessage.cs`

**Changes:**
- Changed `OccurredAtUtc` to use `DateTimeOffset` (was `DateTime`)
- Changed `ProcessedAtUtc` to use `DateTimeOffset?` (was `DateTime?`)
- This provides better timezone-aware handling

**Entity Structure:**
```csharp
public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; init; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? Error { get; set; }
}
```

### 4. Database Context Configuration (`src/BookingEngine.Api/Common/Persistence/AppDbContext.cs`)
- Configured OutboxMessage entity mapping with proper column types
- Added filtered index on `OccurredAtUtc` where `ProcessedAtUtc IS NULL`
- Maintains snake_case column naming convention

### 5. AppHost Configuration (`src/BookingEngine.AppHost/AppHost.cs`)
**Complete implementation:**
```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .AddDatabase("bookingdb");

var api = builder.AddProject<Projects.BookingEngine_Api>("api")
    .WithReference(postgres)
    .WaitFor(postgres);

builder.AddProject<Projects.BookingEngine_Worker>("worker")
    .WithReference(postgres)
    .WithReference(api)
    .WithEnvironment("OutboxWorker__ApiBaseUrl", api.GetEndpoint("https"))
    .WaitFor(postgres)
    .WaitFor(api);

builder.Build().Run();
```

**Key Features:**
- Passes API endpoint to Worker via `OutboxWorker__ApiBaseUrl` environment variable
- Ensures Worker waits for both PostgreSQL and API to be ready before starting
- Uses `api.GetEndpoint("https")` for secure communication (adjust to "http" if needed)

### 6. Worker Configuration (`src/BookingEngine.Worker/Program.cs`)
**Changes:**
- Added DbContext registration: `builder.AddNpgsqlDbContext<AppDbContext>("bookingdb", ...)`
- Added import: `using BookingEngine.Api.Common.Persistence;`

**Complete Program.cs:**
```csharp
using Microsoft.Extensions.Hosting;
using BookingEngine.Worker;
using BookingEngine.Api.Common.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// Add Aspire ServiceDefaults
builder.AddServiceDefaults();

// Add DbContext for OutboxMessage access
builder.AddNpgsqlDbContext<AppDbContext>("bookingdb", configureDbContextOptions: options =>
{
    options.UseSnakeCaseNamingConvention();
});

// Configure OutboxProcessorWorker options
builder.Services
    .AddOptions<OutboxWorkerOptions>()
    .Bind(builder.Configuration.GetSection(OutboxWorkerOptions.SectionName))
    .ValidateDataAnnotations();

// Add BackgroundService
builder.Services.AddHostedService<OutboxProcessorWorker>();

var host = builder.Build();

await host.RunAsync();

public partial class Program;
```

### 7. OutboxProcessorWorker Updates (`src/BookingEngine.Worker/OutboxProcessorWorker.cs`)
**Changes:**
- Added import: `using BookingEngine.Api.Common.Persistence;`
- Updated `ProcessBatchAsync` to use `AppDbContext` instead of generic `DbContext`
- Maintains SQL queries for PostgreSQL-specific operations (SKIP LOCKED)
- Uses snake_case column names from database (`occurred_at_utc`, `processed_at_utc`)

**Key Features:**
- Polls outbox messages table for unprocessed entries
- Uses `FOR UPDATE SKIP LOCKED` for safe concurrent processing across multiple worker instances
- Broadcasts messages via SignalR Hub
- Marks processed messages with current timestamp
- Implements exponential backoff and automatic reconnection

### 8. Worker Application Settings (`src/BookingEngine.Worker/appsettings.Development.json`)
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "OutboxWorker": {
    "ApiBaseUrl": "https://localhost:7046",
    "HubPath": "/hubs/bookings",
    "BroadcastMethod": "BookingUpdated",
    "PollIntervalMilliseconds": 1000,
    "BatchSize": 50
  }
}
```

**Configuration Options:**
- **ApiBaseUrl**: Base URL of BookingEngine.Api (overridden by AppHost in orchestrated environments)
- **HubPath**: Relative path to SignalR hub (must match MapHub configuration)
- **BroadcastMethod**: Method name invoked on connected clients
- **PollIntervalMilliseconds**: Delay when no unprocessed messages are found (default: 1000ms)
- **BatchSize**: Maximum number of messages processed per batch (default: 50)

## Database Schema
The `outbox_messages` table has the following structure:
```sql
CREATE TABLE "outbox_messages" (
  "id" UUID PRIMARY KEY,
  "tenant_id" UUID NOT NULL,
  "event_type" VARCHAR(128) NOT NULL,
  "payload" JSONB NOT NULL,
  "occurred_at_utc" TIMESTAMP WITH TIME ZONE NOT NULL,
  "processed_at_utc" TIMESTAMP WITH TIME ZONE,
  "error" TEXT
);

CREATE INDEX ON "outbox_messages" ("occurred_at_utc") 
  WHERE "processed_at_utc" IS NULL;
```

## Configuration Binding
The double underscore (`__`) in `OutboxWorker__ApiBaseUrl` automatically binds to nested properties:
- `OutboxWorker__ApiBaseUrl` → `OutboxWorkerOptions.ApiBaseUrl`
- This is handled by .NET's default configuration provider

## Running the Solution

### With AppHost (Recommended)
```bash
cd src/BookingEngine.AppHost
dotnet run
```

This will:
1. Start PostgreSQL container
2. Apply migrations automatically
3. Start BookingEngine.Api
4. Start BookingEngine.Worker with proper configuration

### Individual Projects (Development)
```bash
# Terminal 1: API
cd src/BookingEngine.Api
dotnet run

# Terminal 2: Worker
cd src/BookingEngine.Worker
# Ensure appsettings.Development.json has correct ApiBaseUrl
dotnet run
```

## Client Integration (Frontend)

### JavaScript/TypeScript Example
```typescript
import * as signalR from "@aspnetcore/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("https://localhost:7046/hubs/bookings")
  .withAutomaticReconnect()
  .build();

connection.on("BookingUpdated", (message) => {
  console.log("Booking update received:", {
    messageId: message.messageId,
    eventType: message.eventType,
    occurredAt: message.occurredAtUtc,
    payload: message.payload
  });
  // Update UI with the new booking information
});

connection.start().catch(err => console.error("Connection failed:", err));
```

## Verification Checklist

- [x] SignalR Hub created and registered in API
- [x] Hub endpoint mapped at `/hubs/bookings`
- [x] AppHost configured with API and Worker references
- [x] Worker receives API endpoint via `OutboxWorker__ApiBaseUrl` environment variable
- [x] Worker registered with AppHost to wait for API startup
- [x] DbContext added to Worker for outbox message polling
- [x] OutboxMessage entity uses DateTimeOffset for UTC timestamps
- [x] OutboxProcessorWorker uses AppDbContext
- [x] Development configuration includes OutboxWorker settings
- [x] Solution builds successfully without errors
- [x] Column names match between entity and database schema

## Common Issues & Solutions

### Issue: Worker cannot connect to API
**Solution:** Verify `ApiBaseUrl` in appsettings.Development.json matches the API's launch profile URL. Check the API's `launchSettings.json` for the correct port (currently: https://localhost:7046 or http://localhost:5011).

### Issue: Outbox messages not being processed
**Solution:** Verify the Worker can access the database. Check that migrations have been applied and the `outbox_messages` table exists.

### Issue: SignalR connection fails with CORS error
**Solution:** Add CORS configuration to API if the client runs on a different domain:
```csharp
builder.Services.AddCors(options => options.AddPolicy("SignalR", builder => 
    builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
```

### Issue: "ProcessedAtUtc" column not found
**Solution:** Ensure the latest migration has been applied. The column is `processed_at_utc` in snake_case in the database.

## Next Steps

1. **Create domain events** in Booking feature that populate OutboxMessage when bookings are created/updated
2. **Implement event handlers** that add events to the outbox when domain events occur
3. **Test end-to-end** broadcasting by creating a booking and verifying the message reaches connected SignalR clients
4. **Configure CORS** if frontend is on a different domain
5. **Add authentication/authorization** if needed for SignalR hub access
6. **Monitor performance** as outbox table grows and adjust `BatchSize` and `PollIntervalMilliseconds` as needed
