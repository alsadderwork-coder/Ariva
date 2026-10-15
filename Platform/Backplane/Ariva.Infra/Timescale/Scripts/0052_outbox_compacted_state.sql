-- 0052 The outbox keeps the state of the compacted topics (ARV-126, wiki 04 section 10).
--
-- The relay's housekeeping deletes sent rows after Kafka:Outbox:KeepSentDays, except the newest row of each key on a
-- compacted topic (TopicCatalog.CompactedTopics: device registry, zone health, zone profile, desk, nowcast and desk state).
-- So the database always holds the state those topics carry, and after a Kafka loss scripts/ops/ariva-db.sh after-kafka-loss
-- marks those rows unsent again for the relay to send. This index finds the newer row of the same key that lets an older
-- one go. Rows deleted before this script are gone: a key unchanged for longer than the keep time before the upgrade is
-- sent again only after its next change.
CREATE INDEX ix_outbox_message_key_seq ON outbox_message (topic, message_key, seq);
