# Azure DevOps: the owner's setup checklist

Every setting the owner makes in Azure DevOps and on GitHub for the move of `docs/plan/azure-devops-move.md`, step by step. This page is ARV-215's (the project, the code connection, the branch policies and GitHub's clean-up); ARV-216 adds the agent pool, the service connections, the variable group, the pipelines and the `k8s-dev` environment to it. Agents cannot change Azure DevOps, Azure or GitHub settings: each step is yours. When a step is done, fill in its row of the record at the end (or tell the agent, who records it in `backlog/progress.md`); ARV-215 passes on that record.

The setting names are those Microsoft Learn shows on 2026-11-20. If the portal labels one differently, "Use a script to update security settings" on Microsoft Learn lists the same settings by their API names, given in the tables below.

## Decisions this page follows

- Decision 1, Option B (the owner, 2026-11-06): Azure Repos becomes the code's home and Azure Pipelines the gate and the delivery.
- The transition (the owner, 2026-11-20): until every Azure pipeline passes (ARV-217 to ARV-221), Azure Pipelines builds from the GitHub repository through the Azure Pipelines GitHub app, and the repository moves into Azure Repos at the cut-over (ARV-223). Cloud agent sessions work on GitHub repositories only, so this keeps them able to fix the pipelines while they are made to pass.
- Decision 2, a cosign key in Azure Key Vault (the owner, 2026-11-06), which also signs the offline bundle: ARV-218 and ARV-221.
- The build agent (the owner, 2026-11-20): a self-hosted Linux x64 agent in pool `Default`, with no job time limit, so the checkpoint's mutation step can run there (ARV-216, ARV-220).
- Still open: decisions 4 to 6 (the dev cluster and its Kafka and Redis, what replaces Dependabot and CodeQL, the secret store) and where the signed offline bundle is archived, from Dalil's reference project.

## 0. Before you start

- [ ] Fast-forward `main` to `story/mvp-phase1` (`docs/plan/azure-devops-move.md` Part 1). Since ARV-295 GitHub runs no CI, so Part 1's "CI is green" condition is replaced by the gates each story records in `backlog/progress.md`; the push itself is unchanged and still refuses anything but a fast-forward.

## 1. Organization and project

- [ ] 1.1 Use Dalil's Azure DevOps organization (the one AMAN's project is in).
- [ ] 1.2 Create a new private project `Ariva` rather than using AMAN's. Microsoft's guidance is one project per product: with the project-scoped job tokens of 1.3, a pipeline of one product then cannot reach the other's repositories, service connections or agent pools. If Dalil's policy puts both products in one project, record that here and keep AMAN's service connections and pools unauthorized for Ariva's pipelines.
- [ ] 1.3 Pipelines settings, at organization level (Organization settings, Pipelines, Settings) if you administer the organization, otherwise at project level (Project settings, Pipelines, Settings). An organization-level value fixes the project's.

| Setting | Value | API name | Why |
|---|---|---|---|
| Limit variables that can be set at queue time | On | `enforceSettableVar` | Only variables marked settable can be changed when a run is queued |
| Limit job authorization scope to current project for non-release pipelines | On | `enforceJobAuthScope` | Each job's token reaches this project only |
| Limit job authorization scope to current project for release pipelines | On | `enforceJobAuthScopeForReleases` | The same for classic releases, which Ariva does not use |
| Protect access to repositories in YAML pipelines | On | `enforceReferencedRepoScopedToken` | The token reaches only the repositories a job checks out (applies to Azure Repos, so from the cut-over) |
| Disable creation of classic build pipelines | On | `disableClassicBuildPipelineCreation` | Every pipeline is YAML, reviewed in the repository |
| Disable creation of classic release pipelines | On | `disableClassicReleasePipelineCreation` | As above |
| Disable anonymous access to badges | On | `statusBadgesArePrivate` | Pipeline status stays private |
| Disable implied YAML CI trigger | On | `disableImpliedYAMLCiTrigger` | A YAML file without a `trigger` section does not run on every push |
| Enable shell tasks arguments validation | On | `enableShellTasksArgsSanitizing` | Shell tasks refuse arguments that would inject commands (CWE-78) |
| Limit building pull requests from forked GitHub repositories | On: Disable building pull requests from forked repositories | `buildsEnabledForForks` false | See 2.4 |

## 2. The code connection during the transition (now)

- [ ] 2.1 Install the Azure Pipelines app from the GitHub Marketplace on the `alsadderwork-coder` account with "Only select repositories": `alsadderwork-coder/Ariva`, and pick the `Ariva` project of 1.2 when the app asks for one. Builds and status checks then use the app's identity, not your personal GitHub identity, and only through the app does Azure Pipelines keep a forked pull request's build from changing the repository's content (forks do not build at all here, 2.4).
- [ ] 2.2 In Project settings, Service connections, open the GitHub connection the app created and leave "Grant access permission to all pipelines" off: each pipeline is authorized on its first run (ARV-216 registers them).
- [ ] 2.3 Do not add a GitHub connection by personal access token or OAuth: both act as your personal identity.
- [ ] 2.4 Fork pull requests do not build (1.3, last row). The repository is public and the agent is self-hosted: a fork's pull request would run its own code on your agent, the machine that later runs the release with its secrets. Pull requests from branches of the repository itself still build.

## 3. GitHub, which runs nothing since ARV-295

- [ ] 3.1 Settings, Actions, General: "Disable actions" (the workflows are archived in `github_archived/workflows/` and must not run again from an older commit or a fork).
- [ ] 3.2 Settings, Secrets and variables, Actions: delete `ANTHROPIC_API_KEY` and `CLAUDE_CODE_OAUTH_TOKEN`.
- [ ] 3.3 Settings, Environments: delete `k8s-dev` with its secrets (`KUBECONFIG_B64`, `APPSETTINGS_*`, `DB_*_PASSWORD`). ARV-216 puts their values into the Azure variable group under the names `docs/plan/azure-devops-move.md` step 2 lists: copy them from where you keep them before you delete the environment, since GitHub never shows a secret's value again.
- [ ] 3.4 Revoke the tokens behind them:
  - the dev cluster service account token inside `KUBECONFIG_B64` (issue a new one for the Azure release, ARV-219);
  - the GHCR pull token (GitHub, Settings, Developer settings, Personal access tokens);
  - the Claude OAuth token and the Anthropic API key (in their own consoles);
  - the personal access token pasted into an agent chat earlier, if not revoked yet.
- [ ] 3.5 Settings, Rules, Rulesets, a tag ruleset targeting `refs/tags/v*`: restrict creations, updates and deletions, and block force pushes, with only your account as a bypass actor. A `v*` tag pushed at a commit from before the archive would run that commit's `images.yml` and sign images as the identity `scripts/offline/verify.mjs` and the admission policy trust (wiki 04 section 6.5).
- [ ] 3.6 The branch ruleset on `main` (Part 1, "After the merge"): block force pushes, block deletion, require a linear history, with your account as a bypass actor for the fast-forward push. Require no status checks for now: none runs on GitHub. Once ARV-217's pipeline passes on Azure, add its check to the ruleset under the name ARV-217 records.
- [ ] 3.7 Leave the Claude GitHub app installed until the cut-over: the cloud agent sessions use it.

## 4. At the cut-over (ARV-223): Azure Repos becomes the home

Listed here so that every repository setting is on one page. Do these only when ARV-223 says so, after the Azure pipelines have passed from GitHub.

- [ ] 4.1 Freeze GitHub: a branch ruleset on all branches that restricts updates, with no bypass actor, so nothing lands on GitHub between the import and the switch.
- [ ] 4.2 Repos, Files, the repository list, Import repository: clone URL `https://github.com/alsadderwork-coder/Ariva.git` (public, so no credentials), name `Ariva`. The import copies every branch and tag with the full history (the repository has no Git LFS objects), and sets `main` as the default branch because one exists. Check that both sides hold the same refs:

  ```bash
  git ls-remote https://github.com/alsadderwork-coder/Ariva.git | sort > github.refs
  git ls-remote <the Azure Repos clone URL> | sort > azure.refs
  diff github.refs azure.refs && echo "same branches, tags and commits"
  ```

- [ ] 4.3 Register the pipelines again against the Azure Repos repository, under the same names (ARV-216's list; `Build-k8s-all` exactly, since the service pipelines trigger on that name), then delete the GitHub-connected ones and the GitHub service connection, and uninstall the Azure Pipelines app from GitHub.
- [ ] 4.4 Branch policies on `main` (Repos, Branches, `main`, Branch policies):

| Policy | Setting |
|---|---|
| Require a minimum number of reviewers | On, 1; "Allow requestors to approve their own changes" on while you are the only reviewer (otherwise no pull request can complete); "Reset code reviewer votes when there are new changes" on |
| Check for comment resolution | On, Required |
| Limit merge types | On, only "Rebase and fast-forward" (a linear history, no merge commits) |
| Build validation | `Analyze-solution` (once ARV-217 makes it pass): trigger Automatic, Required, build expiration "Immediately when main is updated" |
| Automatically included reviewers | You, Required, for the paths `/security/*`, `/scripts/security/*`, `/.claude/*`, `/Platform/Business/*` (as `github_archived/CODEOWNERS`), and also `/Platform/Cloud/Ariva.Cicd/*` (the pipelines, which run with the release's secrets, take the place `/.github/` had) and `/github_archived/*` until ARV-223 deletes it |

- [ ] 4.5 Branch security on `main` (the same page, Branch security): "Force push (rewrite history, delete branches and tags)" Deny for every group; "Bypass policies when completing pull requests" granted to nobody; "Bypass policies when pushing" granted to your account only (see 4.7) or to nobody; "Edit policies" and "Manage permissions" for Project Administrators only.
- [ ] 4.6 Tags (Project settings, Repositories, `Ariva`, Security): "Create tag" Deny for Contributors and for the project's build service identity, so only you create tags, and "Force push (rewrite history, delete branches and tags)" Deny at the repository level, so no tag is moved or deleted. The web portal has no per-pattern tag rule like GitHub's `v*` ruleset, so this restricts every tag, which is what the `v*` rule needs.
- [ ] 4.7 How `main` moves (CLAUDE.md rule 8). Today agents push a story branch and you fast-forward `main` to it, so `main` holds exactly the signed commits the gates checked. Two ways keep that in Azure Repos:
  - Keep the fast-forward push: your account alone holds "Bypass policies when pushing" (4.5), and `git push origin <branch>:main` still refuses anything but a fast-forward. Pull requests are then for review and the build validation, and you push once both pass.
  - Or complete pull requests with "Rebase and fast-forward" only. Check on the first one whether the commits on `main` keep their ids and signatures: when `main` has moved since the branch was taken, a rebase writes new commits, and those carry no signature.
  - Unverified: Microsoft Learn documents no commit signature verification in Azure Repos, so `git log --show-signature` on a clone remains the way to check signatures there.
- [ ] 4.8 Archive the GitHub repository (Settings, General, Archive this repository) or keep it as a read-only mirror, and tell the agents' harness owner that cloud sessions end here: from now on agents work in local Claude Code sessions on a clone of the Azure Repos repository (CLAUDE.md and `docs/harness/README.md` change in ARV-222).

## Record

Fill in a date when a step is done. ARV-215 passes when sections 0 to 3 are done; section 4 is ARV-223's and is confirmed there.

| Step | Done (date, by) |
|---|---|
| 0 `main` fast-forwarded | |
| 1.1, 1.2 Organization and project | |
| 1.3 Pipelines settings | |
| 2.1 to 2.4 Azure Pipelines app and its connection | |
| 3.1 Actions disabled | |
| 3.2, 3.3 Secrets and `k8s-dev` deleted, values kept for ARV-216 | |
| 3.4 Tokens revoked | |
| 3.5 `v*` tag ruleset | |
| 3.6 `main` ruleset | |
| 4.1 to 4.8 (ARV-223) | |
