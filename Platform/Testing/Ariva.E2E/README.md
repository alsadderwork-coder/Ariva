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
| `tests/functional/shell.spec.ts` | The shell loads with the Ariva title and no console errors or CSP violations; the page response carries the production CSP and security headers; switching to Arabic sets `<html lang="ar" dir="rtl">`; the shell refuses to be framed. |
| `tests/functional/xss.spec.ts` | Script payloads in the query string, the hash and the path never open a dialog, are never injected as markup and never trip the CSP. |
| `tests/support/` | Host URLs, ProblemDetails and header assertions, attack payloads, and the page guard that records console errors, dialogs and `securitypolicyviolation` events. |

### Behaviour worth knowing

- Default deny answers before routing. Until the authentication story replaces the `Ariva.Deny` placeholder scheme, an anonymous caller gets 401 (with a ProblemDetails body) for unknown routes, wrong methods and TRACE as well, so routes cannot be enumerated. The genuine 404, 405 and 500 bodies are covered in `Ariva.UnitTests` (`ErrorHandlingTests`) with an authenticated test scheme.
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
| `CI` | not set | When set: no server reuse, `forbidOnly`, one retry, two workers. |

## In CI

`Platform/Cloud/Ariva.Cicd/AzureDevOps/Common/Analyze-solution.yaml` runs, after the security gate, the .NET build, the unit tests and the web checks: `npm ci`, `npx playwright install --with-deps chromium` and `npx playwright test` in this folder with `CI=true` and `ARIVA_E2E_CONFIGURATION=Release`. The Playwright HTML report (`playwright-report/`) and `.verify/security-report.md` are published as build artifacts, also when a step fails.
