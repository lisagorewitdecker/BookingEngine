# OutboxPayloadExtractor & Unit Tests Summary

## Overview

This document summarizes the implementation of **`OutboxPayloadExtractor`** and its comprehensive **xUnit test suite** for safely extracting tenant ID from outbox event payloads in the `BookingEngine` Worker, with proper handling of `camelCase`, nested wrapper objects, and edge cases.

---

## Files Created

### 1. **[OutboxPayloadExtractor.cs](/src/BookingEngine.Worker/OutboxPayloadExtractor.cs)** (3.1 KB)

**Location:** `src/BookingEngine.Worker/`

**Purpose:** 
- Stateless utility class for extracting tenant ID from JSON payloads
- Handles multiple casing conventions: `camelCase`, `PascalCase`, `snake_case`
- Supports nested wrapper objects: `booking`, `data`, `payload`
- Safe fallback behavior for null, empty, or missing values
- No exceptions thrown on invalid input (returns `null` instead)

**Key Methods:**
- `TryExtractTenantId(JsonElement payloadElement)` → `string?`
  - Entry point for tenant ID extraction
  - Returns `null` for non-object JSON, missing/null/empty values

**Helper Methods:**
- `TryReadTenantProperty(JsonElement obj)` → `string?`
  - Attempts to read tenant ID from an object using all known key variants
  - Handles string, number, boolean value types

- `TryGetPropertyCaseInsensitive(JsonElement obj, string propertyName, out JsonElement value)` → `bool`
  - Exact match followed by case-insensitive search across all object properties
  - Mimics `System.Text.Json` behavior in .NET 11.0

### 2. **[OutboxPayloadExtractorTests.cs](/tests/BookingEngine.UnitTests/OutboxPayloadExtractorTests.cs)** (3.8 KB)

**Location:** `tests/BookingEngine.UnitTests/`

**Test Coverage:** 19 test cases across 5 test methods

#### Test Cases:

| Test Method | Scenarios | Count |
|-------------|-----------|-------|
| `TryExtractTenantId_ReturnsTenantId_WhenTopLevelCamelCasePresent` | Standard camelCase `tenantId` | 1 |
| `TryExtractTenantId_HandlesCasingAndTrimsWhitespace` | camelCase, PascalCase, snake_case, UPPER; trim whitespace | 4 |
| `TryExtractTenantId_ReturnsTenantId_FromNestedWrapperObjects` | `booking`, `data`, `payload` containers | 3 |
| `TryExtractTenantId_PrefersTopLevelOverNestedTenantId` | Precedence verification | 1 |
| `TryExtractTenantId_ReturnsNull_WhenMissingNullOrEmpty` | null, empty string, whitespace, missing, non-objects | 7 |
| `JsonDocumentParse_ThrowsJsonException_OnMalformedJson` | Malformed JSON validation | 2 |
| `TryExtractTenantId_DoesNotThrow_WhenJsonDocumentParseHandledSafelyOnMalformedInput` | Safe exception handling pattern | 1 |

**Test Results:** ✅ **19/19 Passed**

#### Key Test Patterns:

1. **Casing Tolerance:**
   ```csharp
   [InlineData("""{ "tenantId": "tenant-camel" }""", "tenant-camel")]
   [InlineData("""{ "TenantId": "tenant-pascal" }""", "tenant-pascal")]
   [InlineData("""{ "tenant_id": "tenant-snake" }""", "tenant-snake")]
   [InlineData("""{ "TENANTID": "tenant-upper" }""", "tenant-upper")]
   ```

2. **Nested Container Support:**
   ```csharp
   [InlineData("""{ "booking": { "tenantId": "tenant-from-booking" } }""", "tenant-from-booking")]
   [InlineData("""{ "data": { "tenantId": "tenant-from-data" } }""", "tenant-from-data")]
   [InlineData("""{ "payload": { "TenantId": "tenant-from-payload" } }""", "tenant-from-payload")]
   ```

3. **Edge Case Safety:**
   - `null` values → returns `null` (not exception)
   - Empty strings and whitespace → returns `null`
   - Non-object JSON root (array, string, number) → returns `null`
   - Missing properties → returns `null`

4. **Malformed JSON Handling:**
   ```csharp
   Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("{ not valid json "));
   ```
   - Caller is responsible for catching `JsonException` / `JsonReaderException`
   - Recommended pattern in `OutboxProcessorWorker`:
     ```csharp
     try
     {
         using var doc = JsonDocument.Parse(message.Payload);
         string? tenantId = OutboxPayloadExtractor.TryExtractTenantId(doc.RootElement);
     }
     catch (JsonException ex)
     {
         logger.LogWarning($"Malformed outbox payload (ID: {message.Id}): {ex.Message}");
         return; // or mark as failed for retry/DLQ
     }
     ```

---

## Integration Points

### 1. **OutboxProcessorWorker** (Expected Integration)

```csharp
using var payloadDoc = JsonDocument.Parse(message.Payload);
string? tenantId = OutboxPayloadExtractor.TryExtractTenantId(payloadDoc.RootElement);

var broadcastMessage = new OutboxBroadcastMessage(
    Id: message.Id,
    EventType: message.EventType,
    OccurredAtUtc: message.OccurredAtUtc,
    Payload: payloadDoc.RootElement,
    TenantId: tenantId);
```

### 2. **SignalR BookingsHub** (Routing)

```csharp
if (!string.IsNullOrWhiteSpace(broadcastMessage.TenantId))
{
    await Clients.Group($"tenant:{broadcastMessage.TenantId}")
        .SendAsync("ReceiveBookingUpdate", broadcastMessage);
}
else
{
    await Clients.All.SendAsync("ReceiveBookingUpdate", broadcastMessage);
}
```

---

## Project Configuration Updates

### **BookingEngine.UnitTests.csproj**

Added project reference to `BookingEngine.Worker`:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\BookingEngine.Api\BookingEngine.Api.csproj" />
  <ProjectReference Include="..\..\src\BookingEngine.Worker\BookingEngine.Worker.csproj" />
</ItemGroup>
```

---

## Running the Tests

### Command:
```bash
dotnet test tests/BookingEngine.UnitTests/BookingEngine.UnitTests.csproj --filter "OutboxPayloadExtractorTests"
```

### Expected Output:
```
Passed!  - Failed: 0, Passed: 19, Skipped: 0, Total: 19, Duration: ~100ms
```

### Filter by Specific Test:
```bash
# Single test
dotnet test tests/BookingEngine.UnitTests/BookingEngine.UnitTests.csproj \
  --filter "OutboxPayloadExtractorTests.TryExtractTenantId_HandlesCasingAndTrimsWhitespace"

# All "ReturnsNull" scenarios
dotnet test tests/BookingEngine.UnitTests/BookingEngine.UnitTests.csproj \
  --filter "OutboxPayloadExtractorTests.TryExtractTenantId_ReturnsNull"
```

---

## Verification Checklist

- [x] `OutboxPayloadExtractor` created and deployed to `BookingEngine.Worker`
- [x] 19 xUnit tests implemented covering:
  - [x] camelCase field extraction
  - [x] Casing variants (PascalCase, snake_case, UPPER)
  - [x] Nested wrapper objects (booking, data, payload)
  - [x] Whitespace trimming
  - [x] Null, empty, and missing value handling
  - [x] Non-object JSON roots
  - [x] Malformed JSON error handling
  - [x] Top-level precedence over nested
- [x] All 19 tests passing
- [x] Worker project reference added to test project
- [x] Documentation ([OUTBOX_PAYLOAD_INSPECTION_GUIDE.md](./OUTBOX_PAYLOAD_INSPECTION_GUIDE.md))

---

## Next Steps

1. **Integrate into `OutboxProcessorWorker.cs`:**
   - Add `JsonDocument.Parse()` with `try/catch (JsonException)`
   - Call `OutboxPayloadExtractor.TryExtractTenantId()`
   - Pass `tenantId` to broadcast message

2. **Update `BookingsHub` routing:**
   - Check `TenantId` in broadcast receiver
   - Route to `tenant:{tenantId}` group if present
   - Fall back to `Clients.All` if `null`

3. **Verify end-to-end:**
   - Create booking via `POST /api/bookings`
   - Inspect outbox payload with PostgreSQL query
   - Confirm worker extracts `tenantId` without errors
   - Verify SignalR clients receive scoped broadcast

---

## References

- [OUTBOX_PAYLOAD_INSPECTION_GUIDE.md](./OUTBOX_PAYLOAD_INSPECTION_GUIDE.md) – SQL queries and integration guide
- [System.Text.Json Docs](https://learn.microsoft.com/en-us/dotnet/api/system.text.json)
- [xUnit Docs](https://xunit.net/)
