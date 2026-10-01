# Release notes and versioning

How Ariva versions the product, its charts, images, contracts, APIs, topics and schema, and the release history. Rules marked Proposed are to be confirmed by the product owner.

## 1. What carries a version

| Thing | Scheme | Where it is set | Status |
|---|---|---|---|
| Product | Semantic versioning `MAJOR.MINOR.PATCH`; 0.x before the first production release | Chart `appVersion`, release notes, a repository tag `v<version>` | Proposed (chart `appVersion` is `0.1.0` today) |
| Helm chart `ariva-platform` | Semantic versioning; bump on every chart change | `Charts/platform/Chart.yaml` `version` (0.1.0) | Decided (Helm convention noted in the chart) |
| Container images | GitHub Actions: `<tag or branch>-<run number>` pushed to `ghcr.io/alsadderwork-coder/ariva-*` on `v*` tags (`images.yml`). Azure DevOps (customer CD to Dalil Container Registry): `$(Build.SourceBranchName)-yyyyMMddrr` | Build workflows and pipelines | Decided |
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
- Production deployments pin images by build number; the release notes list the build number of every image in the release.

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
