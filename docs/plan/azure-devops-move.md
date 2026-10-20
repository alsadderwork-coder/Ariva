# Merging to main and moving to Azure DevOps

Written 2026-10-20 from the repository as it is on `story/mvp-phase1`. Part 1 is ready to run. Part 2 is a plan: the decisions come first, then the steps, and each piece of work is a story in `backlog/prd-azure-move.json` (ARV-215 to ARV-223). What is open in the rest of the backlog is in [remaining-work.md](remaining-work.md).

## Part 1. Merge `story/mvp-phase1` into `main`

### Where it stands

`main` is at 55be808 (2026-10-06, "MVP: Phase 1 backlog and gap analysis"). `story/mvp-phase1` is 199 commits ahead of it (this document's commit included) and 0 behind, with no merge commits: `main` is an ancestor of the branch, so `main` can be fast-forwarded to it. Every commit is signed. Pull request #67 (`story/mvp-phase1` into `main`) carries the CI runs.

### Why a fast-forward push and not the merge button

The repository's rule (CLAUDE.md rule 8, the owner's workflow since 2026-10-02) is a linear `main` with no merge commits and no rewritten history. GitHub's merge button cannot do that for this pull request:

| Button | What it does here |
|---|---|
| Create a merge commit | Adds a merge commit: against the rule |
| Squash and merge | Turns 199 signed commits into one: the story history and the signatures are lost |
| Rebase and merge | Re-creates all 199 commits with new ids: `main` and the branch then differ, and the commits are no longer the ones signed |

A fast-forward push moves `main` to the branch's last commit, so `main` holds exactly the commits CI checked. GitHub then marks pull request #67 as merged by itself, because its commits are on `main`.

Agents cannot do this step: the harness refuses any agent push to `main` (`.claude/hooks/guard-bash.mjs`). You run it.

### Before you run it

1. Every story in progress is committed and pushed (`git status` clean on the branch).
2. CI is green on the branch's last commit in pull request #67 (every job of `ci.yml`).
3. Nobody has pushed to `main` since (the commands below check this).

### The commands

```bash
git fetch origin
git switch story/mvp-phase1
git status                                   # must say: nothing to commit, working tree clean
git pull --ff-only origin story/mvp-phase1   # the branch as pushed

# main must be an ancestor of the branch, or the push below is refused anyway
git merge-base --is-ancestor origin/main HEAD && echo "fast-forward possible"
git log --oneline origin/main..HEAD | wc -l  # the commits main gains (199 when this was written)

# Fast-forward main on GitHub. The remote refuses anything that is not a fast-forward.
git push origin story/mvp-phase1:main

# Check: both must print the same commit id
git fetch origin
git rev-parse origin/main origin/story/mvp-phase1
```

If the push is refused with "non-fast-forward", someone pushed to `main` in between: stop, and do not force anything; look at what landed on `main` first.

### After the merge

1. Protect `main` (GitHub, Settings, Rules, Rulesets, a branch ruleset on `main`): block force pushes, block deletion, require a linear history, and require the `ci` workflow's checks. If the ruleset also requires pull requests, keep your account as a bypass actor so the fast-forward push stays possible, or switch to merging through pull requests with a rebase-only policy. Check the exact options in GitHub's settings page; this repository does not record them.
2. The two open Dependabot alerts on `main` (esbuild, cookie) close by themselves once `main` holds the fixes (they are fixed on the branch).
3. New work starts on a branch taken from the new `main` (CLAUDE.md rule 8), or `story/mvp-phase1` continues, now level with `main`.
4. The keyless image signing path (`images.yml`, push from `main`) can now run from `main` for the first time.

## Part 2. Moving to Azure DevOps

### What exists today

| | GitHub (in use) | Azure DevOps (in the repository, never run) |
|---|---|---|
| Pull request gate | `ci.yml`: security gate, AsyncAPI, backend, web, E2E with Kafka and Redis, visual baselines | `Common/Analyze-solution.yaml`: would fail as written (starts only PostgreSQL; the E2E config needs Kafka and Redis in CI), and lacks the docs rules, base image check, chart security, offline bundle self-tests, vulnerable package list, AsyncAPI and test-count check |
| Images | `images.yml`: 7 images to GHCR, Trivy image scan, cosign keyless signature, signed SBOM attestation, digest table | `K8s/Build-k8s-<service>.yaml` (7) and `Build-k8s-all.yaml`: build and push to `dalil.azurecr.io/ariva/<service>`, tags `main` and the build number; no scan, no signature, no SBOM |
| Dev release | `release-dev.yml`: by hand, from `main`, GitHub environment `k8s-dev` with the owner as reviewer, every image's signature verified and pinned by digest, checksum-pinned tools, TLS check, pod evidence | `K8s/Release-ariva-k8s-dev.yaml`: by hand, variable group `ariva`, unpinned kubectl and unchecked Helm, Helmfile and helm-diff, deploys the mutable tag `main`, no signature check, no approval |
| Scheduled and other | Trivy and Semgrep scans, SBOM, CodeQL (when enabled), ZAP, mutation, dev environment smoke, base image proposals, Dependabot, offline bundle (signed with GitHub's OIDC identity), Claude review | none |

The chart and Helmfile need no change for Azure: every values file already defaults `imageRepository` to `dalil.azurecr.io/ariva`, and the chart writes the `dalilacr-secret` pull secret for `dalil.azurecr.io` when the release passes `imageCredentials`.

### Decisions to take first

1. **Where the code lives.**
   - Option A, GitHub stays the home and Azure Pipelines does the delivery: Azure Pipelines builds from the GitHub repository (a GitHub service connection), pushes to Dalil Container Registry and releases to Dalil's Azure. Everything that works today keeps working, including this kind of cloud agent session, which works on GitHub repositories. The repository stays public (owner decision 2026-10-15).
   - Option B, Azure Repos becomes the home, as for AMAN: the repository is imported into Azure Repos, every GitHub workflow has to be ported or dropped, and GitHub is archived or kept as a read-only mirror. Agents then work through local Claude Code sessions on a machine with the Azure Repos clone; cloud sessions like this one attach GitHub repositories, so they could only work on the mirror.
   - Recommendation: Option A now. It moves the delivery (registry, release, cluster) to Dalil's Azure with the least risk and keeps the gates and the agent workflow. Take Option B only if Dalil's policy requires the code in Azure Repos, and then after the Azure pipelines have reached the GitHub ones' level (ARV-217 to ARV-221), so nothing is lost on the way. The rest of this plan works for both; the steps that differ say so.
2. **Image signing without GitHub.** A cosign key in Azure Key Vault (`cosign sign --key azurekms://...`, wiki 04 section 6.5), copying the signed GHCR images into Dalil Container Registry with `oras copy -r` (keeps GitHub's signatures; Option A only), or unsigned images. The cluster admission policy and the offline bundle depend on this.
3. **The offline bundle's signer.** It verifies GitHub OIDC signatures only (`scripts/offline/verify.mjs`); with Option B, or with a Key Vault key, it needs a key-based identity, and its archive needs a home ("Dalil's release store").
4. **The dev cluster.** The Azure release targets the kube context `jupiter`; which cluster that is, and whether Kafka and Redis for dev are AMAN's or Ariva's own (the Helmfile ships neither).
5. **What replaces Dependabot and CodeQL** on Azure (Option B): Renovate or the Dependabot task for dependency updates; GitHub Advanced Security for Azure DevOps (paid) or Semgrep and Trivy only for code scanning.
6. **The secret store.** Azure Key Vault was postponed "with the move to Azure" (ARV-083c); the move is the time to decide it, and the variable group can be linked to Key Vault from the start.

### Steps

Each step names its story in `backlog/prd-azure-move.json`. Steps 1 and 2 are done by you in Azure DevOps and Azure; the pipeline work (3 to 7) is agent work in the repository.

**1. Project and repository (ARV-215, you).**
- Pick or create the Azure DevOps project (the one AMAN uses, or a new one).
- Option A: Pipelines, New pipeline, GitHub, and install the Azure Pipelines app on `alsadderwork-coder/Ariva` (read access is enough).
- Option B: Repos, Import repository, clone URL `https://github.com/alsadderwork-coder/Ariva.git` (public, so no credentials); it brings every branch and the full history. Set `main` as the default branch.
- Option B, branch policies on `main`: build validation with the Analyze-solution pipeline (once ARV-217 makes it pass), at least one reviewer, path-based required reviewers in place of `.github/CODEOWNERS`, merge types limited to "Rebase and fast-forward" (a linear history), and deny "Force push" and "Delete" in the branch security settings. The same for `v*` tags.

**2. Agent pool, service connections and secrets (ARV-216, you, with the agent preparing the documented list).**
- Agent pool: the pipelines ask for pool `Default` with a Linux x64 agent (`Agent.OS -equals Linux`, `Agent.OSArchitecture -equals X64`). Register a self-hosted Linux x64 agent there with Docker, the .NET 10 SDK, Node 22 and sudo rights (the release installs tools), or change the YAML to Microsoft-hosted `ubuntu-24.04`.
- Service connection named exactly `Dalil Container Registry`: Docker Registry type, Azure Container Registry, `dalil.azurecr.io`, workload identity federation rather than a stored password where the organisation allows it.
- Cluster access: today the release reads a base64 kubeconfig (`JUPITER-KUBECONFIG-B64`) from the variable group. A Kubernetes service connection scoped to the namespace is better and is part of ARV-219.
- Library, variable group `ariva` (link it to Key Vault if decision 6 says so). Secrets, by the names the release reads: `JUPITER-KUBECONFIG-B64`, `ACR-USERNAME`, `ACR-PASSWORD`, `ARIVA-K8S-DEV-BASE-APPSETTINGS`, `ARIVA-K8S-DEV-API-MAIN-APPSETTINGS`, `ARIVA-K8S-DEV-API-INGEST-APPSETTINGS`, `ARIVA-K8S-DEV-API-STREAM-APPSETTINGS`, `ARIVA-K8S-DEV-API-CRONZ-APPSETTINGS`, `ARIVA-K8S-DEV-API-INTEGRATION-APPSETTINGS`, `ARIVA-K8S-DEV-SIMULATION-APPSETTINGS`, `ARIVA-K8S-DEV-DB-SUPERUSER-PASSWORD`, `ARIVA-K8S-DEV-DB-MIGRATION-PASSWORD`. They hold the same values as the GitHub environment `k8s-dev`'s secrets (`KUBECONFIG_B64`, `APPSETTINGS_*`, `DB_*_PASSWORD`). Never paste them into a pipeline file or a chat.
- Register the pipelines from their YAML files: `Common/Analyze-solution.yaml`, `K8s/Build-k8s-all.yaml` (the name must be exactly `Build-k8s-all`: the service pipelines trigger on it), the seven `K8s/Build-k8s-<service>.yaml`, and `K8s/Release-ariva-k8s-dev.yaml`.
- Environment `k8s-dev` (Pipelines, Environments) with you as approver; the release uses it once ARV-219 turns its job into a deployment job.

**3. The pull request gate at the GitHub gate's level (ARV-217, agent).** Kafka and Redis beside PostgreSQL, every check `ci.yml` runs, "trunk" corrected to `main`, and a run on Azure that passes.

**4. Image builds with scans, signatures and SBOMs (ARV-218, agent).** Complete path filters, a Trivy image scan that fails on high or critical findings with a fix, a CycloneDX SBOM, a signature and an SBOM attestation by the key of decision 2, and a digest table artifact.

**5. The dev release at the GitHub release's level (ARV-219, agent).** The `k8s-dev` environment with your approval, checksum-pinned tools, every image's signature verified and its digest pinned (`imageDigests`), the pull secret made out of band (ARV-209), the one-time secrets check, the TLS check and the pod evidence.

**6. Scheduled scans and dependency updates (ARV-220, agent; Option B, or by choice).** Trivy and Semgrep, SBOM, ZAP, mutation, the dev environment smoke and base image proposals as scheduled pipelines; Renovate or the Dependabot task; the CodeQL decision.

**7. The offline bundle without GitHub's OIDC (ARV-221, agent).** Signed by the decision 2 key and verified by `scripts/offline/verify.mjs` against it, with its self-tests and a rehearsal.

**8. Docs and harness (ARV-222, agent).** An ADR-0016 amendment, CLAUDE.md, wiki 04 (sections 6 and 6.5 to 6.6), the harness README, and the scripts that assume GitHub Actions (`chart-security.mjs` checks Helmfile only under `GITHUB_ACTIONS`, `scripts/offline/bundle.mjs`, `scripts/base-images.mjs` reading tool versions from the GitHub workflows).

**9. Cut-over and the first release from Azure (ARV-223, you).**
- Before the first Azure DevOps release to the dev cluster, delete the GitHub release's pull secret once: `kubectl delete secret dalilacr-secret -n ariva-k8s-dev` (wiki 04: Helm refuses to adopt the secret the GitHub workflow made).
- Run `Build-k8s-all`, check the seven images in `dalil.azurecr.io/ariva` (signed, if ARV-218 is done), then run the release with the build number and approve it.
- Run the smoke suite against dev: this is ARV-137 (ARV-002's last criterion), now from Azure.
- Option B only: freeze pushes to GitHub, import or sync once more, switch the team to Azure Repos, and archive the GitHub repository or keep it as a read-only mirror.

### Order and what it costs

| Order | Step | Who | Size |
|---|---|---|---|
| 1 | Decisions 1 to 6 | You | |
| 2 | Part 1, merge to `main` | You | Minutes |
| 3 | ARV-215, ARV-216 | You (agent writes the checklist) | About a day |
| 4 | ARV-217 | Agent | M |
| 5 | ARV-218 | Agent, needs decision 2's key | M |
| 6 | ARV-219 | Agent | M |
| 7 | First Azure release and smoke (ARV-223 with ARV-137) | You | Hours |
| 8 | ARV-220, ARV-221, ARV-222 | Agent | M, M, S |
| 9 | Option B cut-over (ARV-223) | You | Hours |

Until step 7 passes, keep releasing from GitHub as today: the two paths use the same Helmfile and chart, and only the pull secret has to be switched once between them.
