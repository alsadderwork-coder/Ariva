# Ariva.Business.Contracts

Contracts for the feed from AMAN to Ariva. The package is published from the Ariva repository and referenced by AMAN.

Current version: `Aman/V1`, `ContractVersion.Current = "1.0"`.

## Rules

1. Aggregate only. Contracts carry counts, rates, timings and states per site, desk, gate, lane or flight. They never carry officer identifiers, passenger identities, document numbers, names or per person records.
2. Identities never cross. If a field could identify an officer or a passenger, it does not belong here. The data boundary test in `Ariva.UnitTests` fails the build if a property name looks like a person or officer identifier.
3. AMAN publishes, Ariva consumes. AMAN produces these messages from its outbox; Ariva never writes back to AMAN through these types.
4. Additive changes only within V1. New optional fields or new contracts may be added to `Aman/V1` with a minor bump of `ContractVersion.Current`. Never rename, remove or retype a field, and never renumber an enum value.
5. Breaking changes go to V2. Create `Aman/V2` alongside V1 and run both until AMAN has moved over.

## V1 contracts

- `DeskSessionChanged`: a border desk opened, closed or paused (desk open signal, no officer id).
- `DeskIntervalStats`: passengers processed and processing times per desk per interval.
- `EGateIntervalStats`: attempts, accepts, rejects by reason and cycle time per e-gate per interval.
- `InboundFlightLaneDemand`: expected passengers per lane for an inbound flight, from API data.
