---
description: Run the quality gates for a scope and summarise failures with fixes
argument-hint: [quick|backend|web|e2e|integration|security|docs|all]
---
Run `node scripts/verify.mjs ${ARGUMENTS:-backend}`. For each failing step, show the first meaningful error, the likely cause and the fix. Do not mark anything as passing that failed.
