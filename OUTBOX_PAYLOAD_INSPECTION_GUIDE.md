# Outbox Payload Inspection & Tenant Extraction Guide (`BookingEngine`)

This guide shows how to inspect the actual JSON written to `payload` in `outbox_messages` (`bookingdb`) when `POST /api/bookings` runs, and how to extract `tenantId` safely—handling `camelCase` field names (as well as casing variations or nested booking objects)—in `OutboxProcessorWorker`.

---

## 1. Verify the Actual Payload Structure in PostgreSQL

After sending a `POST /api/bookings` request, inspect the latest row(s) in `outbox_messages` in `bookingdb` to confirm the real JSON keys, types, and nesting before writing extraction code.

### Pretty-Print the Latest Outbox Payload

```sql
SELECT
    "id",
    "event_type",
    "occurred_at_utc",
    "processed_at_utc",
    jsonb_pretty("payload"::jsonb) AS "payload_pretty"
FROM "outbox_messages"
ORDER BY "occurred_at_utc" DESC
LIMIT 1;
```

### Inspect Top-Level Keys and Candidate Tenant Fields

```sql
SELECT
    "id",
    "event_type",
    jsonb_object_keys("payload"::jsonb) AS "top_level_key"
FROM "outbox_messages"
ORDER BY "occurred_at_utc" DESC
LIMIT 20;
```

Check common `camelCase`, `PascalCase`, `snake_case`, and nested paths on recent rows:

```sql
SELECT
    "id",
    "event_type",
    "occurred_at_utc",
    "payload"::jsonb ->> 'tenantId'                AS "camel_tenant_id",
    "payload"::jsonb ->> 'TenantId'                AS "pascal_tenant_id",
    "payload"::jsonb ->> 'tenant_id'               AS "snake_tenant_id",
    "payload"::jsonb #>> '{booking,tenantId}'      AS "nested_booking_camel_tenant_id",
    "payload"::jsonb #>> '{booking,TenantId}'      AS "nested_booking_pascal_tenant_id"
FROM "outbox_messages"
ORDER BY "occurred_at_utc" DESC
LIMIT 5;
```

## 2. Handle `camelCase` Field Names Safely in `OutboxProcessorWorker`

When your outbox event is serialized with `System.Text.Json` using `JsonNamingPolicy.CamelCase` (the standard web/API default), property names in JSON are written as `tenantId` (or nested under an object such as `booking.tenantId`).

A safe extractor should:

1. Check that `payloadElement.ValueKind == JsonValueKind.Object`.
2. Look up `camelCase` (`"tenantId"`) first, with safe fallbacks for `"TenantId"`, `"tenant_id"`, and optional nested objects (such as `"booking"` or `"data"`).
3. Support both string values and `Guid` / non-string scalar values cleanly without throwing if a property is missing or `null`.

### Safe Helper in C# (`System.Text.Json`)

```csharp
using System.Text.Json;

public static class OutboxPayloadExtractor
{
    private static readonly string[] DirectTenantKeys =
    [
        "tenantId",
        "TenantId",
        "tenant_id"
    ];

    private static readonly string[] NestedContainerKeys =
    [
        "booking",
        "data",
        "payload"
    ];

    public static string? TryExtractTenantId(JsonElement payloadElement)
    {
        if (payloadElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 1. Check top-level keys (camelCase first)
        var direct = TryReadTenantProperty(payloadElement);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        // 2. Check common nested wrapper objects (e.g., payload.booking.tenantId)
        foreach (var containerKey in NestedContainerKeys)
        {
            if (TryGetPropertyCaseInsensitive(payloadElement, containerKey, out var container) &&
                container.ValueKind == JsonValueKind.Object)
            {
                var nested = TryReadTenantProperty(container);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? TryReadTenantProperty(JsonElement obj)
    {
        foreach (var key in DirectTenantKeys)
        {
            if (!TryGetPropertyCaseInsensitive(obj, key, out var prop))
            {
                continue;
            }

            switch (prop.ValueKind)
            {
                case JsonValueKind.String:
                    var str = prop.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(str))
                    {
                        return str;
                    }
                    break;

                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return prop.ToString();
            }
        }

        return null;
    }

    private static bool TryGetPropertyCaseInsensitive(
        JsonElement obj,
        string propertyName,
        out JsonElement value)
    {
        if (obj.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
```

## 3. Wire Extraction Into `OutboxProcessorWorker`

Parse the outbox `payload` string into a `JsonDocument` (or `JsonElement`) and pass the extracted `tenantId` into your broadcast payload sent to `BookingEngine.Api`:

```csharp
using System.Text.Json;

using var payloadDoc = JsonDocument.Parse(message.Payload);
var payloadElement = payloadDoc.RootElement;

string? tenantId = OutboxPayloadExtractor.TryExtractTenantId(payloadElement);

var broadcastMessage = new OutboxBroadcastMessage(
    Id: message.Id,
    EventType: message.EventType,
    OccurredAtUtc: message.OccurredAtUtc,
    Payload: payloadElement,
    TenantId: tenantId);
```

### Verification Checklist

- [ ] Trigger a new booking via `POST /api/bookings` and run the `jsonb_pretty` query in `bookingdb` to confirm `"tenantId"` appears in `camelCase`.
- [ ] Confirm `OutboxProcessorWorker` extracts `tenantId` without exceptions when `tenantId` is a string, a GUID string, `null`, or omitted on non-tenant system events.
- [ ] Confirm `BookingEngine.Api` routes broadcasts with `TenantId` to `BookingsHub.TenantGroup(tenantId)` (`tenant:{tenantId}`) while leaving unscoped broadcasts on `Clients.All` only when `TenantId` is null/empty.
