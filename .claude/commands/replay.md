---
description: Run the golden scenario replay (seed 9303) and compare outputs and hashes
---
Start the simulator in replay mode for the reference day (seed 9303), run the stream engine over the recorded events, and compare with the golden outputs: the arrivals Visitors nowcast passes 15 minutes at 18:05, sensor S-17 is offline 18:20 to 18:30 with its zone Degraded, and Handler B breaches its SLA at 19:10 for three consecutive 15-minute bins (provisional, then final). Report the output hash and any difference. Never update golden files without the human's approval.
