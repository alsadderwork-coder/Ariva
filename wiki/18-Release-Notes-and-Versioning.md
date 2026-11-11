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

### ARV-288a: the ingress refuses external authentication, custom error pages and satisfy any (2026-11-11)

- Behaviour: the chart refuses to render, in every environment, an Ingress whose site annotations set an ingress-nginx external authentication key: every `nginx.ingress.kubernetes.io/auth-` key, the prefix and the name in any case (`auth-url`, `auth-signin`, `auth-signin-redirect-param`, `auth-method`, `auth-proxy-set-headers`, `auth-request-redirect`, `auth-response-headers`, `auth-always-set-cookie`, `auth-snippet`, `auth-keepalive`, `auth-keepalive-share-vars`, `auth-keepalive-requests`, `auth-keepalive-timeout`, `auth-cache-key`, `auth-cache-duration` and any other), whatever its value, but, as whole names, the five client certificate keys Ariva documents (`auth-tls-secret`, `auth-tls-verify-client`, `auth-tls-verify-depth`, `auth-tls-pass-certificate-to-upstream`, `auth-tls-match-cn`) and basic authentication (`auth-type`, `auth-secret`, `auth-secret-type`, `auth-realm`); every other `auth-tls` key is refused, `auth-tls-error-page` (a redirect to another host) among them. The failure reads "ingresses <name>: <key> is an ingress-nginx authentication annotation the chart refuses" and never repeats the value. Also refused in every environment: an annotation value that writes an `auth_request` directive; the `custom-http-errors` and `default-backend` annotations (with them ingress-nginx proxies every request answered with one of the codes again to a default backend, with its Authorization and Cookie headers and original URI, the live hub's access token included); and `satisfy` with any value but `all`, or a `satisfy` directive in an annotation value (`satisfy any` with basic authentication would let a client outside `sourceRanges` in with a password). With `auth-url` ingress-nginx sends every request first to that URL with its headers, so a site's authentication service would receive Ariva's bearer tokens and refresh cookie. The controller values check (`tests/checks.mjs`) refuses every `global-auth-` key with a value (the `global-auth-snippet` whole), `enable-global-auth` true, a `*_pass`, `mirror`, `auth_request` or `satisfy` directive in the controller's snippets, a non-empty `custom-http-errors`, the chart's top-level `defaultBackend.enabled: true`, the `default-backend-service` argument, an `annotations-prefix` argument other than `nginx.ingress.kubernetes.io`, and the controller's annotation validation off (`controller.enableAnnotationValidations: false`, or `enable-annotation-validation` under `controller.extraArgs` with any value but true); any `controller.extraArgs` entry but `annotations-prefix` (the default), `enable-annotation-validation` (true) and `default-ssl-certificate`, each key read as the controller reads it (the flag name before its first `=`); a `defaultBackend.enabled` the chart reads as true (the string `"false"` included); `controller.configMapNamespace`; `controller.customTemplate`; and `allow-cross-namespace-resources: "true"`; the rendered Ingresses are checked for the refused authentication and error backend annotations. Nothing Ariva ships changes: no shipped values file or controller value sets any of them.
- Before the upgrade: a site values file that sets one of those annotations on an Ariva Ingress (for example a single sign-on proxy in front of the web host, through `auth-url` and `auth-signin`) must remove it, or the release fails to render naming the key and the Ingress; Ariva signs its users in itself (local accounts with TOTP, ADR-0026), so no external authentication is needed in front of it. A site that wants a second gate in front of a host uses `sourceRanges` (a network allowlist) or, for devices, the five documented client certificate keys (wiki 04 section 5), which stay allowed; a site that set `auth-tls-error-page` drops it (the client then gets the controller's own 400). A site values file with `custom-http-errors`, `default-backend` or `satisfy: any` on an Ariva Ingress drops it (Ariva's hosts write their own error answers; `satisfy: all`, the default, may stay). A site's own controller release that serves Ariva should set no `custom-http-errors`, no default backend (the top-level `defaultBackend.enabled`, `default-backend-service`), no other `annotations-prefix`, no `controller.enableAnnotationValidations: false` or `enable-annotation-validation` argument but true, and no `*_pass`, `mirror`, `auth_request` or `satisfy` directive in its snippets. A controller release that passes other `controller.extraArgs` (a `watch-namespace`, an `ingress-class`, its own `configmap`) keeps them out of Ariva's values file and confirms them with the operators, as they change what the controller reads past this check; one that reads its ConfigMap from another namespace or renders its own nginx template gives Ariva a controller of its own instead, since no check here sees that configuration; one with `allow-cross-namespace-resources: "true"` sets it back to `"false"` (every Ariva Ingress names only Secrets of its own namespace); and `defaultBackend.enabled` stays `false` (a string `"false"` turns the default backend on). The check reads the values as the ingress-nginx chart renders them: a controller values file gives each ConfigMap value as it is to be written (no `{{`: the chart renders every value as a template), each ConfigMap key as a plain name of lowercase letters, digits and hyphens, and no line break in any other setting of the file, the top-level names included (the chart writes the keys and the arguments unquoted); a snippet value over several lines is fine. Helm reads the file as YAML 1.1, so an unquoted `on`, `yes` or `y` is true: quote every `controller.config` value (the check refuses a value that is not a string, and the rule for its key reads it as true). The file uses no YAML tag (`!!binary`, `!!set`, `!!omap`, `!!str`, or one of the site's own such as `!site`: Helm and the check would read the value differently), no `%YAML` or `%TAG` directive, no anchor, alias or merge key, one document only and no byte order mark, every key is text, every value but `true`, `false` and a whole number is quoted, and a value over several lines is a literal block (`|`), not a folded one (`>`); a site whose controller values use any of these writes them plainly before the check can pass. A site's own controller release should set no `global-auth-url` or other `global-auth-` key on a controller that serves Ariva; where other teams need global authentication, give Ariva a controller of its own, or set `enable-global-auth: "false"` on each Ariva Ingress (the chart allows it).

### ARV-297: the advisories of 2026-11-10, SignalR's Redis backplane and Aspire (2026-11-11)

- Behaviour: every .NET image (api-main, api-ingest, api-stream, api-cronz, api-integration, the simulator) builds with SDK 10.0.404 and ships the 10.0.15 runtime packs on `runtime-deps` 10.0.15, and api-main ships `Microsoft.AspNetCore.SignalR.StackExchangeRedis` 10.0.15: together they fix GHSA-wr8v-9x5q-ggcm (CVE-2026-114147, High), in which SignalR's Redis scale-out mishandled a group removal whose name differs in letter case from the joined group's and left subscriptions that grow, a denial of service. The live hub also stops passing on the removal of a zone group the connection did not join under the exact key, and a connection that joins a zone renamed in letter case alone leaves the old key's group first. An image build fails unless both runtime packs are 10.0.15 or later and SignalR.Core (every image) and the Redis backplane assembly (api-main) are at 10.0.15's file version or later. The AppHost (development and test only) moves to Aspire 13.6.5 (GHSA-f7vh-h5g3-7jrh, High, macOS only). No setting, script, grant or chart value changes.
- Before the upgrade: nothing.
- After the upgrade: an image built before this release still carries the vulnerable runtime, whatever its settings: replace every running .NET image with one built from this release, and check one with `scripts/published-runtime.mjs --check ... --runtime 10.0.15` (wiki 04 section 6.5). A developer who runs the AppHost restores again (`dotnet restore`, `dotnet tool restore`).

### ARV-083d: the ops and restore checks hold the host roles as deeply as the runtime role (2026-11-11)

- Behaviour: `ariva-db.sh check` (and the check after a restore) judges the six host roles as it judges `ariva_runtime`: their privileges on chunks, compressed chunks, continuous aggregates' tables, relations outside `public` and Ariva's sequences ("internal privileges", a finding ends with "for <role>"), grants on system, extension and toast tables, parameters and system functions to them or their logins, CREATE for them, a grant to a host login rather than its role, the caps of every host login and the availability ledger's ("runtime caps" lists them all, not only `--runtime-login`), and at most one login per host role, holding `ariva_runtime` and that role only without admin option and never named `ariva_app` ("group members"), with no member of its own (nor of any role that holds a host login); CREATEDB on any login of Ariva's roles ("logins"); and what the migration job refuses a host login for, in any database of the server ("role objects": an object a login of Ariva's roles, the `--reader-login` included, owns or a privilege it holds but CONNECT on a database, TEMP and CREATE on any database included, and anything `ariva_runtime` or a host role owns). Every report line is printed with its control characters replaced. `check` and `restore` refuse `--runtime-login ariva_app`. No script, grant or setting changes.
- Before the upgrade: nothing. A cluster's migration job has refused `ariva_app` as a host login since ARV-083a, so a deployment that passed `check` passes it still.
- After the upgrade: run `ariva-ops.mjs check` once (wiki 04 section 10.4, step 4); a new FAIL names a grant or a login made by hand since the release with ARV-083a: wiki 10 section 4.13.

### ARV-083b: a Kafka principal and a Redis user per host, and no credential in the shared settings (2026-11-10)

- Behaviour: each API host signs in to Kafka as its own principal (`kafkaLogins.<host>`, as `Kafka__SaslUsername` and `Kafka__SaslPassword`) and to Redis as its own ACL user (`redisLogins.<host>`, as `Redis__Username` and `Redis__Password`, which replace any user or password of `Redis:ConnectionString`), each from a Secret no other pod gets. In k8s-prd a host or the topics job with Kafka on refuses to start without its principal, a SASL mechanism, `Kafka:SslCaLocation` or its file (a site's brokers always use the internal CA: the owner's decision of 2026-11-10), and a host with Redis on without its user and password or as `default`. In every cluster each host and job refuses to start while `appsettings.base.json` or `appsettings.base.<env>.json` gives a password, a secret or a Redis connection string with a user or a password. The migration job takes the migration login's password from `migration.passwordSecret` (`timescaledb-credentials`, key `migration-password`, by default). The distributed cache's keys move under `ariva:cache:`, so the cache starts cold once. The committed `appsettings.base.k8s-prd.json` no longer names a Kafka principal. The chart refuses `kafkaSecretName`; the shared `ariva-kafka` Secret is no longer read.
- Before the upgrade: (1) have the site's Kafka administrator create one SCRAM user per host and one for the topics job and apply the generated kafka-acls commands, and the site's Redis administrator create one ACL user per host with the generated ACL SETUSER lines and turn the `default` user off (`ACL SETUSER default off`; never rely on `requirepass`), on a Redis with the October 2025 Lua fixes (7.2.11, 7.4.6, 8.0.4, 8.2.2 or later) (wiki 04 section 6.2a); (2) create the ten Secrets `ariva-kafka-<host>` and `ariva-redis-<host>` and, for k8s-prd, `ariva-kafka-topics` (keys `username` and `password`; wiki 04 section 6.2; in k8s-dev the release writes them from the `ARIVA-K8S-DEV-KAFKA-*` and `ARIVA-K8S-DEV-REDIS-*` variables, and skips a pair left out); (3) remove every password and secret from the base settings (`Database:Password`, `Database:Migration:Password`, any `password=` or `user=` in `Redis:ConnectionString`, any other) and the `Kafka:SaslUsername` with them, in `base-appsettings-secret` and, for k8s-dev, in `ARIVA-K8S-DEV-BASE-APPSETTINGS`; (4) for a site's own PostgreSQL, create the Secret `migration.passwordSecret` names with the migration login's password; (5) drop `kafkaSecretName` from the site's values.
- What fails if one is missing: a base settings file that still carries a credential stops the migration job (a pre-upgrade hook, so the release fails before any pod changes, with "base-appsettings-secret is mounted in every pod, so it carries no credential (ARV-083b): ... gives <setting>") and every host; a missing `migration.passwordSecret` stops the migration job the same way (CreateContainerConfigError); in k8s-prd a missing Kafka or Redis Secret leaves that host waiting in CreateContainerConfigError, a host without the Kafka CA file stops with "Kafka:SslCaLocation names a file that does not exist", and a principal or user without its grants is refused by the broker or Redis (the outbox relay holds the topic back, a consumer endpoint is not connected or faults, the cache falls back to memory and the live paths fail; `ACL LOG` and the broker's authorizer log name what is missing); the chart refuses a values file with `kafkaSecretName`, with two hosts on one Secret, with `optional: true` in k8s-prd, or in k8s-prd without the topics job or its own principal (`kafkaTopics.adminSecretName`), and a k8s-prd topics job without its Secret fails the release before any pod changes. Outside k8s-prd the Kafka and Redis Secrets stay optional: a host without them signs in as before (plaintext brokers, a Redis without users).
- After the upgrade: delete the old shared `ariva-kafka` Secret and withdraw the old shared principal `ariva` and any shared Redis password at the broker and in Redis.

### ARV-083a: a database login per host, with least-privilege grants (2026-11-10)

- Behaviour: script 0074 creates the host roles `ariva_main`, `ariva_ingest`, `ariva_stream`, `ariva_cronz`, `ariva_integration` and `ariva_availability`, grants each what its host's code reads and writes (the manifest `HostDatabaseAccess`), narrows `ariva_runtime` to the base every host needs, removes the default privileges and drops `ariva_ensure_runtime_login`. The migration job creates each host's login and the availability ledger's from a SCRAM-SHA-256 verifier (`Database:HostLogins`, `Database:Availability`), each a member of `ariva_runtime` and its role only; each host connects with its own login, and api-cronz's availability ledger with its own (a pool of 2 more connections, counted by the chart). A cluster's migration job refuses to run without all six logins, and api-cronz to start without the ledger's. With ARV-207 and ARV-211: the key ring purge runs for `ariva_cronz` alone, and the key ring upgrade check at start runs on api-main and api-cronz only (the hosts whose login may read the stored Data Protection payloads); the migration job still checks at release.
- Before the upgrade: create the six secrets `ariva-db-login-main`, `-ingest`, `-stream`, `-cronz`, `-integration` and `-availability` (keys `username` and `password`, each a name and a password of its own; wiki 04 section 6.2), or the migration job stops before any script runs and the release fails with the old pods serving. Plan the release in a quiet window: once the job has applied script 0074, the old pods on the shared `ariva_app` fail what they read and write beyond the base until the rollout replaces them (sign-in on api-main, the devices on api-ingest, the feeds on api-integration, the stream's writes). The owner accepts that window in writing before the first k8s-prd release (docs/product/decisions.md, ARV-083a, Proposed).
- After the upgrade: `ariva-db.sh check` passes "host privileges" and "default privileges" (none) and fails "group members" (`ariva_runtime has member ariva_app`) until the old shared login is withdrawn: as the migration login run `ALTER ROLE ariva_app NOLOGIN;` and `REVOKE ariva_runtime FROM ariva_app;`, remove `Database:Username` and `Password` from `base-appsettings-secret`, and run `check --runtime-login <api-main's login>`. A vm-local machine needs nothing: its hosts share `Database:Username`, which the job gives every host role.

### ARV-244: a restore goes through the backup's own TimescaleDB version (2026-11-10)

- Behaviour: `ariva-ops.mjs restore` takes the backup's TimescaleDB version from the signed manifest (or, with `--sha256`, from the dump), refuses a dump that records another or none, and refuses with nothing changed a version the image does not ship. A backup of an older version the image ships is restored through it: the database superuser replaces the image's extension with that version in the empty database, restores, judges the dump with check's lines and updates the extensions to the image's versions (wiki 04 section 10.4). The superuser statements this adds are proposed and pending the owner (docs/product/decisions.md, 2026-11-10 ARV-244 entry).
- Before the upgrade: nothing. Use the `ariva-ops.mjs` and `ariva-db.sh` of one release: the new wrapper passes `--timescaledb-version` and expects the pod's check pass to report the version.
- After the upgrade: the manual steps of the ARV-236 note for a backup taken before 2.31 are gone. On a DBA-provided server restored with `--allow-extension` or `--allow-setting`, a backup of an older TimescaleDB than the server's cannot be restored there (the server's own extension or setting is refused before anything changes): restore it on a server of the image of its own version, run `update-extensions` there and take a new backup.
### ARV-294: the long waits inside transactions shortened, and the idle transaction limit at 300 s (2026-11-10)

- Behaviour: `Database:IdleInTransactionTimeoutSeconds` defaults to 300 s (960 s before, Proposed): no transaction of a host waits on other work for longer than 240 s at any value its settings allow, so a session lost on the network gives up its locks within five minutes (wiki 04 section 6.11).
- Behaviour: an alert email round claims its emails and commits, sends them outside any transaction and marks each in a transaction of its own as the relay answers. Delivery stays at least once: an email whose host is lost between the relay's answer and the mark is sent again once its claim runs out (9 minutes at the defaults), where before the whole batch went again at the next round (docs/product/decisions.md, ARV-294, Proposed).
- Behaviour: a scheduled report's send ends at a total budget of 180 s and the delivery is counted as failed, to be retried by later rounds; the uploads and the site configuration transfer do their file work outside their transactions; the availability ledger reads Redis before its site lock.
- Behaviour: each host reads its runtime login's limit on the server at start (`SHOW`) and refuses to start when it is not above its longest wait (wiki 10 section 4.19). The shipped settings and the migration job's value start.
- Before the upgrade: nothing. An installation that raised the limit for ARV-292 may take it out of its base settings.
- During the rollout: the migration job lowers the login's limit to 300 s before the old pods stop, so an old pod's alert email round, report send or transfer request that waits longer than that inside its transaction meanwhile is ended by the server and done again (its emails sent again, its upload refused and asked again). Upgrade outside a busy hour.
- After the upgrade: the migration job sets 300 s on the runtime login; `ariva-db.sh check --runtime-login` passes "runtime caps" as before. The Data Protection key ring's connections open within at most 50 s and its statements wait at most 30 s, which changes nothing at the shipped defaults (15 s and 30 s).
- Rolling back to a build before ARV-294: run that build's migration job too (it sets the 960 s its settings give), or the old hosts run under the 300 s limit, which their longer waits inside transactions (a transfer request, an email round) can pass; they would not notice, having no start check of the server's value.
### ARV-211: the Data Protection key ring signed with a key of its own (2026-11-10)

- Behaviour: a certificate of the key ring's own (`ariva-dataprotection-signing`, ECDSA P-256, key usage digital signature alone) signs every key ring element; the Data Protection certificate only wraps them. A host admits a key only under the current signing certificate, or, only to unprotect, under a previous one configured by its certificate alone; a key signed by a retired signing certificate, or by a Data Protection certificate's own key outside the upgrade window, is refused. Script 0073 adds `data_protection_key.signature`: the hosts sign each row they store (its friendly name and exact text) and check it before they read the row, so a row a database writer adds or copies, a revocation among them, is refused; rows written before carry none and are read only inside the window. The migration job, after its scripts, and api-main and api-cronz at start outside vm-local (since ARV-083a the hosts whose login may read the stored values) refuse when the stored values need keys from before ARV-211, none is yet under a signed key and the window is not open (the job exits with code 2); values left under old keys after the upgrade never stop a host or a release: they are measured (`ariva.keyring.problems`, outcome `unsigned`, and `retired-signer` for values under the keys of a signing certificate no host has configured, after a rotation) and alerted by the new rule `keyRingUnsigned` (wiki 10 section 4.25). The chart mounts a rotation's previous certificates from `dataProtectionPreviousSecretName` and `dataProtectionSigningPreviousSecretName` (empty by default). `ariva-ops.mjs keyring` names each key's signer and whether its row is signed.
- Before the upgrade: create the Secret `ariva-dataprotection-signing` (wiki 04 section 6.2; without it the new API pods do not start). An installation that holds data sets `DataProtection:Signing:CertificateSignedUntil` to a date 7 to 90 days ahead in the environment's base appsettings (a later date is refused): without it the migration job stops the upgrade before any pod rolls (exit code 2, "Key ring upgrade check refused", wiki 04 section 6.2), and nothing is deleted.
- After the upgrade: run `SecretReprotection` from the jobs dashboard (or let the nightly run do it), then, once its log shows no unreadable value, remove the window 2 hours later at the least and roll out the API hosts; do not let the date pass instead (wiki 04 section 6.2, the upgrade steps). Import the alert rules again (wiki 10 section 1.1) for `keyRingUnsigned`. A rollback loses every value written after the upgrade at once (they are under a key the older release refuses), and after the first `SecretReprotection` run (02:40 UTC at the latest) every other stored value too: roll back only within hours, or restore the database taken before the upgrade (wiki 04 section 9).
- If a signing certificate is lost for good: the hosts refuse to start while the stored values need only its keys; `DataProtection:Signing:RetiredSignerAcceptedUntil` (a UTC date at most 7 days ahead, never committed) lets them start with those values unreadable and `keyRingUnsigned` firing, so the recovery of wiki 10 section 4.13 can run (authenticators reset, client and outbound secrets entered again); it admits no key and never excuses unsigned keys outside the upgrade window. While the window is open `ariva.keyring.problems` counts only what the hosts would refuse, so a normal upgrade does not fire `keyRingUnsigned`.

### ARV-207: expired Data Protection keys deleted, key ages recorded and alerted (2026-11-10)

- Behaviour: script 0072 adds `ariva_data_protection_key_purge`; the Cronz job `KeyLifecycle` (daily, 03:10 UTC) deletes an expired key 30 days after it expired (Proposed, `Cronz:KeyLifecycle:ExpiredKeyRetentionDays`, at least 30) once no stored value names it, never a revocation and never while no unexpired key is left. New gauges `ariva.keys.age`, `ariva.keys.rotation_due_in` (periods in `Monitoring:Cryptoperiods`) and `ariva.keyring.keys`; `ariva.certificates.expires_in` also reports the key ring signing certificate and the CA files the hosts use. Three new SigNoz rules, `keyRotationDue`, `keyRingHeld` and `keyLifecycleFailed` (a run that failed as a whole, `ariva.keyring.problems`; wiki 10 sections 3.1 and 4.25).
- Before the upgrade: nothing to set. The migration job applies script 0072; `ariva-db.sh check` of this release expects it.
- After the upgrade: import the alert rules again (wiki 10 section 1.1). A `keyRotationDue` warning at once on an older deployment is real: a key past its Proposed period (rotate it, or have the owner confirm the periods).

### ARV-209: no credential as a Helm value; the registry pull secret created out of band (2026-11-10)

- Behaviour: the chart no longer renders `dalilacr-secret` and fails the render on any `imageCredentials` value but the empty string, the registry key included (`imageCredentials.registry` is gone from the chart's values, and a value given for it is refused like the user name and the password; `validate.yaml`), since Helm keeps every value in its release record in the namespace. Every workload still pulls with `dalilacr-secret`. The Azure DevOps release writes it from its variable group (`ACR-USERNAME`, `ACR-PASSWORD`) in its step "Create the registry pull secret", before Helmfile runs, and `chart-security.mjs` refuses a release file that gives Helm or Helmfile a credential as a value (crypto inventory section 5 item 19).
- Before the upgrade: remove every `imageCredentials` value from the environment's values files, `--set` flags and HelmDeploy inputs, or the render fails naming the key. `dalilacr-secret` (type `kubernetes.io/dockerconfigjson`) must exist before the release: the Azure DevOps release writes it itself; anywhere else create it first (wiki 04 section 6.2), or the pods stay in `ImagePullBackOff`. In a namespace whose earlier release let the chart render it, the Azure DevOps release takes it out of Helm's hands; anywhere else run the annotate and label commands of wiki 04 section 6.2 first, or the upgrade deletes it.
- After the upgrade, once, in k8s-dev and every namespace an Azure DevOps release reached before ARV-209: rotate the registry credential, since Helm's release records written before ARV-209 still hold the old password. Issue a new password or token in the registry, put it in the variable group, run the release, then revoke the old one (wiki 04 section 6.2). From then on no rollback goes to a revision before the first ARV-209 release (wiki 04 section 9). The rotation is tracked to closure as TC-87 in the [pilot to confirm list](../docs/product/pilot-to-confirm.md) (section 6).

### ARV-288: the ingress refuses code, copies and kept uploads from site snippets in every environment (2026-11-10)

- Behaviour: the chart refuses to render, in every environment, a site `modsecurity-snippet` that writes `SecRuleScript`, an `exec:` action, `@inspectFile`, `SecUploadKeepFiles` or `SecTmpSaveUploadedFiles` (in any case, anywhere in its text, a comment included); an annotation value that writes a `js_` or `perl` directive, a `*_pass` directive (`proxy_pass`, `grpc_pass`, `fastcgi_pass`, `uwsgi_pass`, `scgi_pass`, `memcached_pass`; refused only with pod TLS before) or a `mirror` directive; and the annotations `enable-rewrite-log` and `mirror-target`, `mirror-host` or `mirror-request-body`. The controller values check (`tests/checks.mjs`) reads the `stream-snippet` like the other snippets and refuses `load_module`, `ssl_engine`, `lua_package_path`, `lua_package_cpath` and every `js_` and `perl` directive in any snippet, and a `plugins` key that is not empty; the rendered Ingresses are checked for `include`, `client_body_in_file_only`, `*_by_lua` and `rewrite_log`. Nothing Ariva ships changes.
- Before the upgrade: a site values file whose annotations write one of those (a ModSecurity rule with `exec:` or `@inspectFile`, a `proxy_pass` or `mirror` in a snippet, `enable-rewrite-log`, a mirror annotation) must drop it, or the release fails to render; and a site's own controller release should hold the same for its snippets and leave `plugins` empty. `error_page`, `log_subrequest`, `mirror_request_body`, `proxy_pass_header` and `SecUploadFileLimit` still render.

### ARV-292: an idle transaction limit and TCP keepalives on the runtime login (2026-11-05)

- Behaviour: the migration job ends a runtime login session left idle inside a transaction for longer than `Database:IdleInTransactionTimeoutSeconds` (960 s, Proposed) and sets TCP keepalives on the login (about two minutes to notice a vanished client), so a host whose connection was lost on the network no longer holds a report delivery, the report slot or a lock until the server notices; nothing of an ended transaction commits (wiki 10 section 4.19).
- Behaviour: a host refuses to start when its own settings allow a wait inside a transaction at or above that limit, naming the wait and its setting (wiki 04 section 6.11). The shipped settings start.
- Before the upgrade: nothing, unless an installation raised `Email:Smtp:TimeoutSeconds` to 60 or more, `Security:RequestTimeouts:TransferSeconds` to 960 or more, or `Database:CommandTimeoutSeconds` to 119 or more: raise `Database:IdleInTransactionTimeoutSeconds` in the base settings above the wait they allow first (wiki 04 section 6.11), or the hosts that hold them do not start (the SMTP one only reaches Integration and Cronz, so their pods stop during the rollout).
- After the upgrade: `ariva-db.sh check --runtime-login` passes "runtime caps" with the idle limit and the keepalives.

### ARV-240: a share of the runs of position samples per device (2026-11-05)

- Behaviour: each device of a zone holds at most its share of the runs of position samples (ARV-230), as it does of the buffer: the runs' bound (`MaxSightingRuns`, 20,000) over the most of the devices holding runs, the zone's devices heard recently and two, never below 2,000 (the bound times `DeviceShareFloor` over `MaxBufferedEvents`). When the runs are at their bound, a device below its share takes a run back from the device holding the most above it, so devices that flooded a quiet zone before the others pushed keep no runs the others need. A device flooding unique tracks into a quiet zone holds at most half the runs, so a sensor whose clock runs within Ingest's 500 ms tolerance fast keeps its tracks coalesced and its crossings counted while the flood lasts, where before the flood used the runs up and the sensor's samples ahead of the clock took a place each until its share of the ahead tenth refused them (`DeviceShareFull`).
- No outcome changes (a sample buffered instead of coalesced is applied to the same effect), no snapshot version (a restore counts each device's runs from their track keys, and takes back the same runs), nothing to configure. A sensor that sees more than 2,000 tracks within a run's life in a zone of ten or more devices has its further tracks buffered a sample a place; `MaxSightingRuns` or `DeviceShareFloor` raise that.

### ARV-290: a temporary file cap on the runtime login (2026-11-04)

- Behaviour: the migration job caps the temporary files each server process of the runtime login may hold (`Database:TempFileLimitMegabytes`, 1,024 MB, Proposed; a parallel worker has its own); a statement past it is ended with SQLSTATE 53400 instead of filling the database's disk (wiki 10 section 4.19). The migration job stops before its first script, with the grant to run, when its login may not set this superuser setting, so the upgrade's hook fails and the running hosts keep serving.
- Before the upgrade, on every server first started before this release: run `node scripts/ops/ariva-ops.mjs grant-settings --namespace <ns>` once (wiki 04 section 6.10), or the migration job stops and the upgrade waits; a server the chart starts afterwards needs nothing. With your own PostgreSQL, the administrator runs `GRANT SET ON PARAMETER temp_file_limit TO ariva;` once.
- After the upgrade: `ariva-db.sh check` passes "temp file cap" and "parameter privileges", lists the cap among the runtime login's settings, and with `--runtime-login` passes "runtime caps". A "FAIL parameter privileges" names a privilege on a parameter someone else granted (wiki 10 section 4.19).

### ARV-289 (checkpoint fix): the token parameter and device key masks no longer go wrong after a long log text (2026-11-04)

- Behaviour: the rules that mask a token-bearing query parameter (`?access_token=...`) and a device or display key (`ardk_`, `ardp_`) in log text, dead letters, the outbox's errors and job failures run on .NET's backtracking regular expression engine again. On .NET 10.0.13's linear engine, once a host had logged a long text of a certain shape (tens of thousands of characters of query parameters or key characters), those rules could miss a one-character token value at the end of a text, or miss a key of exactly 20 characters at the end of a text or take one character past it, until the host restarted. What the rules mask is unchanged, and so is the cost.
- After the upgrade: nothing to do.

### ARV-291: a scheduled report delivery whose read fails in the database counts its attempt (2026-11-04)

- Behaviour: a scheduled delivery whose report read fails in the database (a statement timeout, a lost connection) is marked Failed with the reason "The report could not be read from the database." and its attempt counted, so after 5 failed attempts it is not tried again, as a failed send. Before, it stayed as it was and every round tried it again without end (wiki 10 section 4.19).
- Behaviour: a unit of work whose database connection broke rolls back without an error of its own, so a host's job or request reports the database's failure instead of a disposed transaction; the host logs a warning "A transaction was rolled back after its database connection broke" with the cause's type and SQLSTATE.
- After the upgrade: nothing to do.

### ARV-234: the daily report read aggregated by the hour in the database, so a site of up to 500 queue zones gets its report (2026-11-04)

- Behaviour: the daily report and its CSV read the site's queue minutes aggregated per zone and local hour in PostgreSQL. A site of up to 500 queue zones (the most a profile holds) gets its report; before, a site of more than 200 got 409. The figures are the same as before. A day past the new bounds (12,500 zone hours, 3,125,000 merged histogram pairs) is still refused with 409 and its fixed text.
- Behaviour: at most two report statements run at once across the cluster. A report on demand that finds both slots taken answers 503 with `Retry-After: 10` and a fixed text; a scheduled delivery that finds the deliveries' slot taken is left pending for a later round and logged (wiki 10 section 4.19).
- After the upgrade: nothing to do. A 503 from the report under load means two reports were being built; ask again after the delay.

### ARV-229a: one account's live hub connections bounded, and Integration calls without credentials take no slot (2026-11-03)

- Behaviour: one account holds at most `Security:Hubs:MaxConnectionsPerAccount` live hub connections on an Ariva.Api.Main replica (200 by default, Proposed), every sign-in of the account together, beside the 8 of each sign-in and the replica's total. A negotiate beyond is answered 429 with Retry-After (security event 9116, policy `hub-connections`, partition `user`) and a connection past it is closed with `account_full`; the screen shows its live feed as disconnected, as for `hub_full`. A setting outside 8 to 20,000 stops Main at start.
- Behaviour: a batch or AIDX message that sends no Authorization value no longer waits for, or is refused 429 by, the `integration-batch` and `integration-aidx` policies: it is answered 401 at once, as before when a slot was free. Every call that sends credentials is counted as before.
- After the upgrade: nothing to do where screens sign in under their own accounts or a wall of screens under one account stays at 200 or fewer per replica. A site that signs more than 200 screens in under one account on one replica raises `Security:Hubs:MaxConnectionsPerAccount` (wiki 04 section 5) or signs part of the wall in under a second account before upgrading. The limit is per replica: size it for the fewest replicas left when one is lost or replaced.

### ARV-228b: an alert rule backtest at its bound takes a fifth of the memory, and a sensor rule's outage read is bounded (2026-11-03)

- Behaviour: a backtest of a rule on 64 zones over a day, or of a sensor rule on 1,000 devices, allocates 4 to 70 MB where it allocated 146 to 398 MB, and answers in about the same time; the live evaluation and the backtest raise and clear exactly as before. The rows the alert inputs read are mapped by position instead of by reflection, and a sensor rule's minutes are computed when judged instead of stored.
- Behaviour: a sensor rule reads at most 100,000 device outages over a range (`AlertInputs.DefaultMaxOutageRows`, as the staffing and health reads are bounded): beyond that a backtest answers "The range holds more stored bins than one backtest reads: choose a shorter range or fewer zones." and the live evaluation skips the rule for that tick with the warning it already writes for the other reads ("its stored inputs exceed the rows one read takes"). A backtest of a day on the 1,000 devices a rule may watch reaches it only with more than 100 outages a device.
- After the upgrade: nothing to do. No answer changes below the bound.

### ARV-228a: device outages kept for the raw sensing period, and the outage and staffing reads bounded (2026-11-03)

- Behaviour: script 0068 makes `zone_outage` (the device outages the stream records) a TimescaleDB hypertable kept for the raw sensing period, `Database:Retention:RawSensingDays` (90 days by default, as `device_health_event`); before, it was kept indefinitely. It adds an index on an outage's end, one on the outages still open, and a covering index of interval 0 of `lane_staffing`, so the sensor rule, the reports and the validation results read a range without reading the table's history, and the open desks alert reads interval 0 alone. A P90 rule reads one 15-minute bin beyond its 150-minute look-back instead of a day.
- Before the upgrade: TimescaleDB drops the outages that started more than the period ago at its first daily run after the migration. Export them first if they are still wanted, or raise `Database:Retention:RawSensingDays` (which keeps the raw sensing archive as long). The migration moves the existing rows into chunks and builds the interval 0 index of `lane_staffing` (90 days of it) in its transaction, holding writes to both tables meanwhile (the stream's outages, the staffing job's minutes), all within the migration job's deadline (`activeDeadlineSeconds` 900): on a site with a large `lane_staffing`, run the migration in a quiet hour.
- After the upgrade: nothing to do. No answer changes.

### ARV-286: desk code mappings and floor plans answer only the domain's own refusals, and network refusals are escaped (2026-11-03)

- Behaviour: the desk code mapping and floor plan endpoints answer a 400 only with a rule the domain states (those texts are unchanged); any other exception where the entity runs, or in a mapping's save and audit entry, is now a 500 without its text, as on the topology, zone profile and device endpoints since ARV-282. In a desk code mapping CSV import, such a failure ends the import with a 500 (the import's Failed audit entry is written) where before it refused the row.
- Behaviour: the refusals that name a caller's source network (a device's access, an integration client's networks, an outbound endpoint's `allowedNetworks`: not in CIDR form, host bits set, IPv4-mapped) write the block cut to 50 characters and escaped: every character outside printable ASCII is written as its code (`\u202E`, `\U0001F600`, also a letter or digit of another script) and a backslash is doubled, so the answer cannot reorder or hide its own text. Before, a device's host-bits refusal echoed the whole block as sent, and the other refusals echoed it unescaped.
- After the upgrade: nothing to do. A network refusal for a block of printable ASCII without a backslash reads as before (a device's host-bits refusal only up to 50 characters, the others were already cut there); one for a block holding a backslash or any character outside printable ASCII reads in the escaped form, and a device's host-bits refusal for a longer block reads cut, so a client that matched such a text sees it changed. Only a failure that was never the caller's (a driver or session error) changes from a 400 to a 500.

### ARV-285: exception text kept outside the logs is protected as log text is (2026-11-02)

- Behaviour: a dead letter's error message, `outbox_message.last_error` and the exception TickerQ keeps of a failed job run (shown on the Cronz dashboard) now mask credentials by shape and name, and email addresses, with the logs' rules (`SecretText` in `Ariva.Infra/Security`, which `RedactionEnricher` and `ProtectedExceptionSink` now use), before any cut. `last_error` is also made printable for PostgreSQL, so a NUL or a lone surrogate in a client's message no longer fails the row's update.
- After the upgrade: nothing to do. A `last_error`, a dead letter or a failed run on the dashboard may read `[REDACTED]` (an integration client secret `ics_[REDACTED]`) where a client's message held a credential; the cause is still named. A failed run whose text held one shows the type `ProtectedException`, its message starting with the original type's name.

### ARV-284: the ingress snippet guards read names as nginx does, and refuse include, client_body_in_file_only and Lua everywhere (2026-11-02)

- Behaviour: the chart refuses to render a site's Ingress annotation that writes an nginx `include`, `client_body_in_file_only` or a `*_by_lua` directive in any environment (Lua was refused only with pod TLS before), and the `access_log`, `error_log`, `log_format`, `rewrite_log`, `modsecurity` and `rewrite` guards now read a directive's name as nginx's tokenizer does, so a quoted name followed by `)` (which nginx takes as the name and the start of the next argument) is refused too; `log_format` and `rewrite_log` join `access_log` and `error_log` (`rewrite_log` would write the chart's server-level checks, with the request line and its query string, at notice). The controller values check (`tests/checks.mjs`) refuses the same in the controller's main, http, server, location and global auth snippets.
- Before the upgrade: a site values file whose annotations write one of those directives must drop it; the controller values Ariva ships write none.

### ARV-280: every TLS client a host opens takes TLS 1.2 and 1.3 and the pods' cipher suites only (2026-11-01)

- Behaviour: the connections to PostgreSQL, Redis, the SMTP relay, Loki and the OTLP collector, and Kafka (Ariva's own producer, admin and stream consumer, and the MassTransit consumers) now offer TLS 1.2 and 1.3 only and, on Linux, the suites the pods' listeners offer (TLS 1.3's three; for TLS 1.2 ECDHE with AES-GCM or ChaCha20-Poly1305), as outbound integration calls have since ARV-205. Kafka's TLS 1.3 suites stay OpenSSL's default three, the same ones. Before, each library's defaults applied. A Redis connection string's `sslProtocols` now only narrows the two versions.
- Before the upgrade: check that the site's Redis, Kafka brokers (Ssl or SaslSsl), SMTP relay (TLS on connect or STARTTLS) and https collectors accept TLS 1.2 with an ECDHE AEAD suite, or TLS 1.3, from a machine that can reach them: `openssl s_client -connect <host>:<port> -tls1_2 -cipher 'ECDHE+AESGCM:ECDHE+CHACHA20'` (add `-starttls smtp` for a STARTTLS relay) or `openssl s_client -connect <host>:<port> -tls1_3`; one of the two must complete its handshake. A server that offers only an older version, or only CBC or DHE suites, is refused after the upgrade and the host reports that dependency as down (Redis and Kafka on readiness, the relay as a failed send, the collectors as missing logs and traces). The production database already serves TLS 1.2 or later with ECDHE and AEAD (ARV-082a). A `Redis:ConnectionString` whose `sslProtocols` names neither `Tls12` nor `Tls13` must drop it or name one of them: the host refuses it (k8s-prd at start). Kafka's clients now run at OpenSSL security level 2, which also refuses a broker certificate whose key is shorter than RSA 2048 or ECC 224 bits, or one signed with SHA-1: check the brokers' certificates (`openssl x509 -in <broker.crt> -noout -text`) before the upgrade.

### ARV-214: CI's SDK band, Ryuk and the Dockerfile command forms (2026-11-01)

- Behaviour: CI builds and tests with the newest .NET SDK of the band the images build with (`10.0.4xx` in every `actions/setup-dotnet` step, where it took the newest 10.0 SDK, and a temporary `global.json` each job writes at the repository root from the version installed, so a newer SDK on the runner is not used); no `global.json` is committed, so any .NET 10 SDK builds the repository locally. The integration tests' Testcontainers resource reaper is named by tag and digest in ci.yml and the Azure DevOps analysis (`TESTCONTAINERS_RYUK_CONTAINER_IMAGE`), the same image Testcontainers 4.15.0 names, so it joins the third-party image scan and the patch watch. `scripts/base-images.mjs --check` now reads more RUN forms (wiki 13 section 9 lists them and what it does not read), refuses a command whose tool is a shell expansion and a SHELL that is not a shell, and holds the SDK band, the Ryuk pin, the local tools and the Aspire SDK. Nothing Ariva ships changes.
- Before the upgrade: nothing for a site. For a change to a Dockerfile, a pipeline or a tool manifest: an npm install, `npm exec` or an unlocked .NET restore written in the exec form (`RUN ["npm", "install"]`), by a path (`/usr/local/bin/npm`), behind `env` or a `VAR=value` prefix, in a here-document or as `npm --prefix web install` now fails the check as the plain form always did, and so do npm, yarn, pnpm or bun behind `sudo` or `xargs`, a here-document never closed, `dotnet test`, `run`, `pack` and `format` without `--no-restore`, `--no-build` or `-p:RestoreLockedMode=true`, `dnx`, a .NET tool, workload or added package, and a package source on the command line; a moved SDK image band must move CI's `dotnet-version` and its hold step with it; a new job that runs dotnet needs setup-dotnet and the hold step; a new Aspire release moves the AppHost's SDK, every `Aspire.Hosting` package and the aspire.cli tool together.

### ARV-212: Ariva's secret formats masked inside log text, exception text included (2026-11-01)

- Behaviour: inside any log text the hosts now also mask an integration client secret (written `ics_[REDACTED]`), a temporary password, a recovery code, a TOTP seed, a PEM private key and a DER key in base64, and any value written under a credential's name as a JSON member, a `name=value` pair (a connection string's `Password=`, a form's `client_secret=`) or a header line (`Cookie:`, `X-Api-Key:`). A logged exception whose text holds one is written with that text protected: its stack and inner exceptions are kept but masked, and its type becomes `Ariva.Api.Common.Logging.ProtectedException`, in Loki's `Type` and OTLP's `exception.type`; the original type's name opens its message (Loki's `Message`, OTLP's `exception.message`) and its whole text (OTLP's `exception.stacktrace`, the console).
- After the upgrade: a saved search or alert that matched an exception's type in the OTLP collector should also match the type at the start of its stack text. Text that only looks like a credential is masked too: a word of 16 or more capitals of A to Z and 2 to 7, two dashed groups of five capitals or digits (a port range such as `30000-32767`), four dashed groups of five letters and digits, the rest of a line after `Cookie:`, `X-Api-Key:` or an `Authorization:` header, and after a password pair the rest of its value up to the next `;`, or the rest of the line when none follows (`mustChangePassword=false, maxAge=90` keeps only `mustChangePassword=`).

### ARV-206: the ingress refuses a proxy cache (2026-11-01)

- Behaviour: the chart refuses to render a site's Ingress annotation that names `proxy_cache`, `proxy_cache_path` or `proxy_store` (or the `fastcgi_`, `uwsgi_` or `scgi_` directive) anywhere in its text, or that sets `auth-cache-key` or `auth-cache-duration`; the chart test refuses those names in the controller's snippets and a `global-auth-cache-key` (`Helm/ingress-nginx-values.yaml`). So no site annotation or shipped controller value keeps a copy of an answer at the ingress (a Lua block outside pod TLS and an `include` are ARV-284's). Nothing Ariva ships changes.
- Before the upgrade: a site values file whose annotations name one of those directives must drop it, a defensive `proxy_cache off;` or `proxy_store off;` and a comment naming one included (the name alone fails the render), and so must a controller release whose snippets do; `proxy_no_cache`, `proxy_cache_bypass` and `proxy_cache_valid` may stay. Drop `auth-cache-key`, `auth-cache-duration` and the controller's `global-auth-cache-key` too.

### ARV-204: the ingress's own answers name object-src and base-uri in their policy (2026-11-01)

- Behaviour: the policy the ingress adds to every response (`add_header` in the chart's `server-snippet` and `configuration-snippet`) is now `frame-ancestors 'none'; object-src 'none'; base-uri 'none'` instead of `frame-ancestors 'none'`. The ingress's own answers (the 421, the probes' 404, the 403 and the redirect over plain HTTP, the 413, the 502 to 504) carry it; the pods' answers keep their own policy beside it, which already names or implies all three (the floor plan and advertisement file policies through `default-src 'none'`), so nothing they serve changes.
- After the upgrade: the TLS check (`scripts/ops/tls-check.mjs`) now fails a host whose own 413, probe 404 or plain HTTP answer lacks `object-src 'none'` or `base-uri 'none'`; a host still behind an older chart's Ingresses reports it until the release that carries this chart.

### ARV-203: refused uploads are security events (2026-11-01)

- Behaviour: an upload refused with 400, 404, 408, 409, 413, 415 or 422 (by its controller or its service) now writes `SecurityEvent.UploadRefused` (9134) at Warning when no other security event recorded it: the caller, the upload's kind, the endpoint's route pattern, the status and a fixed reason, never the file or its name. Floor plans, SSIM previews and imports, the CSV imports' refusals before their service, site configuration imports and advertisement uploads were not logged before. The answers do not change.
- After the upgrade: a saved search or SIEM rule on the request-level events (9114 to 9119, 9127 to 9129) can add 9134; [log inventory](../docs/security/log-inventory.md) section 10.2 proposes an alert on repeated `UploadInvalid` or `UploadTooLarge` from one caller.

### ARV-201: PNG and JPEG floor plans are kept without their metadata (2026-11-01)

- Behaviour: a PNG or JPEG floor plan, uploaded or in a site configuration bundle, is stored and served without its metadata (EXIF, XMP, text chunks, comments, colour profiles, thumbnails, animation and private chunks) and without anything after the image's end; its compressed pixels, pixel size and calibration stay as uploaded. With the EXIF orientation and the colour profile gone, a plan shows as its pixels are stored and in sRGB. A raster cut short or otherwise broken is now refused, where it was taken before: an upload with 400 ("Upload a PNG, JPEG or SVG image."), a bundle import with its floor plan file refusal.
- Before the upgrade: nothing. After it: a plan photographed on its side shows on its side; rotate it in an image editor, export it upright and in sRGB, upload it again and check the calibration ([Troubleshooting](17-Troubleshooting-and-FAQ.md)). Plans stored before the upgrade keep their metadata until uploaded again (ARV-283, an owner decision).

### ARV-199: the web host's /api and /hubs refuse plain HTTP, and the affinity cookies carry the __Host- prefix (2026-11-01)

- Behaviour: on the web host, `/api` and `/hubs` now answer plain HTTP with 403, as the machine hosts do, while the web app still redirects to HTTPS (308). The affinity cookies are named `__Host-ariva-<Ingress name>` instead of `ariva-<Ingress name>` (still Secure, SameSite=Strict, Path=/, and no Domain). The first request after the upgrade carries only the old cookie, so it may reach another replica once; the affinity cookie only routes requests and holds no sign-in state.
- Before the upgrade: a site values file that sets `plainHttp: redirect` on its `web-api` or `web-hubs` entry must set `refuse` or drop the key (k8s-prd refuses the release otherwise), and one that sets `session-cookie-domain` or `enable-cors` on an Ingress must drop it (both refused everywhere: a Domain makes browsers drop a `__Host-` cookie, and with CORS ingress-nginx answers OPTIONS itself before the plain HTTP refusal; Ariva needs no cross-origin calls). A client configured with `http://<web host>/api` gets 403 after the upgrade: give it the `https://` URL ([Deployment guide](04-Deployment-Guide.md) section 6.4, response headers and plain HTTP).
- After the upgrade: the TLS check now probes plain HTTP at every path the host's Ingresses route (and that path with a trailing slash), accepts only the ingress's 403 on a refuse path, lists each path with its mode in the summary (`/ redirect, /api refuse, /hubs refuse` for the web host), and fails a host whose `GET /` sets no `__Host-ariva-...` affinity cookie with Secure, `Path=/`, SameSite=Strict and no Domain. A controller that rejected the renamed cookie's annotation shows here first: read its log for the Ingress (wiki 10 section 4.14).

### ARV-205: outbound calls take TLS 1.2 and 1.3 and the pods' cipher suites only (2026-10-31)

- Behaviour: every outbound integration call (AODB, ACRIS and AMAN pulls, any registered endpoint) now offers TLS 1.2 and 1.3 only and, on Linux, the same suites the pods' listeners offer (TLS 1.3's three; for TLS 1.2 ECDHE with AES-GCM or ChaCha20-Poly1305). Before, the platform's defaults applied.
- Before the upgrade: check that each partner endpoint accepts TLS 1.2 with an ECDHE AEAD suite, or TLS 1.3, from a machine that can reach it: `openssl s_client -connect <host>:<port> -tls1_2 -cipher 'ECDHE+AESGCM:ECDHE+CHACHA20'` for TLS 1.2 and `openssl s_client -connect <host>:<port> -tls1_3` for TLS 1.3 (one of the two must complete its handshake). A partner that offers only an older version, or only CBC suites, is refused after the upgrade, as a failed call ([Integration guide](08-Integration-Guide.md) section 11).
- Not changed: the other connections a host opens (the log and trace collector, Redis, SMTP, PostgreSQL, Kafka) keep their libraries' TLS defaults; ARV-280 pins them (above).

### ARV-277: the push answer names events on lines their device does not declare it counts (2026-10-31)

- Behaviour: a push from a device that declares the lines it counts (Listed or None) with accepted crossings or interval readings on another entry or exit line of its zone answers 202 as before, and its `problems` now also name each such line once with its count (`undeclared line '<name>': <n> events accepted on an entry or exit line this device does not declare it counts`), at most 10 such problems apart from the refusals (nine lines named, the tenth summing up the rest). The events are still published and counted. A device that declared nothing has no such problem.
- New counter `ariva.ingest.undeclared_line_events` (meter `Ariva.Ingest`, tag `site`), exported by Ariva.Api.Ingest (wiki 10 sections 2a and 4.22); no alert rule ships with it.
- A device or gateway that treats any problem as a failure, rather than reading `accepted` and `rejected`, should be checked before the upgrade: the answer is still 202 with nothing refused.

### ARV-276: site configuration bundles carry each device's line declaration (2026-10-31)

- Behaviour: a site configuration export now carries the lines each device counts (`lineCoverage` All, None or Listed, and `coveredLines` for a device that lists them), and an import applies them through the registry's own path, after each device is registered. The import refuses a device that lists a line that is not an entry or exit line of its queue zone in the bundle's zone profile (400, naming the device's row), and lists in `devicesWithLinesOutOfReach` the devices whose footprint does not reach a line they count (wiki 11 section 2d).
- The export answers 409 for a device that lists a line its zone no longer has in the published profile: declare its lines again first (wiki 11 section 6).
- A bundle exported before this release carries no declaration and still imports: its devices count every line, as before. The bundle's format version stays 1; a release before this one refuses a bundle from this one (a member it does not know).

### ARV-275: each pod's telemetry names the pod (2026-10-31)

- Behaviour: the chart's `OTEL_RESOURCE_ATTRIBUTES` now carries `service.instance.id`, `k8s.pod.name` (both the pod's name) and `k8s.namespace.name` on the five API hosts and the simulator, declared after the downward API variables they are made from. The replicas of a host export series told apart, so a gauge each reports for its own share (`ariva.stream.zones_unknown`, `ariva.stream.consumer_lag`) is summed rather than read as interleaved samples (wiki 10 sections 1 and 2a). The hosts still set no instance id of their own.
- After the upgrade: nothing to configure. Each new pod is a new instance in SigNoz, so a dashboard grouped by `service.instance.id` shows the old pods' series ending at the rollout. A site collector that already added `k8s.pod.name` sees the same value.
- The chart test refuses a workload that exports telemetry without naming its pod.

### ARV-274: Stream holds a partition's zones from the moment it takes the partition (2026-10-31)

- Behaviour: when an Ariva.Api.Stream replica takes a partition (a restart, a rollout, a rebalance), it restores every zone of that partition with a saved snapshot under its published profile version, without waiting for a record of the zone. A zone whose devices are all silent is idle-ticked, written at every checkpoint and counted by `ariva.stream.zones_unknown`, so `zonesUnknown` pages for zones that were already dark before the restart (wiki 10 section 3.1). Zones retired from the profile are not restored; zones saved under another version start afresh with their first record, as before.
- After the upgrade the first rollout restores the zones; nothing to configure or migrate. A replica now checkpoints on its interval while it holds zones, even before it has read a record.
- Bounds and log: the saved keys are listed once per assignment (at most 100,000 keys of at most 200 characters, command 10 s) on the worker's loop, not in the rebalance callback; zones are restored at most 50 and 5 seconds a pass, never beyond `Stream:MaxZones`, under the partition count read on that assignment; a database that does not answer is retried a minute later and given up after three attempts. One summary line says how many zones were restored and why the others were not (wiki 10 section 4.5b).

### ARV-259e5: the queue engine reads the lines devices declare (script 0067, zone snapshot version 15, 2026-10-30)

- Script 0067 adds `device_health_event.covered_lines` (a text array, at most 64 names; the migration job applies it, compressed chunks included). Every report archived before reads as All. Nothing to migrate.
- Ingest stamps every device health report with the device's declaration (`coveredLines` on `ariva.device.health.v1`, an additive member: a Stream or Main before ARV-259e5 ignores it, so the hosts roll out in any order). Roll out Ingest before relying on declarations: until it runs, reports carry none and every device counts every line.
- Behaviour: a device covers only the entry and exit lines it declares it counts (formulas F11). A zone whose devices declared their lines in ARV-259e4 can now turn `Unknown` while some of its devices are live: when the devices that count one of its lines are all out (an exit sensor beside a live snake sensor), or when no device counts a line at all. Before the upgrade, check the zones' sensing health on the devices screen: a line no commissioned device counts keeps its zone `Unknown` (and `zonesUnknown` paging) from the first report after the upgrade. A declared line a zone no longer holds is ignored and counted (`DeclaredLineNotInZone` on `ariva.stream.queue_refused`, wiki 10 section 4.22).
- Ariva.Api.Stream writes zone snapshot version 15 (each device's declaration). A version 14 snapshot restores as it is, every device counting every line until its next health report; a rollback to a build before ARV-259e5 refuses version 15 snapshots and starts the zones afresh, as for version 11 (below).
- Not in this release: a push answer naming events on lines its device does not declare (the owner's optional item, ARV-277).

### ARV-259e4: devices declare the lines they count (script 0066, 2026-10-30)

- Script 0066 adds `device.line_coverage` (`All` by default, so every existing device keeps counting every line of its zone) and `device.covered_lines` (the migration job applies it). Nothing to migrate.
- New endpoint `PUT api/v1/admin/devices/{id}/coverage` (EditDevice, not a critical action) and the devices screen's Lines it counts; the device answer carries `lineCoverage`, `coveredLines` and `linesNotInZone`, the health answer `uncoveredLines`, a publish answer `warnings`. A web app before ARV-259e4 ignores them.
- The queue engine reads declarations since ARV-259e5 (above); in a release with ARV-259e4 alone, declaring lines changes no wait, bin or alert. Site configuration export and import do not carry them (ARV-276).

### ARV-259e3: the zones Unknown gauge and a Critical rule, on by default; a tunable degraded zones count (2026-10-30)

- **Act after the release:** a new Critical rule, `zonesUnknown`, is on by default (the owner's decision of 2026-10-29): it pages when any queue zone of a site has had no measurement for 5 minutes (every device of the zone out; `ariva.stream.zones_unknown` by `site`, summed over the Stream replicas). Import the rules again into SigNoz after the upgrade (wiki 10 section 1.1), and check where the pages go (`monitoring.channels`). It pages for zones whose sensors are switched off on a schedule (a hall closed overnight): schedule a planned downtime for those hours in SigNoz, or turn the rule off (`monitoring.alerts.zonesUnknown.enabled: false`). After retiring a zone or moving its last sensor, roll the Stream deployment, or the zone pages until 24 hours after its devices were last heard (wiki 10 section 3.1). Right after this upgrade, zones already dark are not counted until a record of them arrives (since ARV-274, above, a replica restores them when it takes their partition).
- `zonesDegraded` fires above `monitoring.alerts.zonesDegraded.zones` degraded zones (0 by default, any, as before; a whole number from 0 to 10,000, refused otherwise, and refused on any other rule). Nothing changes unless it is set.
- Ariva.Api.Stream exports `ariva.stream.zones_unknown` (`Ariva.Stream`, by `site`). Nothing to configure.

### ARV-259e2: a zone without measurement is Not measured on the screens and boards (2026-10-29)

- The live snapshot in Redis and on the live hub carries `unknown` (the zone had no measurement in its minute, formulas F11); such a snapshot has no nowcast, throughput or no-service reason and both flags set, and one claiming `unknown` with any of them is refused on its way to a screen. A snapshot stored before the release reads as measured.
- The live operations screen, its floor plan (outlined dashed) and the immigration screen show such a zone as Not measured, in English and Arabic, with no queue length or wait, never within target and outside the passengers waiting tile and the zones reporting count; an immigration lane with such a zone shows Not measured. Display boards give its entry no nowcast, so the player shows the display's neutral message, and the `displaysStale` rule does not count the display while the snapshot is fresh.
- Ariva.Web and Ariva.Api.Main roll out or back in either order: the new web app reads a snapshot without the member (from an Api.Main before ARV-259e2) as measured, and a web app before ARV-259e2 ignores it and shows such a zone as Data degraded, without a nowcast. Nothing to migrate.

### ARV-259e1: a zone whose every device is out is Unknown (zone snapshot version 14, 2026-10-29)

- Ariva.Api.Stream writes zone snapshot version 14: the zone's entry and exit lines without a live covering device and the ended spells whose live minutes are not all published yet. A version 13 snapshot restores as it is, its spells derived from its devices; a rollback to a build before ARV-259e1 refuses version 14 snapshots and starts the zones afresh, as for version 11 (below).
- Behaviour: a zone whose every device is out (offline, or silent beyond 180 seconds) is `Unknown` where it was `Degraded` (formulas F11, the owner's decision of 2026-10-29): its bins are stored `Unknown` for the spell, so a single-sensor zone's bins over an outage read `Unknown` in reports, exports and the weekly summary's Unknown count. A zone with another live device stays `Degraded`. Bins made final before the release keep their revision; a recomputation over a range with such a spell writes revision r + 1 for them.
- The screens and boards follow in ARV-259e2, the gauge and the `zonesUnknown` rule in ARV-259e3 (both above).
- Nothing to configure: a device that declares no lines (ARV-259e4, read by the engine since ARV-259e5) covers all the lines of its zone.

### ARV-259d: the display stale rule (2026-10-29)

- New metric: `ariva.displays.stale` (meter `Ariva.Displays`, by `site`): the enabled displays whose board would be stale now (a queue zone's live snapshot missing or older than the display's stale threshold), from a sweep Api.Main runs every minute on one replica (lock class 54). No setting, no script and no grant.
- New rule: `displaysStale` (on by default, 5 minutes, Warning; wiki 10 section 4.8). Import the rules ConfigMap into SigNoz again after the upgrade (wiki 10 section 1.1). It does not model operating hours: a site whose zones publish nothing while it is closed and whose displays stay enabled raises it then; schedule a planned downtime in SigNoz, raise its minutes or turn it off there (wiki 10 section 3.1).

### ARV-259c: the retention rules (2026-10-28)

- New metrics (meter `Ariva.Retention`, Cronz): `ariva.retention.problems` by `outcome` (the last relational retention run: data sets failed, without a period, or the run failed as a whole) and `ariva.retention.policy_jobs_failed` by `kind` (TimescaleDB's retention, compression and refresh jobs whose last run failed), the latter from a new TickerQ function `TimescalePolicyJobs` every 10 minutes. No script and no grant: the runtime login reads `timescaledb_information.job_stats` as it is.
- New rules: `retentionFailed` (5 minutes) and `policyJobsFailed` (30 minutes), both on by default and Critical (wiki 10 section 4.8m). Import the rules ConfigMap into SigNoz again after the upgrade (wiki 10 section 1.1).

### ARV-259b: the bin maturity rule (2026-10-28)

- New metric: `ariva.stream.bins_overdue` (meter `Ariva.Stream`, by `site`): queue bins still provisional past their maturity, from a sweep Stream runs whenever `Stream:Enabled` is on (lock class 53; new settings `Stream:BinMaturitySweepSeconds`, 60, and `Stream:BinMaturityLookbackHours`, 24).
- New rule: `binsOverdue` (on by default, 15 minutes, Warning; wiki 10 section 4.5c). Import the rules ConfigMap into SigNoz again after the upgrade (wiki 10 section 1.1). The bins a zone left open when its snapshot was refused stay counted, and the rule firing, until they leave the look-back (about 26 hours at the defaults; nothing writes them Final today, wiki 10 section 4.5c); a zone that moved to a newer profile version is not counted for the bins of the old one, nor a zone taken out of the published profile.

### ARV-259a: the clock rule (2026-10-28)

- New metric: `ariva.devices.clock_unreliable` (meter `Ariva.Devices`, no tags): devices heard within the heartbeat timeout whose clock is unreliable.
- New rule: `clockUnreliable` (on by default, 15 minutes, Warning; wiki 10 section 4.5). Import the rules ConfigMap into SigNoz again after the upgrade (wiki 10 section 1.1).

### ARV-257 to ARV-259: per-site AMAN feed age, the queue engine's checkpoint age and new alert rules (2026-10-27)

- Script 0065 adds `desk_feed_state.heartbeat_utc` (the migration job applies it); Stream fills it at its next read of each site.
- New metrics: `ariva.border.feed_age_seconds` (by `site`, ARV-257) and `ariva.stream.checkpoint_age_seconds` (by `group`, ARV-258).
- Changed rule: `amanFeedStale` reads the per-site age and names the site; the chart refuses a window under 2 minutes.
- New rules: `streamCheckpointStale` (ARV-258), `devicesDegraded`, `zonesDegraded`, `aodbFeedStale`, `integrationLockout` and, off by default, `amanCodesUnmapped` (ARV-259). Border deployments turn on `amanFeedStale` and `amanCodesUnmapped` (wiki 04).
- Import the rules ConfigMap into SigNoz again after the upgrade (wiki 10 section 1.1).

### ARV-185: one lane rule for the staffing recommendation and the predicted nowcast, and the shared queue's demand (2026-10-26)

Changed (owner decisions of 2026-10-26): the predicted nowcast's arrival-wave projection takes a queue's lane from the queue zone's own lane category, as the immigration screen, the nowcast desk term and the staffing recommendation do, and the same arrivals hall as the recommendation (formulas F14 and F16). The shared lane `ALL` (one queue for every manual category) now has demand: the manual categories (CIT, RES, VIS, CRW) without a queue of their own at the site. No database script, setting or API change.

Known effects:

- A queue zone without a lane category has no projection, whatever desks its service zones stand at: set the lane of every arrivals queue in the zone profile (the Zones screen) before relying on its predicted-nowcast rules. A queue whose own lane differs from its desks' categories now takes its own (a citizens queue at desks coded CIT and RES takes CIT only).
- Seeded sites: DMO's arrivals queues (A-CIT, A-RES, A-VIS, A-CRW, A-EG and the band A-OV) now have a projection (DMO has no service zones, so before they had none) and their predicted-nowcast rules judge; NBJ-BC1's arrivals hall queue `ALL` and its band now take every manual category's arrivals (the projection was zero) and its staffing recommendation has arrivals (it read `NoLaneDemand`); AUH-TA is unchanged.
- The staffing recommendation's open-now wait is checked again as read: a stored row with a recommendation and desks open now known but no open-now wait (which the engine never writes) reads `Stale`.

### ARV-236: TimescaleDB 2.31 and the third-party pins moved to their newest patches (2026-10-23)

- The TimescaleDB chart runs `timescale/timescaledb-ha:pg17-ts2.31` (PostgreSQL 17.11 as before, TimescaleDB 2.31.0, toolkit 1.26.0), where it ran `pg17-ts2.30` (TimescaleDB 2.30.2 and toolkit 1.25.0 on a database created under it). The chart's `appVersion` moves with it. The database pod restarts once on the release, as on any change to its image; the hosts reconnect, and the database keeps working on its old extension versions.
- **Act after the release** (wiki 04 section 6.8): scale the Ariva deployments to 0, run `node scripts/ops/ariva-ops.mjs update-extensions --namespace <ns>`, then `node scripts/ops/ariva-ops.mjs check --namespace <ns> --runtime-login <login>` (its `extension versions` line passes once both extensions are at the image's versions), then apply the release again with Helmfile to start the platform. A minute or two without the platform. The update refuses to start while a session of an Ariva login is connected, and before any statement as the superuser it also refuses functions and operators under the system's or TimescaleDB's names, another extension, and settings made for every session of the database.
- **Superuser, pending the owner:** the update runs as the database superuser over the pod's socket (only the extension's owner may update it; the migration login is not a superuser, so the migration job cannot). That is a superuser use beyond the owner's 2026-10-15 decision on the restore's steps, proposed and pending the owner (docs/product/decisions.md, 2026-10-23; the residual in wiki 04 section 6.8). Running the two statements by hand instead is the same pending use without the refusals (wiki 04 section 6.8 says how, if a database administrator does it).
- **Rollback:** before the update, rolling the database image back is safe. After it, the database's TimescaleDB is 2.31.0, which `pg17-ts2.30` cannot load: roll back the Ariva hosts if needed, not the database image (reasoned from the images, not tried).
- **Backups:** a backup restores only through its own TimescaleDB version. Take a new backup after the update. One taken before it (2.30.2) was to be restored by hand with the extensions created at its versions first; since ARV-244 the restore does this itself and nothing is done by hand (section 6, ARV-244; wiki 04 section 10.4).
- With your own PostgreSQL: the database administrator updates the extensions, with the hosts stopped (wiki 04 section 6.8).
- Development and test images, nothing to act on in a cluster: Kafka 4.1.2 (docker-compose, CI, the ZAP workflow), confluent-local 8.2.4 (the AppHost), cp-kafka 7.6.14 (integration tests), and new digests for `redis:8.2-alpine`, `redis:8.6` (the AppHost) and ZAP's `stable`. A developer's existing TimescaleDB volume keeps 2.30.2 until updated (wiki 04 section 6.8; a new volume needs nothing).

### ARV-239: position samples ahead of the clock coalesced, and a share of the ahead tenth per device (zone snapshot version 13, 2026-10-22)

- Ariva.Api.Stream writes zone snapshot version 13: a run of position samples may now hold samples ahead of the receive clock (its anchor in the buffer, its pending sample marked `pendingAhead`). A version 12 snapshot restores as it is (its samples ahead of the clock all in its buffer); a rollback to a build before ARV-239 refuses version 13 snapshots and starts the zones afresh, as for version 11 (below).
- Behaviour: a sensor whose clock runs up to Ingest's 500 ms tolerance fast no longer fills the tenth of the buffer kept for events ahead of the clock (a hall zone of 20 such sensors kept 766 of 1,199 entries before, every one now), and each device holds at most its share of that tenth (the tenth over the devices sharing the buffer, never below 1,000), so a device whose clock runs minutes ahead is refused alone (`DeviceShareFull`) instead of having the zone refuse every event ahead of the clock (`BufferFull`; wiki 10 section 4.22).
- Nothing to configure: the share's floor is a tenth of `DeviceShareFloor`.

### ARV-231: queue engine refusals visible, and the buffer's share per device (zone snapshot version 12, 2026-10-22)

- Ariva.Api.Stream writes zone snapshot version 12: the device of each anonymous event the queue engine holds (a tracked one's key names it), the step's new `DeviceShareFull` count and the minutes a full buffer touched. A version 11 snapshot restores as it is (its tracked events counted for their devices, its anonymous ones for none); a rollback to a build before ARV-231 refuses version 12 snapshots and starts the zones afresh, as for version 11 (below).
- New: the counter `ariva.stream.queue_refused` (kind, site), a warning per zone and kind at most once a minute, and the SigNoz rule `queueRefused` (`monitoring.alerts.queueRefused`, critical on `BufferFull` and `DeviceShareFull`); import the rules ConfigMap into SigNoz again after the upgrade (wiki 10 section 1). The live minutes a full buffer touched are written with `length_degraded` and `nowcast_degraded` set.
- Behaviour: each device holds at most its share of a zone's 100,000-event buffer (the bound over the devices sharing it: those holding events, the zone's devices heard recently, and at least two, so never more than half; at least 10,000). Past its share, events that were due anyway are applied to make room; if that frees nothing the device's event is refused alone (`DeviceShareFull`) where before the whole zone refused events of every kind (wiki 10 section 4.22). A single-device zone now holds at most half the buffer for that device.

### ARV-230: the queue engine keeps track positions in runs (zone snapshot version 11, 2026-10-22)

- Ariva.Api.Stream writes each zone's state as snapshot version 11: the queue engine's runs of position samples (a track's anchor and one pending sample) travel in it, and its reorder buffer no longer holds one event a position. A hall zone's `stream_zone_state.state_bytes` falls from about 20 MB to a few hundred KB (wiki 05).
- Upgrading needs nothing: Stream restores a version 10 snapshot as it is (its held positions all in the buffer) and the runs start with the next positions.
- Rolling back to a build before ARV-230 is the case to plan for: that build refuses version 11 snapshots, so each zone starts afresh with its next events and logs an error ("The saved state of zone ... cannot be restored; the zone starts afresh"): its open entrants and the events it held are lost, as with any earlier version change, and the minutes already stored stay. Roll back only between peaks. For about one queue dwell time after any zone starts afresh (a rollback, a refused snapshot, a new profile version, go-live), exits of people who were already inside pair by FIFO with the earliest person another sensor of the zone holds, whose own exit then counts as a duplicate (formulas F5): exits are undercounted and those waits come out short until the people inside at the restart have left (77 of 252 exits a hall zone in the replay of ARV-230's first pilot run; ARV-237).
- Nothing to configure: the bound on runs (`MaxSightingRuns`, 20,000 a zone) is the engine's own, like its buffer's.

### ARV-092: download names, charset and the file handling guide (2026-10-19)

Changed in the hosts:
- Stored uploads go back under a name the server builds, never the uploaded one (`Ariva.Api.Common/Security/StoredFileHeaders.cs`): floor plan content (`GET api/v1/admin/levels/{levelId}/floor-plan/content`) sends `Content-Disposition: inline; filename="floor-plan-{levelId}.{png, jpg or svg}"`, and advertisement content, in the library preview (`GET api/v1/admin/sites/{siteCode}/advertising/media/{id}/content`) and the display player's fetch (`GET api/v1/display/advertising/{id}`), `inline; filename="advertisement-{id}.{png, jpg, webp, mp4 or webm}"`, where both sent `inline` without a name before. The extension is that of the type read at upload. The types, content security policies, `nosniff`, the floor plan's `ETag` and the advertisements' cache and resource policy headers are unchanged.
- SVG floor plans go out as `image/svg+xml; charset=utf-8`.
- Every text answer of the five API hosts names its encoding: a `text/*`, `application/xml`, `application/javascript` or `+xml` answer without a charset gets `charset=utf-8` (`SecurityHeadersMiddleware`), which reaches the framework's plain-text status pages and the TickerQ dashboard's page and files. JSON, binary answers and answers that already name a charset are unchanged.

Changed in the web image:
- nginx names UTF-8 on the app's page, `/healthz`, scripts, styles, JSON and SVG files (`charset utf-8` and `charset_types` in `static/nginx/default.conf`; the web build fails without them).
- Fixed: the image served its own nginx configuration at `GET /nginx/default.conf`, because it copied the build's `nginx` folder into the web root. The Dockerfile now removes that folder from the web root, and the path gets the app's fallback page. The file holds the listener, the content security policy and the security headers, no credential.

New page: [File handling](13a-File-Handling.md), every upload with who may send it, its types and limits, what Ariva does to it, where it is stored, how it is served, its rate limit and audit, and what happens to a malicious file (ASVS V5.1.1). Malware scanning stays open (ARV-092b, ARV-092c).

Not changed: the daily report CSV download (already an attachment under a server-built name, `text/csv; charset=utf-8`), the imports' JSON answers, and the site configuration export, a JSON answer without `Content-Disposition`. No database script, endpoint or permission changes.

Upgrade steps: none. A browser that saves a floor plan or an advertisement now proposes the server's name.

Rollback notes: rolling the images back restores the nameless `inline` answers and the charset-less text answers, and the web image serves its configuration at `/nginx/default.conf` again.

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
4. Until a listed rule is repaired or deleted: it is not evaluated (it raises nothing); its open alerts are resolved as withdrawn at the first evaluation tick; Ariva.Api.Stream logs one warning per rule ("not evaluated, it fails the rule checks of its border metric", wiki 10) and, since ARV-190, writes one `AlertRule.NotEvaluated` audit entry with it; the alert rules screen marks it "Not evaluated: fix its border settings"; and the site's configuration export answers 409 (an alert rule fails the checks an import applies).

Known effects:

- The terminal duty manager no longer finds its own predicted-nowcast rules (404) and no longer sees predicted alerts, historic ones included: they leave its alerts list, its daily report and CSV, scheduled report deliveries rendered after the upgrade and the weekly summary. Reports and emails already delivered cannot be recalled.
- Alert emails about predicted alerts still pending for airport-side people at the upgrade are not sent (recorded as held back).
- A site configuration bundle exported before the upgrade that holds a predicted-nowcast rule without a border owner no longer imports (the alert rules part is refused); repair the rule at the source and export again.

Rollback notes: rolling the chart back alone restores the previous behaviour (there is no script to undo), which re-opens the channel the change closes (the duty manager could again create and backtest predicted rules and receive their alerts); rules repaired or deleted in the meantime stay as they are.
