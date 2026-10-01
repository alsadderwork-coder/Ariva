# Ariva

Ariva is Dalil Tech's airport queue management system. It measures queues at border control, e-gates, check-in and security with ceiling sensors, combines them with AODB flight data and aggregate feeds from AMAN, and gives control rooms live wait times, desk state, SLA alerts and forecasts. Two modules are sold separately: Border (for the border authority) and Airport Operations (for the airport operator and ground handlers).

Ariva is a separate product and codebase from AMAN. It mirrors AMAN's layout and conventions and integrates with AMAN only through the aggregate-only contracts in `Ariva.Business.Contracts`.

Start with [CLAUDE.md](CLAUDE.md) for working conventions, then [docs/](docs/) for the architecture, ADRs and domain glossary.

## Layout

```
Ariva/
  Ariva.slnx
  Directory.Build.props          shared MSBuild settings (net10.0, C# 14)
  Directory.Packages.props       central package versions, curated from AMAN
  docs/                          architecture, ADRs, domain glossary
  backlog/                       stories
  wiki/                          how-tos and runbooks
  deploy/                        environment specific deployment material
  Platform/
    Backplane/
      Ariva.Utilities            shared helpers, no Ariva dependencies
      Ariva.Core                 domain model and service interfaces
      Ariva.Resources            language files and templates
      Ariva.Infra                service implementations, NHibernate, Timescale scripts, Kafka
      Ariva.Di                   dependency registration (RegisterArivaServices)
      Ariva.Api.Common           shared API plumbing, layered appsettings, health endpoints
      Ariva.Api.Main             configuration and operations REST API, SignalR live hub
      Ariva.Api.Ingest           sensor adapters: vendor webhooks and pollers into Kafka
      Ariva.Api.Stream           Kafka stream workers that compute queue state
      Ariva.Api.Cronz            TickerQ jobs: forecasts, SLA evaluation, reports, retention
      Ariva.Api.Integration      AODB (AIDX, ACRIS, SSIM), AMAN feed consumer, email
      Ariva.UnitTests            unit and architecture tests (xUnit v3)
      Ariva.IntegrationTests     integration tests (Testcontainers, added by the backlog)
    Business/
      Ariva.Business.Contracts   AMAN feed contracts V1, shipped to AMAN as a package
    Frontplane/
      Ariva.Web                  SvelteKit 2, Svelte 5, Tailwind 4 single page app
    Simulation/
      Ariva.Simulation.Api       sensor, AODB and AMAN emulators; seeded reference day
    Testing/
      Ariva.E2E                  API end-to-end and Playwright functional tests (Node, TypeScript)
    Cloud/
      Ariva.K8s                  Helm chart (platform, timescaledb) and helmfile
      Ariva.Cicd                 Azure DevOps pipelines (optional CD to Dalil Container Registry; CI runs on GitHub Actions)
```

## Ports

- Ariva.Api.Main: 51001
- Ariva.Api.Ingest: 51002
- Ariva.Api.Stream: 51003 (health only)
- Ariva.Api.Cronz: 51004
- Ariva.Api.Integration: 51005
- Ariva.Web: 51010 (vite dev), 51011 (vite preview)
- Ariva.Simulation.Api: 51020

In containers every .NET host listens on 8080 and the web image (nginx) on 3000. Every host answers `/health/startup`, `/health/readiness` and `/health/liveness`.

## Build and run

Prerequisites: .NET 10 SDK, Node 22, Docker Desktop or Rancher Desktop for the local dependencies:

```
node scripts/dev-up.mjs     # TimescaleDB, Kafka (KRaft), Redis, smtp4dev; creates .env and appsettings.local.json
node scripts/dev-down.mjs   # stop (add --volumes to delete the data)
```


```
dotnet restore Ariva.slnx
dotnet build Ariva.slnx
dotnet test Platform/Backplane/Ariva.UnitTests/Ariva.UnitTests.csproj
```

Run a host from its folder, for example:

```
cd Platform/Backplane/Ariva.Api.Main
dotnet run
```

Run the web app:

```
cd Platform/Frontplane/Ariva.Web
npm install
npm run dev
```

`npm run check` type checks the app and `npm run build` produces the static site in `build/`, with the content security policy written into `build/nginx/default.conf`.

Run the API end-to-end and functional tests (they build and start the hosts and the web preview themselves; see [Platform/Testing/Ariva.E2E/README.md](Platform/Testing/Ariva.E2E/README.md)):

```
node scripts/verify.mjs e2e
```

## Configuration

Each host loads, in order: `appsettings.base.json`, `appsettings.base.<env>.json` (both linked from Ariva.Api.Common), `appsettings.service.json`, `appsettings.service.<env>.json`, `appsettings.local.json` (local development only, written by `scripts/dev-up.mjs`, git-ignored), then environment variables. The environment comes from `DOTNET_ENVIRONMENT`, or from `environment.json` when that is not set. Environments: `vm-local`, `k8s-dev`, `k8s-demo`, `k8s-prd`. In Kubernetes the `k8s-*` files are replaced by secrets written by the release pipeline, so real credentials never live in the repository.

## Working with agents

This repository is built with Claude Code under a harness: `CLAUDE.md` (rules), `.claude/` (subagents, commands, skills, hooks), `.mcp.json` (documentation and tooling servers), `backlog/` (ralph-tui stories) and `scripts/verify.mjs` (gates). Start with `docs/harness/README.md` and `docs/plan/implementation-plan.md`. The wiki for operators and integrators is in `wiki/`.
