# Release notes and versioning

How Ariva versions the product, its charts, images, contracts, APIs, topics and schema, and the release history. Rules marked Proposed are to be confirmed by the product owner.

## 1. What carries a version

| Thing | Scheme | Where it is set | Status |
|---|---|---|---|
| Product | Semantic versioning `MAJOR.MINOR.PATCH`; 0.x before the first production release | Chart `appVersion`, release notes, a repository tag `v<version>` | Proposed (chart `appVersion` is `0.1.0` today) |
| Helm chart `ariva-platform` | Semantic versioning; bump on every chart change | `Charts/platform/Chart.yaml` `version` (0.1.0) | Decided (Helm convention noted in the chart) |
| Container images | GitHub Actions: `<tag or branch>-<run number>` pushed to `ghcr.io/alsadderwork-coder/ariva/<service>` on `v*` tags, or by hand from `main` (`images.yml`), each signed with cosign keyless and carrying a signed CycloneDX SBOM; the release notes give each image's digest (ARV-073). Azure DevOps (customer CD to Dalil Container Registry): `$(Build.SourceBranchName)-yyyyMMddrr` | Build workflows and pipelines | Decided |
| Offline install bundle | `ariva-offline-<version>`: the images of one build by digest with their signatures and SBOMs, the packaged charts, the Helmfile and the web build, with a signed checksum manifest naming the version, build and commit (ARV-127) | `offline-bundle` workflow, run by hand from the release tag (or `main` at the images' commit) with the build's image tag and the release version; verified on a connected machine with `scripts/offline/verify.mjs --version <version>`; kept as a workflow artifact for 90 days and archived by Dalil (wiki 04, section 12) | Decided |
| Release identity in a cluster | `releaseVersion`, written to `RELEASE_VERSION`, labels and telemetry | Release pipeline (`--set releaseVersion=$(Build.BuildNumber)`) | Decided |
| AMAN feed contracts | `Aman/V1` namespace; `ContractVersion.Current` (`1.0`) | `Ariva.Business.Contracts` | Decided |
| Integration API | URL path version `/api/v1` | Integration host | Decided path; change rules Proposed |
| Kafka topics | Suffix `.v1` in `ariva.<context>.<event>.v1` | Topic constants | Decided; migration rule Proposed |
| TimescaleDB schema | Numbered scripts `NNNN_*.sql`, recorded in `schema_version` with checksums | `Ariva.Infra/Timescale/Scripts` | Decided |
| Zone profiles, contracts | Data versions: immutable, monotonically increasing per site or contract | In the database | Decided |
| Forecast models (v1) | Model version recorded with every forecast run | Forecasting worker | Decided |

Proposed product rules:

- MAJOR: a release that needs a manual migration beyond the documented upgrade steps, or that removes a supported API version.
- MINOR: new features, new endpoints, new topics, new optional fields, new sensor families.
- PATCH: fixes and security updates with no configuration change.
- 1.0.0 marks the first production release (the pilot go-live). Until then, 0.x releases may change anything, with notes.
- Production deployments pin images by build number; the release notes list the build number and the digest of every image in the release (ARV-073: the `images` workflow's `image-digests` table), and deployments verify the signature of that digest (wiki 04, section 6.5). For a release that goes to an air-gapped site, the notes also give the offline bundle's manifest SHA-256 from the `offline-bundle` run summary, which the site compares at verification (wiki 04, section 12.3).

## 2. AMAN contract versioning (the V1 additive rule)

The contracts in `Platform/Business/Ariva.Business.Contracts` (namespace `Ariva.Business.Contracts.Aman.V1`) are shipped to AMAN as a package. AMAN publishes; Ariva consumes.

| Change | Allowed in V1? | Version effect |
|---|---|---|
| New optional field on an existing contract | Yes | Minor bump of `ContractVersion.Current` (for example `1.0` to `1.1`) |
| New contract | Yes | Minor bump |
| New enum value added at the end (for example a new `EGateRejectCategory`) | Yes | Minor bump |
| Rename, remove or retype a field | No | Create `Aman/V2` |
| Renumber an enum value | No | Never (values are fixed; zero is unused in `DeskSessionState` so a missing value is detectable) |
| Change the meaning of a field (for example interval length other than 60 seconds) | No | `Aman/V2` |

Breaking changes go to `Aman/V2`, created alongside V1; both run in parallel until AMAN has moved over. The data-boundary test in `Ariva.UnitTests` fails the build if any contract property looks like a person or officer identifier, in any version. AMAN-side changes go through each client's change control, so plan them early.

## 3. Integration API, topics and schema (Proposed rules)

- **Integration API**: additive changes (new endpoints, new optional fields) stay in `/api/v1`. A breaking change gets `/api/v2`, served in parallel with `/api/v1` for an agreed deprecation period. Note that v1 rejects unknown fields, so a client must not send a new field until the server version that accepts it is deployed.
- **Kafka topics**: a breaking schema change gets a new version suffix; old and new topics are published in parallel during migration (ADR-0019).
- **Database**: scripts are forward-only and never edited after shipping. Schema changes are additive in one release and destructive only in a later release, so that the previous application version still runs during a rollback window.

## 4. Release note template

Copy this block for each release, newest first.

```
## <version>, <yyyy-mm-dd>

Summary: one or two sentences.

Images (build numbers): api-main <n>, api-ingest <n>, api-stream <n>, api-cronz <n>,
api-integration <n>, web <n>, simulation <n>
Image digests: the table of the release's images workflow run (job summary, artifact
image-digests), pasted as is; every image is signed and carries its SBOM (wiki 04, 6.5)
Chart: ariva-platform <chart version>

Modules affected: Core | Border | Airport Operations

New:
- ...

Changed:
- ...

Fixed:
- ...

Security:
- Advisories addressed, dependency updates, new controls (CWE ids)

Database scripts:
- NNNN_<name>.sql (additive | destructive), runs in a transaction: yes | no

Kafka topics:
- New or changed topics, retention, partitions

Configuration:
- New or changed appsettings keys and Helm values, with defaults

Contracts and APIs:
- ContractVersion.Current: <x.y>; Integration API changes

Sensors:
- Adapter changes; certified firmware ranges

Upgrade steps:
1. ...

Rollback notes:
- Can this release be rolled back with helm rollback alone? yes | no, and why

Known issues and To confirm items:
- ...
```

## 5. Release history

### 0.1.0 skeleton, 2026-10-01

Summary: the repository skeleton for Phase 0. Nothing measures queues yet; every host starts and answers health probes, and the build, security and deployment tooling is in place.

Images: built per service by the `Build-k8s-<service>` pipelines on trunk. Chart: `ariva-platform` 0.1.0 (`appVersion` 0.1.0).

Modules affected: Core (no module-specific code yet).

New:

- .NET 10 solution `Ariva.slnx` mirroring AMAN's layout: Ariva.Utilities, Ariva.Core, Ariva.Resources, Ariva.Infra, Ariva.Di, Ariva.Api.Common, hosts Ariva.Api.Main (51001), Ariva.Api.Ingest (51002), Ariva.Api.Stream (51003), Ariva.Api.Cronz (51004), Ariva.Api.Integration (51005), Ariva.Simulation.Api (51020), test projects Ariva.UnitTests and Ariva.IntegrationTests.
- Layered appsettings (`appsettings.base*.json` in Ariva.Api.Common, `appsettings.service*.json` per host) for `vm-local`, `k8s-dev`, `k8s-demo`, `k8s-prd`.
- Health endpoints `/health/startup`, `/health/readiness`, `/health/liveness` on every .NET host.
- Role codes: Border shift supervisor, Terminal duty manager, Handler station manager, System administrator.
- AMAN feed contracts V1 (`ContractVersion.Current` 1.0): `DeskSessionChanged`, `DeskIntervalStats`, `EGateIntervalStats` with `EGateRejectCategory`, `InboundFlightLaneDemand`.
- Ariva.Web: SvelteKit 2 and Svelte 5 skeleton with Tailwind 4, bits-ui, ECharts and svelte-i18n (Arabic and English files), static build served by nginx on port 3000 with `/healthz`.
- Simulation host settings: seed 9303, site `DMO`.
- Helm chart `ariva-platform`: Deployments, Services and HPAs for the five APIs, web and simulation; ingresses with host check and ModSecurity; registry pull secret; OpenTelemetry environment. Values for `k8s-dev`, `k8s-demo`, `k8s-prd` and local Kubernetes. TimescaleDB chart placeholder (not installed). `helmfile-k8s.yaml`.
- Source control and CI on GitHub (`alsadderwork-coder/Ariva`, private): `ci.yml` pull request gate (CWE gate, build, unit, web, Playwright), `security-scan.yml` (Trivy, Semgrep CE, CycloneDX SBOM), `codeql.yml` (with Advanced Security), `claude.yml` (security review on pull requests, @claude), `images.yml` (images, Trivy image scan, GHCR), Dependabot. Azure DevOps pipelines kept for Dalil Container Registry deployments: image build per service and fan-out (`Build-k8s-*.yaml`), release to `k8s-dev` (`Release-ariva-k8s-dev.yaml`) with Helm 3.19.0, Helmfile 1.1.7 and helm-diff.
- Security gates: .NET security analyzers as errors, unsafe code disabled, repository scanner (40 rules over 14 CWEs, self-tested), allowlist with pending-approval rule, `scripts/verify.mjs` with scopes from `quick` to `all`.
- Architecture tests: layering and the AMAN contract data-boundary test.
- Design documentation in `docs/` (architecture overview, ADR-0001 to ADR-0025, integration, sensor adapters, glossary, formulas, data boundary, roadmap, decisions, CWE controls) and this wiki.

Database scripts: none (`0000_README.sql` only describes the convention).

Kafka topics: none provisioned yet.

Security: one pending allowlist entry (`SEC-052`, anonymous health probes), awaiting human approval.

Known issues and To confirm items:

- Hosts do not yet resolve `${...}` placeholders or check dependencies in readiness.
- The Helmfile pins development values; production installs use Helm directly.
- The Ingress objects have no `tls` section; the chart sets no pod `securityContext`.
- The web image does not receive `VITE_ARIVA_*` URLs at build time.
- `Platform/Testing/Ariva.E2E` does not exist yet; `verify.mjs e2e` reports it missing.
- Integration tests are skipped until story ARV-007 adds Testcontainers.
- Open decisions are listed in `../docs/product/decisions.md`.

## 6. Migration notes for the next release

Changes since the last release that an operator must act on when upgrading, newest first. Each moves into the release's notes (section 4) when the release is cut.

### ARV-084: ingress transport and header hardening (2026-10-19)

Changed: every host at the ingress sends HSTS of a year with includeSubDomains over HTTPS, nosniff and `frame-ancestors 'none'` on every response, the ingress's own included; the machine hosts (api-main, api-ingest, api-integration) answer plain HTTP with 403 instead of a redirect, while the web host and the Cronz dashboard keep the redirect (`plainHttp` in each `ingresses` entry); the affinity cookies are Secure, SameSite=Strict, Path=/; Ingest's MQTT listener takes TLS 1.2 and 1.3 only, with ECDHE and AEAD suites on Linux; the web app's sidebar cookie is now `__Host-sidebar_state`, so the sidebar opens expanded once after the upgrade. No database script, endpoint or permission changes.

Upgrade steps:

1. Before the upgrade, apply Ariva's controller values (`Platform/Cloud/Ariva.K8s/Helm/ingress-nginx-values.yaml`) to the site's ingress-nginx (wiki 04 section 2): snippets allowed at the Critical risk level, TLS 1.2 and 1.3 with the Mozilla intermediate suites. Where other teams create Ingresses on that controller, give Ariva a controller of its own. TLS must end at the controller (a layer 4 load balancer in front at most).
2. Remove `nginx.ingress.kubernetes.io/from-to-www-redirect` from the site's values files: the chart now refuses it, with `custom-headers`, any annotation that names `Strict-Transport-Security`, `X-Content-Type-Options`, `Content-Security-Policy` or `more_clear_headers`, a site annotation that sets a key the chart sets (`ssl-redirect`, `force-ssl-redirect`, the `session-cookie-*` keys and the others in wiki 04 section 5), and a site `modsecurity-snippet` that sets `SecRuleEngine` or `SecAuditEngine`. A site that wants `www.<host>` adds it as an `ingresses` entry of its own, with a certificate that names it.
3. Check that every sensor, gateway and integration calls the machine hosts with `https://` URLs and supports TLS 1.2 with ECDHE and AES-GCM or ChaCha20-Poly1305, or TLS 1.3: after the upgrade an `http://` call answers 403, and a client that offers only TLS 1.0, 1.1 or CBC suites cannot connect (to the ingress or to MQTT).
4. After the upgrade, run the TLS check against every host (wiki 10 section 4.14) and keep its output with the release evidence.

Fixed: the ModSecurity upload rule's message quote broke every Ariva Ingress at a controller that applied it (ingress-nginx wraps the snippet in `modsecurity_rules '...'`).

### ARV-191: the arrival wave reaches arrivals queues only (2026-10-16)

Fixed: the predicted nowcast's arrival-wave projection linked a queue zone to a lane through the desks its service zones stand at, of any checkpoint. Border desks of every kind carry lane categories, so a departures queue standing at lane-coded emigration desks shared the lane with the arrivals queue (dividing the arrivals queue's share, so its breach was under-predicted) and was judged on the arrivals wave itself. A queue zone now takes a lane only through desks of an Immigration checkpoint on its own level (formulas F14, predicted breach). No database script, endpoint or permission changes, and no upgrade step.

Known effects:

- A predicted-nowcast rule on a departures queue (or on a queue whose service zones name emigration desks, or desks of another level) has no projection from this release on, so it raises nothing. An alert such a rule left open is not cleared by the evaluation (minutes without a value are skipped, formulas F20): a Border shift supervisor or the administrator resolves it by hand.
- An arrivals queue whose lane such a queue shared now takes the whole lane, so its projected arrivals are higher (twice its former share when one departures queue shared the lane), its predicted values are higher and its rules can raise earlier than before.
- Seeded sites: NBJ-BC1's departures hall queue and its overflow band no longer have a projection (their lane ALL carried no arrivals, so the projection was zero); DMO (no service zones) and AUH-TA (one immigration checkpoint) are unchanged.

### ARV-187: predicted-nowcast rules become border data (2026-10-16)

Changed (owner decision of 2026-10-16, docs/product/decisions.md): the predicted nowcast (`PredictedNowcast`, "Predicted nowcast", a breach projected 15 to 60 minutes ahead) steps a lane queue with its share of the arrival wave, which is border data. It is now a border metric like open desks below recommendation (`DesksBelowPlan`): its rules, backtests, previews, alerts, live notices, emails and reports are for holders of `ArrivalWaveLanes.View` only (the Border shift supervisor and the system administrator). A rule on it belongs to one of those roles (never to no owner, which means every role) and escalates to one of them or to no role; it keeps all four comparators, its lead time and its overflow bands. The terminal duty manager loses predicted-wait alerts and keeps live-wait (`Nowcast`) alerts; nothing airport-side is lost in practice, since only lane queues have a projection. No database script, endpoint or permission changes.

Upgrade steps:

1. Before the upgrade, list the predicted-nowcast rules that will no longer pass the border checks (a read only query, run with the operator's database access, wiki 04):

   ```sql
   SELECT site_code, code, name, owner_role, escalate_to_role, enabled
   FROM alert_rule
   WHERE deleted_on IS NULL AND metric = 'PredictedNowcast'
     AND (owner_role IS NULL OR owner_role NOT IN ('BorderShiftSupervisor', 'SystemAdministrator')
          OR (escalate_to_role IS NOT NULL AND escalate_to_role NOT IN ('BorderShiftSupervisor', 'SystemAdministrator')))
   ORDER BY site_code, code;
   ```

2. For each rule listed, decide with the site and act before the upgrade where you can: on the release still running, an administrator edits the rule and sets the Border shift supervisor or the administrator as owner and a border escalation role or none, or a Border shift supervisor or administrator deletes it (a second factor is asked). Doing this first closes the legacy-rule part of the rolling upgrade window below. After the upgrade the same repair is still possible: Edit opens such a rule on border roles; a supervisor cannot take a rule from the duty manager (the server answers that only an administrator can), and disabling it is refused until it has a border owner. Never change rows by hand in the database.
3. Upgrade every host in the same release (Ariva.Api.Main, Ariva.Api.Stream, Ariva.Api.Integration, Ariva.Api.Cronz and the rest). During a rolling upgrade each old host keeps the old rule until it is replaced: an old Ariva.Api.Main lets the terminal duty manager create, backtest and list predicted rules and alerts and relays live notices to it; an old Ariva.Api.Stream keeps evaluating rules owned by airport-side roles (re-raising alerts a new Stream withdraws) and queues their alert emails; an old Ariva.Api.Integration sends pending emails to airport-side people; an old Ariva.Api.Cronz renders scheduled report deliveries with predicted alerts for them. Keep the window short and run step 1's query again after the upgrade.
4. Until a listed rule is repaired or deleted: it is not evaluated (it raises nothing); its open alerts are resolved as withdrawn at the first evaluation tick; Ariva.Api.Stream logs one warning per rule ("not evaluated, it fails the rule checks of its border metric", wiki 10); the alert rules screen marks it "Not evaluated: fix its border settings"; and the site's configuration export answers 409 (an alert rule fails the checks an import applies).

Known effects:

- The terminal duty manager no longer finds its own predicted-nowcast rules (404) and no longer sees predicted alerts, historic ones included: they leave its alerts list, its daily report and CSV, scheduled report deliveries rendered after the upgrade and the weekly summary. Reports and emails already delivered cannot be recalled.
- Alert emails about predicted alerts still pending for airport-side people at the upgrade are not sent (recorded as held back).
- A site configuration bundle exported before the upgrade that holds a predicted-nowcast rule without a border owner no longer imports (the alert rules part is refused); repair the rule at the source and export again.

Rollback notes: rolling the chart back alone restores the previous behaviour (there is no script to undo), which re-opens the channel the change closes (the duty manager could again create and backtest predicted rules and receive their alerts); rules repaired or deleted in the meantime stay as they are.
