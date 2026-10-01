---
description: Mirror backlog stories to Azure Boards PBIs in DalilCloud (only when the human asks)
argument-hint: <prd file, default backlog/prd-phase0.json> [area path]
---
Using the azure-devops MCP, mirror the stories in ${ARGUMENTS:-backlog/prd-phase0.json} to Product Backlog Items: title "ARV-nnn: <title>", description, acceptance criteria as a checklist, tag "ariva" and the epic name, parent Feature per epic. First list what would be created or updated and wait for the human's confirmation. Never delete work items. Record the PBI ids back into the story "notes" field.
