# ADR-0001: Ariva is a separate product and codebase from AMAN

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 1 (restates a product owner decision)

## Context

The queue management system serves border authorities and airport operators, including airports where AMAN is not the immigration system (AUH is the likely first target and AMAN is not deployed there). AMAN releases go through each client's change control. A queue product inside AMAN would inherit that release cycle and could not be sold where AMAN is absent.

## Decision

Ariva is a separate product with its own repository, backlog and release cycle. Per site it is hosted in AMAN's Kubernetes cluster (own namespace and database) or in a separate cluster. It integrates with AMAN only through aggregate-only contracts ([ADR-0010](ADR-0010-aggregate-only-aman-contracts.md)).

## Consequences

- No direct code reuse from AMAN. Mitigated by mirroring AMAN's layout and conventions ([ADR-0016](ADR-0016-repository-mirrors-aman.md)) and by shared packages for common infrastructure.
- AMAN-side changes (outbox events for the feed contracts) are separate work items in AMAN's backlog and go through the client's change control; they sit on the pilot's critical path (D6).
- Ariva can be sold to airport operators with no AMAN footprint.

## Alternatives considered

- A module inside AMAN. Rejected: ties Ariva to AMAN's release cadence and client change control, keeps officer-level data one query away, and is not sellable where AMAN is absent.
