# Troubleshooting and FAQ

Symptom, likely cause, check, fix. For longer incident procedures see the [Operations runbook](10-Operations-Runbook.md). Commands use `$NAMESPACE` for the deployment's namespace.

## Deployment and platform

| Symptom | Likely cause | Check | Fix |
|---|---|---|---|
| Pod stuck in `ImagePullBackOff` | Missing or wrong `dalilacr-secret`; wrong `imageRepository` or `buildNumber`; no route to the registry | `kubectl -n "$NAMESPACE" describe pod <pod>` | Pass `imageCredentials.username` and `password`, or create the secret out of band; fix the repository or tag; at air-gapped sites point `imageRepository` at the mirror |
| Pod restarts during startup | Startup probe failing (about two minutes allowed); configuration error | `kubectl -n "$NAMESPACE" logs <pod> --previous` | Fix the configuration; check that the appsettings secrets exist and are valid JSON |
| Pod fails because a settings file is missing | `base-appsettings-secret` or `<service>-appsettings-secret` missing, or its key not named `appsettings.*.<env>.json` for the deployment's `environment` | `kubectl -n "$NAMESPACE" get secrets`; `kubectl -n "$NAMESPACE" describe pod <pod>` (volume mount errors) | Recreate the secret with the exact key name (see [Deployment guide](04-Deployment-Guide.md), section 6.2) |
| Production pods use development settings | The Helmfile pins `values-k8s-dev.yaml` | `helm get values ariva-platform -n "$NAMESPACE"` | Install production with `helm upgrade --install` and the production and site values files until the Helmfile has per-environment values |
| Every pod restarted after a small change | Expected: the `rollme` annotation changes on every apply | | Plan applies in a maintenance window |
| `helm rollback` did not restore the old version | The release used a mutable tag such as `trunk`, so the image did not change | `kubectl -n "$NAMESPACE" get deploy -o wide` (image tags) | Pin `buildNumber` to a build number in production; redeploy the previous build number |
| HTTP 421 from the ingress | Request host does not match `<subdomain>.<domain>` (host check in the ingress) | The URL used | Use the exact host; fix `domain` or the subdomain values; fix DNS |
| HTTP 403 when uploading a file | ModSecurity rule blocking dangerous file extensions | Ingress controller logs | Expected for executables and scripts; use the supported formats |
| HPA shows `<unknown>` targets | No metrics server | `kubectl -n "$NAMESPACE" get hpa` | Install or fix the metrics server |
| Ingress protections not applied | Snippet annotations disabled on the controller | Controller configuration | Allow the reviewed snippet annotations, or replace them with controller-level settings |
| Web app calls `localhost` APIs | The web image was built without `VITE_ARIVA_*` URLs | Browser network tab | Build the web image with the site's URLs |
| `${Database:Host}` appears literally in errors | Placeholder substitution not yet active in Ariva's hosts | Logs | Set the full connection values directly until the configuration extension is ported |
| Readiness is green but nothing works | Readiness does not yet check PostgreSQL, Kafka or Redis | Logs of each host | Check dependencies directly; dependency checks arrive in the platform epic |

## Sensors and measurement

| Symptom | Likely cause | Check | Fix |
|---|---|---|---|
| Device stays `Commissioning` | No passed calibration recorded | Devices screen, calibration records | Run and record the calibration; fix mounting or coverage if it fails |
| Device `Offline` | Power, cable, switch port, network, credential | Neighbouring devices on the same switch; ping from the gateway; Ingest logs for authentication errors | Ticket to the local partner; re-issue the credential if it was rotated |
| Zone shows a band instead of a number | Zone `Degraded` (sensor degraded, desk state unknown, feed stale, clock corrected) | Zone's data-quality reason; Devices screen | Fix the input; the number returns when the zone is `Good` |
| Zone shows a neutral message | `Unknown` (an entry or exit line without coverage), no service (no staffed desk), or stale data | Coverage of the lines; desk states; nowcast age | Restore coverage or desk signals; for no service, open a desk |
| Waits look too short | Track fragmentation; exit line too close to the coverage edge; staff tracks not excluded | Track completion rate; line positions (at least 1 m inside coverage); staff exclusion rules | Fix coverage overlap or line placement in a new profile version; re-validate |
| Waits look too long | Censored or stuck tracks; entry line placed before people actually join | Bin maturity backlog; censored share; line placement | Adjust lines in a new profile version; check sensors for ghost tracks |
| Negative or rejected waits, data-quality events | Clock error between sensors, or geometry error | Clock offsets per device | Fix time sync; check line orientation (which side is inside) |
| Counts drift from manual counts | Mounting height or calibration wrong; obstruction in view; firmware change | Calibration record; photos; firmware version | Re-calibrate; remove the obstruction; keep firmware in the certified range |
| Clock offset alarm on many sensors at once | Site time source problem | Site NTP server or PTP grandmaster status | Fix the time source; offsets recover |
| Bins stay provisional for hours | Tracks never resolved (no exit, not timed out); watermark stuck because a partition receives no data | Bin maturity backlog; consumer lag per partition | Fix the input; censoring resolves tracks after T_censor (Proposed 120 minutes) |
| A new zone shows "Not measured" | No calibrated sensor covers it | Device coverage and calibration | Calibrate a covering sensor |
| Overflow alert keeps firing | Overflow band polygon overlaps a walkway; people standing in the band | Floor plan; live tracks | Adjust the band in a new profile version |

## Integrations

| Symptom | Likely cause | Check | Fix |
|---|---|---|---|
| `401 invalid_client` on `/api/v1/auth` | Wrong secret or seed; clock drift on the client; code reused within the same 30-second step; source IP outside the allowed CIDRs; rate limit; client locked or suspended | Client clock (NTP); client status in administration; audit log | Fix the cause; wait for the next TOTP step; unlock after 15 minutes or by an administrator once the cause is known |
| `401` on data calls after a while | Token expired (15 minutes); no refresh tokens exist | `expiresAt` | Re-authenticate about a minute before expiry |
| `403` on a data call | Scope not granted, or the site code not bound to the client | Client scopes and sites | Ask the administrator to grant the scope or bind the site |
| `400` on a batch | Unknown field, validation failure, JSON too deep, wrong interval length (AMAN intervals must be 60 seconds) | Error messages in the `Result` | Fix the payload; remove extra fields |
| `413` | Body above 1 MB (5 MB for AIDX) | Request size | Split into batches of at most 500 items |
| Duplicate data after retries | Retries sent new `Idempotency-Key` values | Request logs | Reuse the same key when retrying the same request |
| AMAN desks show `Unknown` | Unknown desk or gate code parked; AMAN feed stale | Data-quality alerts; feed heartbeat | Add the desk code mapping in a new profile version; restore the feed |
| Arrival-wave strip says it uses last estimates | Stale AODB feed | Feed freshness; Integration logs | See the runbook procedure for a stale AODB feed |
| Outbound call to an AODB or AMAN fails | Endpoint resolves outside its allowed CIDRs; redirect; TLS pinning; circuit breaker open | Integration logs; endpoint configuration | Fix the CIDRs or certificate; Ariva never follows redirects |

## Displays

| Symptom | Likely cause | Check | Fix |
|---|---|---|---|
| Board shows the neutral message | Data stale beyond the threshold, no service, or zone `Unknown` | Nowcast age for the checkpoint | Fix the upstream cause |
| Board changes rarely | Expected: 5-minute bands with hysteresis change only when the nowcast moves a full band | | None |
| Board blank | Player cannot reach the display page on the display VLAN | Network path; kiosk browser | Fix the network; reload the kiosk page |
| Arabic layout broken | Right-to-left not applied | Language order setting | Report as a defect; right-to-left must be tested for Arabic boards |

## FAQ

**Why are there two wait numbers?** The realised wait is exact but only known after people leave the queue, so during a build-up it is late and low. The nowcast is immediate but modelled. Screens and alerts use the nowcast; reports and penalties use the realised wait.

**Why does a bin's value change after I saw it?** Bins are provisional until everyone who joined in them has left or been resolved and late data has had time to arrive. Final bins do not change, except through an audited recomputation, which creates a new revision and keeps the old one.

**Can Ariva show who was in the queue or follow a passenger from check-in to the gate?** No. Tracks are anonymous, rotated at zone exit, and never linked across processes.

**Can a supervisor see which officer was at a desk?** Not in Ariva. Officer data stays in AMAN; the Immigration screen links to AMAN's own reports.

**Why does the passenger board never show the realised wait?** Passengers need to know the wait if they join now; a realised wait describes people who joined earlier.

**What happens if a sensor fails during an SLA period?** The zone is flagged, the board shows a band or a neutral message, and a sensor outage of more than 5 minutes in a bin is an exclusion type in the reference contract, so the handler is not penalised on missing data.

**Can we use our existing CCTV?** Not for queue KPIs. Camera analytics servers that output counts or occupancy events can feed Ariva (planned family); Ariva never receives video.

**Does Ariva need internet access?** No. It runs on premises; live measurement never depends on a WAN. Images are pulled from the registry or a local mirror.

**Can Ariva share AMAN's Kubernetes cluster, Kafka and Redis?** Yes, with its own namespace and database, the `ariva.` topic prefix with ACLs, and the `ariva:` Redis key prefix.

**Which sensors are supported?** See [Sensor catalogue and adapters](09-Sensor-Catalogue-and-Adapters.md). One stereo family (Xovis) is built against recorded payloads in Phase 0; a LiDAR perception platform is certified for the pilot or v1.
