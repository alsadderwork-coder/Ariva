---
description: Implement one backlog story end to end (plan, tests, code, gates, security review, docs, progress log)
argument-hint: <story id, for example ARV-012>
---
Implement story $ARGUMENTS from backlog/prd-phase0.json (or the PRD that contains it).

1. Read the story, its dependsOn stories (they must pass already; stop if not), and every doc and skill its acceptance criteria name. Read CLAUDE.md rules again if this is a fresh session.
2. Plan in a short list: files, domain changes, endpoints with permissions, CWEs touched (from docs/security/cwe-controls.md), tests at each level. If the plan does not fit one session, split the story in the PRD and stop.
3. Delegate by area when useful (domain-modeler, backend-engineer, stream-engineer, integration-engineer, sensor-adapter-engineer, web-engineer, test-engineer, devops-engineer). Check framework and library APIs with the microsoft-learn, context7, svelte and shadcn-svelte MCP servers rather than guessing.
4. Run every gate listed in the acceptance criteria and fix failures. Never weaken a test or the security gate.
5. Ask the security-reviewer subagent for a verdict on the diff. Fix and repeat until PASS.
6. Update docs and wiki pages the change affects (docs-writer).
7. Append to backlog/progress.md: date, story id, summary, files, gates run with results, security verdict, gotchas for future stories.
8. Set "passes": true for the story, then commit on the current branch with message "$ARGUMENTS: <story title>". Do not push.
