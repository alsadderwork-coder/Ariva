---
description: Show the next eligible backlog stories and a plan for the first one
---
Read every backlog/prd-*.json. List stories with "passes": false whose dependsOn all pass, ordered by priority (top 5, with titles). For the first one, give a short implementation plan: files, CWEs touched, tests per level, gates. Do not start coding.
