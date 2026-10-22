# Remaining work

Snapshot of 2026-10-21, branch `story/mvp-phase1`. The backlog files in `backlog/prd-*.json` are the source of truth; this page groups what is open by what it waits for. Merging to `main` and the move to Azure DevOps are in [azure-devops-move.md](azure-devops-move.md).

## Where things stand

| Backlog | Done | Open |
|---|---|---|
| Phase 0 (`prd-phase0.json`) | 78 of 79 | ARV-002: its last criterion is a dev cluster release by a person, deferred by the owner to ARV-137 |
| Phase 1, the MVP (`prd-phase1.json`) | 70 of 86 | 16, almost all waiting for site answers or the owner (ARV-124 split in two on 2026-10-21; ARV-124a's run filed ARV-230 to ARV-232, ARV-232 done, and stays open until the others let it pass) |
| Phase 2, displays and advertising (`prd-phase2.json`) | 8 of 8 | none |
| ASVS gaps (`prd-asvs-gaps.json`) | 26 of 42 | 16 (ARV-096b split in four on 2026-10-21, all four parts done; ARV-227 done and ARV-236 filed by it) |
| Phase 3, deferred hardening (`prd-phase3.json`) | 1 of 49 | 48, built after the pilot go-live gate unless pulled forward (ARV-228 and ARV-229 added by ARV-096b, ARV-233 by ARV-124a and pulled before the pilot, done, ARV-234 by ARV-096d, ARV-235 by ARV-096e) |
| Azure DevOps move (`prd-azure-move.json`) | 0 of 9 | 9, new; see [azure-devops-move.md](azure-devops-move.md) |

ASVS 5.0 Level 2 (`docs/security/asvs-l2.md`): 157 of the 203 requirements that apply are met (77 percent), 43 partly, 3 gaps (V16.3.4 back to Partly: ARV-231).

## 1. Workable now, no decision needed

| Story | What it does | Notes |
|---|---|---|
| ARV-226 | No certificate downloads while a chain is built: Redis, and the collectors without a site CA | Found in ARV-200; Medium, so before the pilot |
| ARV-236 | Bump the third-party pins ARV-227's first weekly check found behind | Three Kafka patch releases, new digests under ZAP's stable tag and both Redis tags, and TimescaleDB to its newest line (`pg17-ts2.31` or later; the older line stops moving, and existing databases need the extension update); each bump moves every copy at once (wiki 04 section 6.8) and runs the tests that use the image; before the pilot |
| ARV-230 | Queue engine: track positions out of the checkpointed state, so Stream fits its pod at the pilot rate | Found by ARV-124a: Stream runs out of its pod's heap three minutes into the pilot hall; High, next |
| ARV-124a | Pilot-profile load test and the D5 burst | The harness, the run and the measurements are in (wiki 05); open until the pilot run passes after ARV-230 (ARV-232 and ARV-233 done: no push was reset in ARV-233's run) |
| ARV-231 | Queue engine refusals visible (buffer full, forced advances, late and invalid events) | Found by ARV-124a; Medium, before the pilot |
| ARV-124b | 24-hour soak and catch-up after a 30-minute stream outage | After ARV-230; needs a machine doing nothing else for a day, with disk for a day of Kafka and TimescaleDB growth; its numbers and ARV-124a's answer TC-33 (node sizes) |

## 2. Waiting for the owner to lift the sign-in freeze

The owner froze every change to sign-in, passwords, TOTP, sessions and authentication (2026-10). These stories start when the freeze lifts:

| Story | What it does |
|---|---|
| ARV-080a | Credential hashes carry their algorithm |
| ARV-086 | Session management completeness (an open tab idles out) |
| ARV-087 | Cronz job dashboard behind user sign-in and MFA |
| ARV-088 | Authentication secret hardening |
| ARV-089a | Strict input parsing on the authentication endpoints (with ARV-096c's form refusal and NUL check order there) |
| ARV-090 | Live hub without a token in the URL, and an Origin check |
| ARV-091b | Sign-in, logout and refresh security events |
| ARV-093a | Sign-out clears the browser state |
| ARV-093b | Display player key out of sessionStorage |
| ARV-095a | Brute force and lockout hardening (includes the self-unlock loop) |
| ARV-121 | First system administrator without a standing password (also waits for ARV-134a) |
| ARV-134a, ARV-134b | Sign-in and step-up through the site identity provider (also wait for TC-30 and TC-06) |

## 3. Waiting for an owner decision

| Story or item | The decision |
|---|---|
| ARV-083a | Per-host database logins: four open questions |
| ARV-083b | Per-host Kafka principals and Redis users: which CA signs the Kafka certificates |
| ARV-083c | The secret store (Azure Key Vault was postponed with the move to Azure) |
| ARV-096a | Approve the remediation time frames (Proposed: critical 7 days, high 30, medium 90, low by the next release) |
| ARV-135 | Licensing for the pilot: build the signed licence file, or defer to v1 (TC-42, TC-43) |
| ARV-146 | How the Insight report tool is integrated (where it lives, how it gets data and delivers) |
| ARV-213 | Handler organisations inside a site, or the site stays the tenant boundary |
| ARV-202, ARV-208 | Retention of retired advertisement files; sensing day keys at rest |
| Proposed values | The cryptoperiods (`docs/security/key-management.md` section 6), the identity proofing procedure (TC-05), the IPv6 rate limit by /64, the V16.2.5 residuals |

## 4. Waiting for the site or the client

| Story | Waits for |
|---|---|
| ARV-130 | TC-20 and TC-21: AMAN site codes and lane category codes |
| ARV-131 | The AMAN outbox change in the client's AMAN test environment; TC-22, TC-24, TC-25 |
| ARV-132 | The pilot sensor family and a real capture (TC-38 or TC-39, TC-71) |
| ARV-133 | The site network design, TC-37, TC-40 (follows ARV-132) |
| ARV-136 | TC-01, TC-02, TC-03, TC-08, TC-09, TC-44, TC-47 (the start-up check can be built earlier with placeholders) |

## 5. Done by people, at the end of the MVP

| Story | What |
|---|---|
| ARV-002, ARV-137 | The first dev cluster release and its smoke run, by the owner (agents never change clusters) |
| ARV-138 | The pilot go-live gate: site installation, smoke run on the pilot cluster and burn-in |
| Phase-end runs | The demo and the live validation rehearsal (each plays for over an hour), deferred by the owner to the phase end |

## 6. Phase 3 (after the pilot)

49 stories in `backlog/prd-phase3.json`: low-severity findings of the security reviews, test determinism and harness work, and small product refinements. Examples: ARV-175 (dead letters), ARV-154 (site import lock), ARV-163 (linear regular expressions), ARV-182 and ARV-183 (scheduled backups, restore hardening), ARV-209 (secrets in the release tooling, which matters for the Azure release), ARV-214 (supply chain residuals of CI and test tooling), ARV-224 (the test report secret check's remaining gaps), ARV-225 (the Trojan Source check's remaining gaps and two harness hooks for the owner). The owner can pull any of them forward.
