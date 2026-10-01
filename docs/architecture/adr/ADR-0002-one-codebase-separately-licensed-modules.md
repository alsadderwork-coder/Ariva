# ADR-0002: One codebase with separately licensed, separately deployable modules

- Status: Accepted
- Date: 2026-09-28
- Source: D5 key decision 2; product decision 1 (modules sold separately)

## Context

Two buyers with different needs: border authorities (immigration halls, e-gates, AMAN ground truth) and airport operators (check-in, security, handler SLAs and penalties). Both share the same measurement, queue engine and alerting core.

## Decision

One Ariva codebase. The Border module and the Airport Operations module are enabled per deployment by a signed, offline-verifiable licence file, so each can be sold and installed on its own and air-gapped sites still validate. The Border Integration context exists only in border deployments.

## Consequences

- One test suite and one release train for both modules.
- No independent release cadence per module.
- Licence checks are part of the code and of the install bundle.

## Alternatives considered

- Separate codebases for the Border and Airport Operations modules. Rejected: duplicated core, two products for a team of two.
