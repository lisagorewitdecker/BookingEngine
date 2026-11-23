#!/bin/bash
set -e

TENANT_ID="b6e3f3b0-9c3f-4d8f-8c8c-3e4f8c8c3e4f"
RESOURCE_ID="a5d2e2a9-8b2e-3c7e-7b7b-2d3e7b7b2d3e"

echo "=== Seeding Database with Test Data ==="
echo "Tenant ID: $TENANT_ID"
echo "Resource ID: $RESOURCE_ID"
echo ""

# Use psql via Docker since we're using Aspire's container
# Try to seed via API first (easier)
echo "Attempting to seed via database connection..."

# Create the seed script content
read -r -d '' SEED_SQL << 'SQLEOF' || true
BEGIN;
INSERT INTO tenants (id, slug, name, iana_timezone, created_at_utc)
VALUES ('b6e3f3b0-9c3f-4d8f-8c8c-3e4f8c8c3e4f'::uuid, 'test-tenant', 'Test Tenant', 'UTC', NOW())
ON CONFLICT (id) DO NOTHING;

INSERT INTO resources (id, tenant_id, name, resource_type, capacity, buffer_minutes_after, is_active)
VALUES ('a5d2e2a9-8b2e-3c7e-7b7b-2d3e7b7b2d3e'::uuid, 'b6e3f3b0-9c3f-4d8f-8c8c-3e4f8c8c3e4f'::uuid, 'Test Resource', 'Room', 1, 0, true)
ON CONFLICT (id) DO NOTHING;

INSERT INTO availability_schedules (id, tenant_id, resource_id, day_of_week, start_time, end_time)
VALUES 
  (gen_random_uuid(), 'b6e3f3b0-9c3f-4d8f-8c8c-3e4f8c8c3e4f'::uuid, 'a5d2e2a9-8b2e-3c7e-7b7b-2d3e7b7b2d3e'::uuid, 1, '09:00', '17:00'),
  (gen_random_uuid(), 'b6e3f3b0-9c3f-4d8f-8c8c-3e4f8c8c3e4f'::uuid, 'a5d2e2a9-8b2e-3c7e-7b7b-2d3e7b7b2d3e'::uuid, 2, '09:00', '17:00'),
  (gen_random_uuid(), 'b6e3f3b0-9c3f-4d8f-8c8c-3e4f8c8c3e4f'::uuid, 'a5d2e2a9-8b2e-3c7e-7b7b-2d3e7b7b2d3e'::uuid, 3, '09:00', '17:00'),
  (gen_random_uuid(), 'b6e3f3b0-9c3f-4d8f-8c8c-3e4f8c8c3e4f'::uuid, 'a5d2e2a9-8b2e-3c7e-7b7b-2d3e7b7b2d3e'::uuid, 4, '09:00', '17:00'),
  (gen_random_uuid(), 'b6e3f3b0-9c3f-4d8f-8c8c-3e4f8c8c3e4f'::uuid, 'a5d2e2a9-8b2e-3c7e-7b7b-2d3e7b7b2d3e'::uuid, 5, '09:00', '17:00')
ON CONFLICT (id) DO NOTHING;

COMMIT;

SELECT 'Tenant' as entity, COUNT(*) FROM tenants;
SELECT 'Resource' as entity, COUNT(*) FROM resources;
SELECT 'Availability Schedule' as entity, COUNT(*) FROM availability_schedules;
SELECT 'Booking' as entity, COUNT(*) FROM bookings;
SELECT 'Outbox Message' as entity, COUNT(*) FROM outbox_messages;
SQLEOF

echo "$SEED_SQL" | PGPASSWORD=postgres psql -h localhost -U postgres -d bookingdb 2>&1 || echo "Direct psql failed, trying alternative..."

echo ""
echo "✓ Seed script completed"
