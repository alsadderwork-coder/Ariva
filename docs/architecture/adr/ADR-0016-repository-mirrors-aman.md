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
| `Platform/Cloud` | Ariva.K8s (Helm and Helmfile), Ariva.Cicd (Azure DevOps YAML, kept for Dalil Container Registry deployments) |

AMAN conventions are inherited: Onion architecture (Ariva.Core holds domain and service interfaces, Ariva.Infra holds implementations); `IStorageProvider`, `IUnitOfWork` and `SvcBase`; `Result<T>` and `Fx.Specification` from FluentX; controllers at `Controllers/AdminArea/<Entity>/Controller.cs`; the `Permission` attribute; FusionCache with Redis; Serilog; xUnit v3, Moq, FluentAssertions and Bogus; layered appsettings in Ariva.Api.Common. Bounded contexts are modules (namespaces and folders) inside the shared projects, each with its own database schema, rather than separate services.

## Consequences

- AMAN developers and Claude Code are productive from the first sprint; ADRs and docs can point at AMAN patterns.
- Weaknesses in AMAN's patterns propagate; fixes should flow both ways.
- Context boundaries are enforced by namespaces, schemas and tests, not by process boundaries. Architecture tests should block cross-context references outside published events and interfaces (Proposed).
- No compile-time dependency on AMAN projects; only shared infrastructure packages, if any.

## Alternatives considered

- One service per bounded context. Rejected: too many deployables for a team of two.
- A fresh template unrelated to AMAN. Rejected: loses the team's existing patterns and tooling.

## Amendment 2026-10-01: hosting

The repository is hosted on GitHub (`github.com/alsadderwork-coder/Ariva`, private), not in Azure DevOps like AMAN. Continuous integration runs on GitHub Actions (`.github/workflows`), dependency updates come from Dependabot, and the backlog is mirrored to GitHub issues. The folder structure, project names and conventions still mirror AMAN; the Azure DevOps pipelines remain for deployments that publish to Dalil Container Registry.

## Amendment 2026-10-15: visibility

The repository is public: the owner confirmed on 2026-10-15 that this is intended (the amendment of 2026-10-01 said private). Everything committed is published, so client material and credentials stay out of the repository. Being public makes rulesets on `main`, environments with required reviewers, CodeQL and secret scanning available on GitHub Free (docs/harness/README.md). The workflows were checked for a public repository on the same day: the `@claude` comment job runs only for owners, members and collaborators, pull requests from forks get no secrets, and the only workflow that may use a self-hosted runner (`release-dev.yml`) runs only on a manual dispatch.

## Amendment 2026-11-06: the move to Azure DevOps (Proposed)

The owner decided on 2026-11-06 that the repository and its pipelines move to Azure DevOps (docs/plan/azure-devops-move.md, decision 1, Option B), as AMAN's are: Azure Repos becomes the code's home and Azure Pipelines the gate and the delivery, building images into Dalil Container Registry and releasing to Dalil's Azure. The amendment of 2026-10-01 (GitHub, GitHub Actions, Dependabot, issues mirrored to GitHub) then describes the state before the cut-over. Until the Azure pipelines reach the GitHub ones' level (ARV-217 to ARV-222) GitHub stays the working home; the cut-over (ARV-223) freezes pushes to GitHub, checks a last sync commit by commit, and archives GitHub or keeps it as a read-only mirror by the owner's decision. Images and the offline bundle are signed with a cosign key held in Azure Key Vault (decision 2), replacing the keyless signatures GitHub's OIDC identity gave. After the cut-over, agents work in local Claude Code sessions on a clone of Azure Repos; CLAUDE.md's branch and push rules (rule 8) change then, by the owner (agents propose CLAUDE.md changes, ARV-222). Proposed until the owner accepts this amendment.
