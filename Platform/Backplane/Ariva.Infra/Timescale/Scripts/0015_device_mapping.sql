-- 0015 Device mapping (ARV-024): the declarative mapping a device's payloads are read with, by name from the catalog
-- shipped with Ariva. Set exactly when the dialect is Declarative; the application checks the name exists.
ALTER TABLE device
    ADD COLUMN mapping_name  varchar(64) CHECK (mapping_name ~ '^[a-z0-9][a-z0-9-]{0,63}$');

ALTER TABLE device
    ADD CONSTRAINT device_mapping_for_declarative CHECK ((dialect = 'Declarative') = (mapping_name IS NOT NULL));
