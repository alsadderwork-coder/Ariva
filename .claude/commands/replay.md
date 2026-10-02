---
description: Run the golden scenario replay (seed 9303) and compare outputs and hashes
---
Run `dotnet test Platform/Backplane/Ariva.UnitTests --filter FullyQualifiedName~GoldenReplayTests` (the reference evening, seed 9303, through Ingest and the archive's form into the zone replay) and, with Docker, `dotnet test Platform/Backplane/Ariva.IntegrationTests --filter FullyQualifiedName~GoldenReplayArchiveTests` (the same evening through the TimescaleDB archive and the replay command's runner). The golden outputs: the arrivals Visitors nowcast passes 15 minutes at 18:05; sensor S-17 is out from 18:20 to 18:31 with its zone Degraded and the 18:15 and 18:30 bins marked; Handler B's island C breaches in the 19:00, 19:15 and 19:30 bins. Report the output hash (`outputHead`) and any difference from `Platform/Backplane/Ariva.UnitTests/Replay/replay-golden.json`. Never update the golden file without the human's approval; when the hash changes, show which outputs changed (minutes, bins, live rows, outages) and why.

For a real site's evidence, the runbook (wiki 10, section 4.10) has the `Ariva.Api.Stream --replay` and `--verify-replay` commands.
