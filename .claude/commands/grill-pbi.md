---
description: Hunt for ambiguity, missing acceptance criteria and risks in a story or PBI before refinement
argument-hint: <story id or pasted PBI text>
---
Grill $ARGUMENTS before it is built. Be direct; point at holes, do not praise.

Check and report, numbered:
1. Ambiguous terms (compare with docs/domain/glossary.md) and conflated concepts.
2. Missing or untestable acceptance criteria; propose concrete ones (numbers, states, roles).
3. Domain rule gaps against docs/domain/formulas.md (edge cases: zero throughput, missing exits, late events, clock drift, degraded sensors, provisional versus final).
4. Authorization: which roles and permissions, site scoping, data boundary exposure.
5. Security: which of the 14 CWEs it touches and whether the criteria cover them.
6. Integration and contract impact (AMAN contracts are additive only within V1).
7. Size: does it fit one agent session; if not, propose the split.
8. Open questions for the product owner, phrased so they can be answered in one line each.
If the story is in a PRD file, propose the improved acceptance criteria as a diff but do not apply it unless asked.
