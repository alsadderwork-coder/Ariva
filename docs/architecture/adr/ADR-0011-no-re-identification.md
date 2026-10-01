# ADR-0011: Continuity by overlapping coverage; no re-identification

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 11; D4 tracking

## Context

Wait time needs a person followed from entry line to exit line. Following people across gaps would need re-identification, which produces pseudonymous identifiers and drifts toward biometric data.

## Decision

Inside one process (a snake and the desks it feeds) coverage has no gaps and tracks are handed between sensors by position, time and velocity (at least 0.3 m overlap). Between processes, flows are linked only statistically (time shift between aggregate count curves) or not at all (default). Track ids are rotated at zone exit and never persist past the operating day.

## Consequences

- No per-person journeys across processes (for example curb to gate).
- Coverage must be continuous, which drives sensor counts and the site survey.
- Staff crossing the snake cannot be recognised; they are excluded by zone rules and the residual bias is measured in validation.

## Alternatives considered

- Appearance re-identification (height, shape, gait). Rejected: pseudonymous identifiers, drifts toward biometric data.
- Device re-identification (Wi-Fi or BLE). Rejected: inaccurate under MAC randomisation and privacy-negative.
