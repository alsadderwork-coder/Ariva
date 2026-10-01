# ADR-0016: Repository layout mirrors AMAN and inherits AMAN's code conventions

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01

## Context

The developer and the architect know AMAN's layout and patterns, and Claude Code already works in that codebase. D5 asked for shared packages and reuse of Aman.Web components and skills. D5 described each bounded context as "a Clean Architecture service or module with its own schema".

## Decision

The repository mirrors AMAN's layout:

| Area | Projects |
|---|---|
| `Platform/Backplane` | Ariva.Utilities, Ariva.Core, Ariva.Resources, Ariva.Infra, Ariva.Di, Ariva.Api.Common; hosts Ariva.Api.Main (51001), Ariva.Api.Ingest (51002), Ariva.Api.Stream (51003), Ariva.Api.Cronz (51004), Ariva.Api.Integration (51005); tests Ariva.UnitTests, Ariva.IntegrationTests |
| `Platform/Business` | Ariva.Business.Contracts (AMAN feed contracts V1) |
| `Platform/Frontplane` | Ariva.Web (51010) |
| `Platform/Simulation` | Ariva.Simulation.Api (51020) |
| `Platform/Cloud` | Ariva.K8s (Helm and Helmfile), Ariva.Cicd (Azure DevOps YAML) |

AMAN conventions are inherited: Onion architecture (Ariva.Core holds domain and service interfaces, Ariva.Infra holds implementations); `IStorageProvider`, `IUnitOfWork` and `SvcBase`; `Result<T>` and `Fx.Specification` from FluentX; controllers at `Controllers/AdminArea/<Entity>/Controller.cs`; the `Permission` attribute; FusionCache with Redis; Serilog; xUnit v3, Moq, FluentAssertions and Bogus; layered appsettings in Ariva.Api.Common. Bounded contexts are modules (namespaces and folders) inside the shared projects, each with its own database schema, rather than separate services.

## Consequences

- AMAN developers and Claude Code are productive from the first sprint; ADRs and docs can point at AMAN patterns.
- Weaknesses in AMAN's patterns propagate; fixes should flow both ways.
- Context boundaries are enforced by namespaces, schemas and tests, not by process boundaries. Architecture tests should block cross-context references outside published events and interfaces (Proposed).
- No compile-time dependency on AMAN projects; only shared infrastructure packages, if any.

## Alternatives considered

- One service per bounded context. Rejected: too many deployables for a team of two.
- A fresh template unrelated to AMAN. Rejected: loses the team's existing patterns and tooling.
