-- 0027 AIDX 22.1 inbound (ARV-044): an AIDX message sent with an Idempotency-Key is claimed like an Integration API
-- batch (script 0026), under its own operation.
ALTER TABLE integration_idempotency DROP CONSTRAINT integration_idempotency_operation_check;
ALTER TABLE integration_idempotency ADD CONSTRAINT integration_idempotency_operation_check
    CHECK (operation IN ('flights.legs', 'flights.events', 'allocations', 'aodb.aidx'));
