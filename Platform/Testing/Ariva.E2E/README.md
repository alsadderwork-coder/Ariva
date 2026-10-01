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
| `ARIVA_E2E_KEY_DIR` | `.e2e-keys/` (git-ignored) | Where the hosts keep this run's development token key; `auth.spec.ts` reads it to sign tampered tokens. |
| `CI` | not set | When set: no server reuse, `forbidOnly`, one retry, two workers. |

The E2E accounts (`e2e.admin`, `e2e.border`, `e2e.terminal`, `e2e.handler`, `e2e.pending`, `e2e.changer`, `e2e.lockout`, `e2e.unlock`) are created by Ariva.Api.Main from `Auth__DevelopmentUsers__*`, which `playwright.config.ts` derives from a random seed per run; no password is committed, and the log scan in `global-teardown.ts` fails the run if any of them reaches a host log. The hosts trust loopback as a proxy in this run only, so each test sets its own `X-Forwarded-For` and the per-address sign-in limit applies where a test means it to.

## In CI

`Platform/Cloud/Ariva.Cicd/AzureDevOps/Common/Analyze-solution.yaml` runs, after the security gate, the .NET build, the unit tests and the web checks: `npm ci`, `npx playwright install --with-deps chromium` and `npx playwright test` in this folder with `CI=true` and `ARIVA_E2E_CONFIGURATION=Release`, against a PostgreSQL container (`timescale/timescaledb-ha:pg17-ts2.30`) started by the pipeline; `.github/workflows/ci.yml` does the same with a service container and posts the failing tests on the pull request. The Playwright HTML report (`playwright-report/`) and `.verify/security-report.md` are published as build artifacts, also when a step fails.
