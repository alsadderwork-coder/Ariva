---
description: Run the full security gate and an independent CWE review on the current changes
---
1. Run `node scripts/security/scan.mjs --self-test` then `node scripts/security/scan.mjs`.
2. Run the semgrep MCP security_check on files changed since the branch point (git diff --name-only), if the server is available.
3. Delegate to the security-reviewer subagent with the diff.
4. Summarise: a table of the 14 CWEs with PASS or FAIL and evidence (rule, test, file), the findings to fix, and any allowlist entries awaiting human approval.
Do not change security/allowlist.json approvals.
