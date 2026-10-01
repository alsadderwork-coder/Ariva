# Progress log

One entry per story, newest last. Format:

```
## YYYY-MM-DD ARV-nnn <title>
- Summary:
- Files:
- Gates: backend PASS, e2e PASS, ...
- Security review: PASS (CWEs checked: ...)
- Learnings for later stories:
```

## 2026-10-01 Scaffold (before ARV-001)
- Summary: Repository created by mirroring AMAN's layout; security baseline (default deny, headers, limits, rate limiting, ProblemDetails, trusted proxies, CORS allowlist); security gate with 40 rules mapped to 14 CWEs; unit, API end-to-end and Playwright functional tests for the skeleton; wiki; harness (CLAUDE.md files, subagents, commands, skills, hooks, MCP servers); Phase 0 backlog.
- Gates run in the build environment (no NuGet access): every non-test project builds with 0 warnings; security gate 0 errors (6 warnings: health probe allowlist entries awaiting approval); e2e 58 of 58 pass; unit tests compiled and run against stand-ins only, so ARV-001 must run them for real.
- Security review: baseline controls in place for CWE-862, CWE-306, CWE-120, CWE-79 (CSP), CWE-269 (non-root images and pod security context); the rest arrive with their stories.
- Learnings: Playwright on the build machine needed an explicit Chromium path; default deny answers 401 even for unknown routes (by design: the fallback policy runs before routing).
