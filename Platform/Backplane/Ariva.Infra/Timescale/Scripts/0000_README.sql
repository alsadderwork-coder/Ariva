/*
  Versioned TimescaleDB scripts live in this folder, named NNNN_description.sql
  (for example 0001_create_queue_observations.sql). The script runner applies them
  in ascending numeric order, once each, and records which scripts it has applied.

  These scripts are the only way the production schema for time-series tables
  (hypertables, continuous aggregates, compression and retention policies) changes.
  Never edit a script that has shipped; add a new script with the next number instead.
*/
