# Sensor emulator

Plays the simulated days (`Scenarios/`) to Ariva.Api.Ingest as device traffic (ARV-028; ARV-139b: each device plays its own scenario site's day, DMO or AUH-TA, on the shared demo clock). See "Sensor emulator" in `docs/architecture/sensor-adapters.md` for what each sensor sends.

| File | What it holds |
|---|---|
| `SensorTraffic.cs` | One push per sensor and demo minute: canonical crossings, occupancy and status, or a Xovis firmware 5 logics push; deterministic for a seed. The scenario site fixes each sensor's role: at DMO, S-18 to S-20 report the Visitors desks' staff and service zones (ARV-116), which need a profile with zones `<desk> staff` and `<desk> service`; at AUH-TA the seed's `D-` sensors report their counters' zones, `Q-<lane>-01` counts a queue, `O-VIS-01` and `O-EG-01` report the bands |
| `SensorEmulator.cs` | The demo clock (start, pause, speed, jump, stop minute) and the background player that pushes each completed minute, in order |
| `SensorEmulatorSettings.cs` | `Simulation:Sensors`: Ingest's address, transport, speed limit, devices; validated at start |
| `SensorEmulatorExtensions.cs` | Registration: settings, the Ingest HTTP client (no redirects, no cookies) and the hosted player |

Endpoints, under `api/v1/simulation/sensors` and behind an operator key: `GET` (read scope) for the status; `POST start` with `minute`, `speed` and `untilMinute`, `POST pause`, `PUT speed`, `POST jump` and `PUT devices` (control scope). A device is `{ "site", "sensor", "dialect", "credential" }`: `site` is DMO when not given; the sensor must be one of that site's (DMO: S-01 to S-59; AUH-TA: the seed's codes such as `Q-VIS-01`), and the credential one Ariva issued (`ardk_` and 43 characters); at most 150 devices (both sites' sensors). The seeds are the scenarios' (`PUT api/v1/simulation/scenario`).

To run it against a local stack: register devices for the DMO demo airport in Ariva (one per sensor you want, on the sensor's queue zone, HTTPS push, the dialect you choose), then load their credentials and start:

```bash
curl -X PUT -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"devices":[{"sensor":"S-15","dialect":"Canonical","credential":"ardk_..."}]}' http://localhost:51020/api/v1/simulation/sensors/devices
curl -X POST -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"minute":1080,"speed":10}' http://localhost:51020/api/v1/simulation/sensors/start
```

with `Simulation:Sensors:IngestUrl` set to `http://localhost:51002` and `AllowInsecureTransport` true (vm-local only).

AUH-TA's sensors already exist: the illustrative seed (ARV-139a) places them in Commissioning without a credential. Issue each the credential it plays with (`POST api/v1/admin/devices/{id}/credential`, which needs a recent second factor) and record its calibration (`POST api/v1/admin/devices/{id}/calibrations`), as an installer would, then load it with its site (`node scripts/demo-local.mjs start` does both for both sites):

```bash
curl -X PUT -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"devices":[{"site":"AUH-TA","sensor":"Q-VIS-01","dialect":"Canonical","credential":"ardk_..."}]}' http://localhost:51020/api/v1/simulation/sensors/devices
```
