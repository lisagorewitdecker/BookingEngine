# BookingEngine

A .NET 10 / .NET Aspire application featuring an API service, PostgreSQL persistence (`bookingdb`) with Entity Framework Core and an outbox pattern (`outbox_messages`), real-time SignalR broadcasts (`BookingsHub`), and a background outbox worker (`BookingEngine.Worker`).

## Solution Structure

- `src/BookingEngine.AppHost` — .NET Aspire orchestration host (`postgres`, `bookingdb`, `api`, and `worker`).
- `src/BookingEngine.Api` — ASP.NET Core API exposing booking endpoints (`POST /api/bookings`) and SignalR hub `/hubs/bookings`.
- `src/BookingEngine.Worker` — Background worker polling `outbox_messages` in `bookingdb` and broadcasting updates.

## Running Locally with .NET Aspire

From the repository root:

```bash
dotnet run --project src/BookingEngine.AppHost