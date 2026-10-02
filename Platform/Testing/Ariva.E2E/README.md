# Ariva.E2E

API end-to-end tests and Playwright functional tests for Ariva. They run against real processes: the .NET hosts started with `dotnet run` and the production build of Ariva.Web served by `vite preview` with the same security headers nginx sends. They are layer 5 (behaviour tests) of the security gates in [docs/security/cwe-controls.md](../../../docs/security/cwe-controls.md).

## Projects

| Playwright project | Folder | What it drives |
|---|---|---|
| `api` | `tests/api` | Playwright's `request` fixture against Ariva.Api.Main (51001), Ariva.Api.Ingest (51002), Ariva.Api.Integration (51005) and Ariva.Simulation.Api (51020). No browser. |
| `functional` | `tests/functional` | Chromium against Ariva.Web on the `vite preview` port (51011). |

`playwright.config.ts` starts everything the suites need through `webServer` entries, one after another, waiting for each health URL: the first entry runs `scripts/build-backend.mjs` (one `dotnet build` per host) and then each host runs with `dotnet run --no-build` on its launchSettings port; the last entry runs `npm run build && npm run preview` in `Platform/Frontplane/Ariva.Web`. Outside CI an already running host or preview server is reused (`reuseExistingServer`); when you reuse Ariva.Api.Main, build the other hosts yourself.

## Suites

| File | Covers |
|---|---|
| `tests/api/health.spec.ts` | The three Kubernetes probes answer 200 anonymously on every running host, with the API security headers. |
| `tests/api/security-baseline.spec.ts` | Default deny (CWE-862, CWE-306): 401 on `/api/v1/system/info` without a token, with a malformed or unsigned bearer token and with `?access_token=`; security headers and no `Server` header; unknown routes, wrong methods and TRACE answer ProblemDetails without stack traces or framework names; CORS gives a foreign origin no `Access-Control-Allow-Origin` and the web origin an exact match; request limits (CWE-120); SQL injection and XSS payloads in query strings never cause a 5xx or reflected markup. |
| `tests/api/auth.spec.ts` | Sign-in (ARV-010a): ES256 `at+jwt` tokens that are never cached and are accepted by every host; one answer for wrong password, unknown and malformed users; 429 on the eleventh attempt a minute from one address; lockout after 10 failures and its expiry; administrator unlock; the pending scope of a temporary password and the password change out of it; refusal of `alg none`, HS256 with the public key, another audience, issuer or `typ`, expired tokens, unknown keys and `?access_token=`. |
| `tests/api/sessions.spec.ts` | Sessions (ARV-010b): the refresh cookie's attributes and that it never reaches a body; 403 without `X-Ariva-Csrf` or from a foreign Origin; rotation, the same successor once within the grace window, family revocation on reuse within and after it, two tabs refreshing together; a new sid at every sign-in and revocation of a cookie sent with it; logout and disable refused on every host within 5 seconds. Idle and absolute expiry need a movable clock and are in `Ariva.IntegrationTests` (`SessionTests`). |
| `tests/api/totp.spec.ts` | TOTP (ARV-010c), serial: 401 `mfa_required` without a code and never after a wrong password; a replayed code refused and the next step accepted; enrolment shown once, a wrong first code refused, recovery codes single use and regenerated; the break-glass account from the installer command signs in with a recovery code after twelve failures and logs the critical event. |
| `tests/api/step-up.spec.ts` | Step-up (ARV-010d), serial: every route in `security/critical-actions.json` answers 401 `insufficient_user_authentication` with `max_age=900` and `mfa_required` for a password-only token and for one whose second factor is 16 minutes old (re-signed with the run key), passes within 15 minutes, and is reached after `POST /api/auth/step-up` in the same session; a non-critical endpoint never asks; a code steps up once. |
| `tests/api/administration.spec.ts` | User, role and audit administration (ARV-011), serial, as `e2e.secadmin` signed in with TOTP: create with a one-time temporary password (pending scope), duplicate 409, markup and SQL as usernames 400; self-grant, self-revoke, own TOTP reset and grants or creation by a supervisor return 403; grant, revoke and both resets succeed and appear newest first in the audit trail without the temporary password; no method changes an audit entry; search filters and refuses unknown sort fields and oversized pages. |
| `tests/api/sites.spec.ts` | Site scoping (ARV-012, CWE-863): each account lists only its seeded sites; another site's code, an unknown code, a lower case code and an injection attempt all answer 404 without data; renaming another site is 403; site access changes need step-up, stay within the granter's sites, apply at once when widened and end the sessions when narrowed (own accounts `e2e.siteadmin` and `e2e.siteuser`). |
| `tests/api/topology.spec.ts` | Topology administration (ARV-014), serial: builds an airport with terminals in E2E1 and E2E2, a level, a checkpoint and desks; duplicate 409, wrong desk kind and numeric kinds 400, delete with children 409; every entity reads, updates, searches and soft deletes, a deleted code is free again, and desk changes are audited; another site's records answer 404 and are not listed, supervisors cannot write; injection in codes, search text and sort fields is refused or matches nothing, and markup in names is stored as inert JSON text. Ranges (ARV-015) create all codes or none and stop at 200; external AMAN and AODB codes map to one desk per system and site, follow the desk kind, stay in their site and can be re-pointed or freed. |
| `tests/api/permission-matrix.spec.ts` | `security/permission-matrix.json`: the anonymous column without a token, and each role column with a real token for that role. |
| `tests/functional/shell.spec.ts` | The shell loads with the Ariva title and no console errors or CSP violations; the page response carries the production CSP and security headers; switching to Arabic sets `<html lang="ar" dir="rtl">`; the shell refuses to be framed. |
| `tests/functional/xss.spec.ts` | Script payloads in the query string, the hash and the path never open a dialog, are never injected as markup and never trip the CSP. |
| `tests/support/` | Host URLs, ProblemDetails and header assertions, attack payloads, and the page guard that records console errors, dialogs and `securitypolicyviolation` events. |

### Behaviour worth knowing

- Default deny answers before routing. An anonymous caller (no token or an invalid one) gets 401 (with a ProblemDetails body) for unknown routes, wrong methods and TRACE as well, so routes cannot be enumerated. The genuine 404, 405 and 500 bodies are covered in `Ariva.UnitTests` (`ErrorHandlingTests`) with an authenticated test scheme.
- A 2 MB body sent to a protected route is answered with 401 before Kestrel reads it: authorization runs before model binding, and a client that sends `Expect: 100-continue` never uploads the body. Kestrel's 1 MB limit (413) applies once an endpoint reads a body. Request lines over 8 KB get 414 and header blocks over 32 KB get 431 from Kestrel.
- The web CSP allows SvelteKit's inline bootstrap script by hash (`kit.csp` in hash mode) and SvelteKit's route announcer style attribute by hash (`style-src-attr 'unsafe-hashes'`); there is no `'unsafe-inline'`. See `Platform/Frontplane/Ariva.Web/csp.config.js`.

## Run locally

Prerequisites: .NET 10 SDK, Node 22, and `npm ci` in `Platform/Frontplane/Ariva.Web` (the verify script does it when `node_modules` is missing).

```
cd Platform/Testing/Ariva.E2E
npm ci
npx playwright install chromium
npm test                    # both projects
npm run test:api            # API end-to-end only
npm run test:functional     # browser tests only
npm run report              # open the HTML report
```

From the repository root, `node scripts/verify.mjs e2e` installs missing dependencies and runs both projects.

Environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `ARIVA_E2E_CONFIGURATION` | `Debug` | Build configuration for `dotnet build` and `dotnet run`. |
| `ARIVA_E2E_CHROMIUM` | not set | Path to an installed Chromium to use instead of the build that matches this Playwright version, for machines that cannot run `npx playwright install`. |
| `ARIVA_E2E_MAIN_URL`, `ARIVA_E2E_INGEST_URL`, `ARIVA_E2E_INTEGRATION_URL`, `ARIVA_E2E_SIMULATION_URL`, `ARIVA_E2E_WEB_URL` | the local ports | Point the suites at another deployment. |
| `ARIVA_E2E_SCHEMA_UPDATE` | not set | `true` when PostgreSQL is up (CI, or `npm run dev:up` locally): the hosts migrate it at startup and the sign-in suites run. Without it they are skipped; in CI its absence fails the run. The hosts read `Database__*` from the environment (CI sets a separate migration and runtime login). |
| `ARIVA_E2E_REDIS_URL` | not set | `redis://[:password@]host:port` of a Redis for the live hub (ARV-035): Ariva.Api.Main uses it as its cache, SignalR backplane and live snapshot store, and `live-hub.spec.ts` publishes a snapshot there as Ariva.Api.Stream would. Without it the snapshot test skips. CI starts `redis:8.2-alpine` on 6380. `ARIVA_E2E_REDIS_INSTANCE` overrides the key prefix (`ariva:`). |
| `ARIVA_E2E_KAFKA_BOOTSTRAP` | not set | `host:port` of a single Kafka broker. Ariva.Api.Ingest provisions its topics there (one partition, replication 1) and answers sensor pushes 202 only once their events are in Kafka, so the accepted-push tests of `device-push`, `device-health` and `sensor-emulator` need it and skip without it. CI starts `apache/kafka:4.1.0` on 19092 and fails the run without it. |
| `ARIVA_E2E_KEY_DIR` | `.e2e-keys/` (git-ignored) | Where the hosts keep this run's development token key; `auth.spec.ts` reads it to sign tampered tokens. |
| `CI` | not set | When set: no server reuse, `forbidOnly`, one retry, two workers. |

The E2E accounts (`e2e.admin`, `e2e.border`, `e2e.terminal`, `e2e.handler`, `e2e.pending`, `e2e.changer`, `e2e.lockout`, `e2e.unlock`) are created by Ariva.Api.Main from `Auth__DevelopmentUsers__*`, which `playwright.config.ts` derives from a random seed per run; no password is committed, and the log scan in `global-teardown.ts` fails the run if any of them reaches a host log. `tests/support/global-setup.ts` runs `Ariva.Api.Main --create-break-glass` (or `--rotate-break-glass` on a reused database) once the hosts are up and writes the credential to the git-ignored `.e2e-keys/break-glass.txt`. The hosts run with `Auth__TotpRequired=false`, because the general accounts sign in many times a minute in parallel and the TOTP replay guard would refuse that; `e2e.totp` (seeded with a known secret) and `e2e.enrol` test TOTP itself, `e2e.stepup` (also seeded) tests step-up, `e2e.secadmin` (a SystemAdministrator with TOTP) runs the administration tests, and `e2e.siteadmin` with `e2e.siteuser` run the site access tests. Accounts carry site bindings (`Auth__DevelopmentUsers__N__Sites__M`, `*` for every site): border E2E1, terminal E2E2, handler both, the administrators every site; the seed creates the sites. Sign-in, refresh and logout calls go through Node's `fetch` (`call()` in `tests/support/accounts.ts`) rather than Playwright's request context, whose cookie jar would keep the refresh cookie and send it with the next sign-in, which revokes that session. The hosts trust loopback as a proxy in this run only, so each test sets its own `X-Forwarded-For` and the per-address sign-in limit applies where a test means it to.

## In CI

`Platform/Cloud/Ariva.Cicd/AzureDevOps/Common/Analyze-solution.yaml` runs, after the security gate, the .NET build, the unit tests and the web checks: `npm ci`, `npx playwright install --with-deps chromium` and `npx playwright test` in this folder with `CI=true` and `ARIVA_E2E_CONFIGURATION=Release`, against a PostgreSQL container (`timescale/timescaledb-ha:pg17-ts2.30`) started by the pipeline; `.github/workflows/ci.yml` does the same with a service container and posts the failing tests on the pull request. The Playwright HTML report (`playwright-report/`) and `.verify/security-report.md` are published as build artifacts, also when a step fails.
