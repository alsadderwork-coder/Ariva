# Xovis conformance samples (ARV-023)

Payloads shaped exactly like Xovis firmware 5 data push JSON, with values of our own. Sources for each shape and what is verified are in `docs/architecture/sensor-adapters.md` (section "Xovis push format"). `XovisConformanceTests` maps every `*.json` here and compares the result with `expected/<name>.json`.

| Sample | Shape source | Status |
|---|---|---|
| `xovis-logics-fw5.json` | Logics push captured from a PC2SE on firmware 5.8.2, published by MRI Software (IoT Hub, Xovis page) | Field names verified; values ours |
| `xovis-logics-unix-ms-array.json` | The same with `format.time` UNIX_TIME_MS and `package_size` above 1 (an array of envelopes), per the Xovis Sensor V5 API (release 5.3) bundled in vanti-dev/sc-bos | Time format verified; the array form plausible |
| `xovis-live-fw5.json` | Live data push per the tracked object schema of the V5 API and the xovis-sdk parsers | Envelope verified from parser code; frame fields and metres inferred |
| `xovis-connection-test.json` | `connection_test` envelope (xovis-sdk) | Verified from parser code |
| `xovis-legacy-event.json` | Firmware 3 and 4 event push (IoTnxt Raptor page) | Verified; refused by design |

Replace these with recorded payloads from the pilot sensors before certification, keeping the expected files.
