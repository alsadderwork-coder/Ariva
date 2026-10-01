---
description: Mirror backlog stories to GitHub issues in alsadderwork-coder/Ariva (only when the human asks)
argument-hint: <prd file, default backlog/prd-phase0.json> [epic filter, for example E2]
---
Using the github MCP (or `gh issue` when the MCP is not connected), mirror the stories in ${ARGUMENTS:-backlog/prd-phase0.json} to GitHub issues in the repository this checkout's `origin` points to:

- Title "ARV-nnn: <title>"; body with the description, the acceptance criteria as a task list, the CWEs to review and the dependencies as issue links once they exist; labels `story`, the epic (for example `E2`) and `passes` when the story passes.
- Match existing issues by the "ARV-nnn:" title prefix and update them; never create duplicates; never close or delete issues (a human closes them when the pull request merges).
- First print the plan (create, update, unchanged counts and the list) and wait for the human's confirmation. Then apply it and record each issue number in the story's "notes" field in the PRD file through `backlog/generate.py` conventions (do not hand-edit prd-phase0.json; add the number to the story in generate.py and regenerate).
