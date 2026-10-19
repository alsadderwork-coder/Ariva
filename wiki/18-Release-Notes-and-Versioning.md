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

### ARV-091a: production log shipping over TLS to a collector (2026-10-19)

Changed in the hosts (every Backplane host; [log inventory](../docs/security/log-inventory.md) sections 2.1 and 2.2):
- In k8s-prd a host refuses to start unless it ships its logs over TLS, besides the console, to Loki (`Loki:Enabled` true and an https `Loki:Uri`) or to the OTLP collector (`OTEL_EXPORTER_OTLP_ENDPOINT`, or `Otlp:Enabled` true with `Otlp:Endpoint`, at an https address). `Otlp:Endpoint` alone and `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` do not count: neither turns the log sink on.
- In k8s-prd a host refuses to start without `Logging:PseudonymKey`; elsewhere it stays optional. From this release a production deployment's pods all hash desk codes and track ids with the deployment's key, so a day's pseudonyms agree across pods and restarts.
- New optional Loki sender credentials, `Loki:Username` and `Loki:Password`, sent with every batch as HTTP basic authentication: both or neither, no colon in the user name, from the environment only outside vm-local, and only with an https `Loki:Uri`.
- The OTLP headers, a collector's sender credential (`OTEL_EXPORTER_OTLP_HEADERS` and the per-signal `OTEL_EXPORTER_OTLP_TRACES_HEADERS`, `_METRICS_HEADERS` and `_LOGS_HEADERS`): in every environment but vm-local, while the OTLP export is on, a host and the simulator refuse to start when one comes from a configuration file, or when Ariva's endpoint or `OTEL_EXPORTER_OTLP_ENDPOINT` is not https. In k8s-dev and k8s-demo the simulator stops too when given headers with an http endpoint. vm-local is exempt (the Aspire AppHost sends its dashboard's key that way). The OTLP log sink now reads the standard OTLP variables from the host's configuration, so it ships to the endpoint the guards judged.
- Fixed: with `otel.protocol` `http/protobuf`, traces, metrics and the simulator's OTLP logs were posted to the endpoint's own path, which a collector does not serve; only the hosts' Serilog log sink used `/v1/logs`. Each signal now goes to its own path after the endpoint (`/v1/traces`, `/v1/metrics`, `/v1/logs`), keeping any path prefix (`https://collector/otlp` gives `https://collector/otlp/v1/traces`). A site on `http/protobuf` was not receiving traces and metrics before this release and now does; nothing to change on the site. gRPC, the chart's default, is unchanged.
- The one-shot commands (`--migrate`, `--provision-topics`, the break-glass commands, `--check-stored-names`, Stream's `--replay` and `--verify-replay`) need neither a collector nor the key.

Changed in the chart and the Helmfiles (wiki 04 section 5):
- A new `logging` block: `collector` (`otlp` or `loki`), `pseudonymKeySecret` (`name`, `key`), `lokiCredentialsSecret` (`name`, `usernameKey`, `passwordKey`) and `required`, nothing else. `values-k8s-prd.yaml` ships `collector: loki`, `pseudonymKeySecret` `ariva-log-pseudonym-key` with the key `pseudonym-key`, and `networkPolicies.loki` with the documentation network `192.0.2.0/24` on 3100, which holds no Loki.
- The chart requires a collector and the key's Secret in k8s-prd and wherever `logging.required` is true; the prd environment of both Helmfiles sets it (state value `logShipping`, refused false there). `otlp` needs an https `otel.endpoint`; `loki` gives the five API hosts `Loki__Enabled` "true" and, with the NetworkPolicies on, needs `networkPolicies.loki` peers.
- `Logging__PseudonymKey`, `Loki__Username` and `Loki__Password` reach the five API hosts only, as secretKeyRefs that are not optional; the Jobs, the web pod and the simulator never get them. No value can hold the key or a credential itself.
- At install and upgrade, when the release can see the cluster, the chart reads the key's Secret with Helm's `lookup` (and the namespace's `kube-root-ca.crt` ConfigMap, to know it can see the cluster) and refuses a Secret that is missing, lacks the key or holds a key the hosts would refuse.
- `otel.headersSecret` (`name`, `key`, default `headers`): the Secret of `OTEL_EXPORTER_OTLP_HEADERS`, for the five API hosts and the simulator, with an https `otel.endpoint` required in every environment. `otel` now takes only `endpoint`, `protocol` and `headersSecret`.

Not changed: dev, demo and localk8s render as before unless their values set the new keys (only the `otel` key rule applies everywhere; the hosts' OTLP header rules apply outside vm-local). No database script, API endpoint or permission changes.

Upgrade steps (k8s-prd; dev and demo need only step 4):

1. Before the upgrade, create the log pseudonym key's Secret `ariva-log-pseudonym-key` in the release's namespace (wiki 04 section 6.2: from a file of random bytes, so the key stays out of the shell's history). Without it the release fails to render where it can see the cluster, and otherwise the API pods wait in `CreateContainerConfigError`.
2. Choose a collector and fill its settings and peers (wiki 04 section 6.1, step 6). Loki, as shipped: Loki's https address in `Loki:Uri` of `base-appsettings-secret`, and Loki's own peers and port in `networkPolicies.loki` in place of `192.0.2.0/24`. The OTLP collector instead: `logging.collector: otlp`, an https `otel.endpoint` and its `networkPolicies.otlp` peers. Where the store authenticates senders, create the credentials' Secret first, then name it (`logging.lokiCredentialsSecret.name` or `otel.headersSecret.name`); move any `Loki:Username`, `Loki:Password`, `OTEL_EXPORTER_OTLP_HEADERS` or `Logging:PseudonymKey` out of appsettings files and secrets into those Secrets.
3. The hosts refuse to start otherwise: with `loki` and no https `Loki:Uri`, every new API pod stops with "Loki:Uri must be https in k8s-prd" and the rollout stalls (wiki 10 section 4.17 lists each refusal and its fix). The migration and topics Jobs are not affected.
4. Remove from the site values file any key under `otel` but `endpoint`, `protocol` and `headersSecret`: the chart refuses it in every environment. In every environment but vm-local, move any OTLP header variable (`OTEL_EXPORTER_OTLP_HEADERS` or a per-signal one) out of appsettings files and secrets into the Secret of `otel.headersSecret` (create it first, then set the name), with an https `otel.endpoint`: the hosts and the simulator refuse them otherwise.
5. Give the deployer's credentials `get` on ConfigMaps and Secrets in the namespace (Secrets already for `ariva-cluster-ca`, ARV-082b): the chart's lookup guard reads them.
6. After the upgrade, find each API host's startup line in the store (wiki 04 section 8, check 10): with the shipped `192.0.2.0/24` left in `networkPolicies.loki` and a real `Loki:Uri`, the hosts start and their logs never leave the pods. Set up the store-side alert for when a host's lines stop arriving ([log inventory](../docs/security/log-inventory.md) section 8.1). Complete the go-live record of the log stores ([13 Security Guide](13-Security-Guide.md), checklist item 15) and keep the proof with the release evidence. ASVS V16.4.2 (logs protected from unauthorised access and change) is Met for a site only once that record is complete: the store's readers, access control, protection against change and retention are the site's.

Rollback notes: rolling the chart back removes the requirement and the variables (there is no script to undo): the hosts go back to a key per pod, and ship to Loki only where their appsettings turn it on. The Secrets can stay.

### ARV-091: security event logging (2026-10-19)

Changed in the hosts (every Backplane host):
- New security events at Warning, with stable EventIds: 9114 `SecurityEvent.AuthorizationForbidden` (403), 9115 `AuthorizationChallenged` (401), 9116 `RateLimitRejected` (429), 9117 `InvalidModelRefused`, 9118 `StrictJsonRefused`, 9119 `RepeatedQueryRefused`, 9127 `NulCharacterRefused` (400s), 9128 `SiteScopeRefused` (the `[SiteScoped]` 404), and 9129 `RequestRefused`: any other 401, 403 or 429, answered outside the authorization middleware and the rate limiter, logged when the request ends with its status and a fixed reason (an ended session, a pending account, a service's 403, an integration client's own limit, the upload limit, the sign-in's and the refresh's refusals). Each names who, from where, the method, the endpoint's route pattern, a fixed reason and the trace id, never the refused input. A throttle writes at most 60 of a kind a second per pod, 3 from one source (the caller, else the client address, IPv6 by its /64); the events it leaves out are written as `SecurityEvent.EventsSuppressed` (9130), one line per kind every 10 seconds, and counted by the new metric `ariva.security.events` (meter `Ariva.Security`). The catalogue, the earlier events 9101 to 9113 and 9120 to 9126 included, is the [log inventory](../docs/security/log-inventory.md), section 10.
- Every line a request writes now carries `CallerAddress` and the caller by id (`CallerUserId` with `CallerSessionId`, or `CallerClientId`, `CallerDeviceId`, `CallerDisplayId`; `anonymous` before the authorization decision), beside `TraceId` and `SpanId`. A line about another account keeps that account in its own `UserId`, apart from the `Caller` fields. Live hub join refusals carry them too.
- Values are protected by level in every log line: staff email addresses are masked (`a***@example.com`), also inside message text; track ids and desk codes are hashed (`#` and 12 hex characters, under a key per UTC day derived from the new optional secret `Logging__PseudonymKey`, so a pseudonym never links lines across days); dictionary keys take their dictionary's level (keys that protect alike become `<protected key>~<n>`); credentials stay omitted. The redaction enricher fails closed: a value it cannot protect is written as `[REDACTED]` and counted by the new metric `ariva.security.log_protection_failures`. The request-level security events carry no `RequestPath`.
- Only `Serilog:MinimumLevel` is read from configuration: a host with any other key under `Serilog` (`WriteTo`, `AuditTo`, `Using`, `Enrich`, `Filter`, `Destructure`, `LevelSwitches`, `Properties`, `MinimumLevel:ControlledBy`) does not start, in any environment. The security events' category stays at Warning whatever the levels say.
- The JSON file sink (`LogFile:Path`) is refused outside vm-local: a host or the simulator with it set in k8s-dev, k8s-demo or k8s-prd does not start.

Changed at the ingress (the chart and Ariva's controller values):
- No Ariva Ingress writes a ModSecurity audit log (`SecAuditEngine Off` on each). On ingress-nginx 1.9.6 to 1.15 these Ingresses wrote none before either; the setting keeps it so whatever the controller's file says.
- The controller's access log format (`log-format-upstream`) writes the request line without its query string. The controller values also require, and the chart test checks: `error-log-level` notice or above (never `info` or `debug`), `log-format-escape-none` not true, `enable-opentelemetry` off for the whole controller, and no log or ModSecurity directive in the controller's snippets. The OWASP Core Rule Set may be on; it keeps the audit log off.
- The live hub's Ingress exports no OpenTelemetry spans (`enable-opentelemetry: "false"`, set by the chart).
- Every host without the live hub's Ingress, api-main's own host included, answers `/hubs` and anything under it 404; the web host serves the lowercase `/hubs` only.
- The chart refuses more site annotations: a `modsecurity-snippet` with any `SecAuditLog...` or `SecDebugLog...` setting, `ctl:auditEngine`, `ctl:auditLogParts`, `Include` or `SecRemoteRules`, or with a macro that copies request values into a rule's message (`%{REQUEST_HEADERS...}`, `ARGS`, `MATCHED_VAR`, `TX.0` to `TX.9` and the like; `%{MATCHED_VAR_NAME}` is allowed); any annotation value that writes a `modsecurity...`, `access_log` or `error_log` directive, quoted or not; `enable-opentelemetry` on the live hub's Ingress; a `tokenInQuery` that is not true or false or that routes anything but `/hubs`; and `/hubs` on an Ingress without `tokenInQuery`.

Not changed: the sign-in, logout and refresh events of their own (ARV-091b, after the owner's login freeze), production log shipping by default and the chart's Secret for the pseudonym key (ARV-091a, above). No database script, API endpoint or permission changes; the api-main host no longer serves `/hubs`.

Upgrade steps:

1. Before the upgrade, re-apply Ariva's controller values (`Platform/Cloud/Ariva.K8s/Helm/ingress-nginx-values.yaml`) to the site's ingress-nginx: `enable-modsecurity` stays "false" for the whole controller (on, the controller's `modsecurity.conf` would turn the audit log back on for every Ariva Ingress, with bearer tokens and cookies in it), and the new `log-format-upstream`. Check the controller's own settings against the values' list of defaults (`error-log-level`, `log-format-escape-none`, `enable-opentelemetry`, the four controller snippets) with the read-only commands of the log inventory, section 5. Until then the controller writes query strings in its access log.
2. Remove `LogFile:Path` (or `LogFile__Path`) from every settings file and variable outside a developer machine, and every key under `Serilog` but `MinimumLevel` from every appsettings file, secret and site overlay of every environment, vm-local included: a site overlay that still sets one stops the hosts at start.
3. Do not put a pseudonym key in an appsettings file or secret, a ConfigMap or the chart's values: outside vm-local a host refuses `Logging:PseudonymKey` from a configuration file. The key is set only as the environment variable `Logging__PseudonymKey` from a Kubernetes Secret, which the chart passes since ARV-091a and requires in k8s-prd: create that Secret as ARV-091a's step 1 says (above).
4. Remove from the site's values files any annotation the chart now refuses (above); the release fails to render otherwise.
5. Update dashboards, alerts and SIEM rules that matched log lines on a whole staff email address, a desk code (for example Integration's "Border feed code ... has no AMAN desk code mapping" now shows `#` and 12 hex characters, different each UTC day; the codes to map are in the border feed tables, wiki 10 section 4.16), `RequestPath` on a refusal, or a query string in the controller's access log. Match the masked or hashed forms, the security events' EventIds and fields, or the trace id instead. Add the new metrics `ariva.security.events` and `ariva.security.log_protection_failures` (wiki 10 section 2a) where the site watches floods and faults. Expect routine `SecurityEvent.RequestRefused` (9129) lines from the sign-in's `mfa_required` 401 and from the refresh's 401 when a page loads after its session has ended.
6. Point any client that opened the live hub through the api-main host at the web host (`https://<web host>/hubs/live`); the web app already does.
7. After the upgrade, confirm the controller with the read-only commands of the log inventory, section 5, and find one refused request by its trace id (wiki 10 section 4.16). Keep both outputs with the release evidence.

Rollback notes: rolling the chart back restores the earlier hosts and Ingresses (there is no script to undo); a host rolled back writes email addresses and desk codes whole again, and reads the whole `Serilog` section again. The controller values are the site's own release: keep `enable-modsecurity` "false" and the new format even after a rollback, since both only take away what is logged.

### ARV-089: strict input parsing (2026-10-19)

Changed: Ariva.Api.Main, Ariva.Api.Ingest and Ariva.Api.Integration read request JSON strictly and refuse a repeated query parameter. A client that sent a member the request does not define, a member twice (also when the two differ only in case), a number as a string (`"5"` for `5`) or the same query parameter more than once now gets 400, where the member used to be ignored, one of the values taken or the string read as a number. A body refusal is the usual problem with `errors.body` "The input was not valid."; a repeated query parameter answers the detail "A query parameter appears more than once."; neither repeats what was sent. Member names still match in any case, answers are unchanged, and authentication and permissions are checked first (401 and 403 as before). Not changed: the sign-in and the other `api/auth` endpoints of Ariva.Api.Main and the integration token exchange keep their earlier parsing until ARV-089a (the owner's login freeze); file uploads, the live hub, the health probes and the simulator. Integration batches, sensor pushes and site configuration bundles were already strict. Ariva.Web now compiles with strict TypeScript and the security gate refuses prototype pollution in it (SEC-131, SEC-132); nothing changes on screen. No database script, endpoint or permission changes.

Upgrade steps:

1. Before the upgrade, check every script or tool that calls the APIs directly (operator scripts, monitoring checks, reporting tools, sensor gateways, integrations): it sends only the members the endpoint documents, each once, numbers as JSON numbers, and each query parameter once.
2. After the upgrade, a 400 with one of the two texts above points to such a call: fix the caller.

Rollback notes: rolling the chart back alone restores the earlier parsing (there is no script to undo).

### ARV-085: network policies, trusted proxies and private probes (2026-10-19)

Changed: in k8s-prd every Ariva pod is isolated by NetworkPolicies, a default deny and exactly the firewall matrix's flows (wiki 05 section Network policies), on in `values-k8s-prd.yaml` and refused off there by the chart and by the prd environment of both Helmfiles (state value `networkPolicies`); the API hosts trust `X-Forwarded-For` only from the ingress controller's pod network, the new Helm value `ingressController.podNetworks` (the private ranges are gone from `appsettings.base.k8s-prd.json`), and a k8s-prd host refuses to start without a narrow network there; every Ingress, in every environment, answers `/health`, `/healthz` and anything under them 404; the Cronz Ingress needs `sourceRanges` in k8s-prd. Dev, demo and localk8s render no NetworkPolicy and keep their trusted ranges. No database script, endpoint or permission changes.

Upgrade steps (k8s-prd; dev and demo need only steps 4 and 5):

1. Before the upgrade, confirm that the cluster's CNI enforces NetworkPolicy and that the ingress controller sees each client's own address (`externalTrafficPolicy: Local` on its Service, or the PROXY protocol) (wiki 04 section 2).
2. Before the policies turn on with this release, fill in the site values file (wiki 04 section 6.1, step 5): `ingressController.podNetworks` (the cluster's pod CIDR in place of the shipped `10.244.0.0/16`: IPv4 /14 or narrower, IPv6 /48 or narrower and no prefix that trusts IPv4-mapped clients, which the chart refuses and the hosts too: Cilium's default IPv6 pool `fd00::/104` among them, so give `fd00::/96` or the controller's IPv4 pod network there), the `ingressController` selectors when the controller is not the ingress-nginx chart in the namespace `ingress-nginx`, the Cronz Ingress's `sourceRanges` (the management network in place of `192.0.2.0/24`), and the `networkPolicies` peers: kube-dns, the database, Kafka and Redis checked against the cluster, and the OTLP collector, Loki, the SMTP relay, every outbound endpoint and, with MQTT, the sensor networks given. A flow without peers stays closed.
3. A site whose `base-appsettings-secret` was written from the earlier committed k8s-prd file still lists `10.0.0.0/8`, `172.16.0.0/12` and `192.168.0.0/16` under `Security:ForwardedHeaders:KnownNetworks`: remove them. An environment variable replaces only its own index of that list, so the ranges after the chart's entries would remain, and the hosts refuse to start with them.
4. In the site values file, move any `nginx.ingress.kubernetes.io/whitelist-source-range` or `allowlist-source-range` annotation into the entry's `sourceRanges`, and remove `nginx.ingress.kubernetes.io/rewrite-target` and `use-regex`, any snippet that writes a `rewrite` directive and any `paths` entry under `/health` or `/healthz`: the chart now refuses them all, in every environment. The shipped values no longer set `rewrite-target: /` on the `/` Ingresses (ingress-nginx 1.15 never applied it as a rewrite there), so the paths reach the pods as before.
5. Point external monitors that probed `/health` or `/healthz` through the ingress elsewhere (they get 404 from now on): the hosts' `ariva.health.status` metric (wiki 10 section 2a), or a port-forward (wiki 04 section 8, check 2).
6. Preview with `helmfile diff`. On this first upgrade the migration and topics jobs (pre-upgrade hooks) run before any policy exists; from the next upgrade on they run under the previous release's policies, so release a moved database's or brokers' peers before the release that points the hosts at them.
7. After the upgrade: `kubectl -n "$NAMESPACE" get networkpolicy` (read only) lists `ariva-default-deny` and the flows' policies (wiki 04 section 8, check 9), the TLS check passes on every host (it now also fails a host that routes `/health/readiness`), and keep both outputs with the release evidence.

The release smoke suite (ARV-128) no longer reads readiness through the ingress: against a deployed site it requires each probe path to answer the ingress's 404 (or 403) with its HSTS, nosniff and frame-ancestors headers, then proves each host is up with an anonymous read the host itself refuses (its 401 problem with its own headers). Readiness, the database included, is read from inside the cluster (wiki 04 section 8, check 2).

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
