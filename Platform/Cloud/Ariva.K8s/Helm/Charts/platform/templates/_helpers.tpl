{{/*
Expand the name of the chart.
*/}}
{{- define "ariva-platform.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "ariva-platform.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels added to every resource (selectors keep using the plain app label, as in AMAN).
*/}}
{{- define "ariva-platform.labels" -}}
helm.sh/chart: {{ include "ariva-platform.chart" . }}
app.kubernetes.io/name: {{ include "ariva-platform.name" . }}
app.kubernetes.io/part-of: ariva
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/version: {{ .Values.releaseVersion | quote }}
{{- end }}

{{/*
OpenTelemetry environment variables shared by the .NET hosts (the five API hosts and the simulator). The service name comes
from Application:Name in appsettings; these add the export target and the resource attributes, and with otel.headersSecret
(ARV-091a) the collector's sender headers, OTEL_EXPORTER_OTLP_HEADERS, from that Secret only (secretKeyRef, not optional);
validate.yaml then requires an https:// endpoint. With otel.caSecretName (ARV-200), Otlp__RootCertificate, the file of the
otlp-ca volume (collectorCaVolumes), which the exporters and Serilog's OTLP sink then trust alone for the collector;
validate.yaml then requires an https:// endpoint too. The chart test fails if another workload gets them.
*/}}
{{- define "ariva-platform.otelEnv" -}}
{{- $headers := (.Values.otel | default dict).headersSecret -}}
{{- if not (kindIs "map" $headers) }}{{ $headers = dict }}{{ end -}}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ .Values.otel.endpoint | default "" | quote }}
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: {{ .Values.otel.protocol | default "grpc" | quote }}
{{- if $headers.name }}
# The collector's sender headers (ARV-091a): from their Secret only, over https only
- name: OTEL_EXPORTER_OTLP_HEADERS
  valueFrom:
    secretKeyRef:
      name: {{ toString $headers.name | quote }}
      key: {{ toString ($headers.key | default "headers") | quote }}
      optional: false
{{- end }}
{{- if include "ariva-platform.otlpCaSecretName" . }}
# The OTLP collector's CA (ARV-200): the otlp-ca volume's file, the only CA the exporters then trust for it, over https only
- name: Otlp__RootCertificate
  value: "/app/secrets/otlp-ca/ca.crt"
{{- end }}
- name: OTEL_RESOURCE_ATTRIBUTES
  value: {{ printf "service.namespace=ariva,deployment.environment=%s,k8s.cluster.name=%s,service.version=%s" .Values.environment .Values.clusterName .Values.releaseVersion | quote }}
- name: K8S_NODE_NAME
  valueFrom:
    fieldRef:
      fieldPath: spec.nodeName
- name: K8S_POD_NAME
  valueFrom:
    fieldRef:
      fieldPath: metadata.name
- name: K8S_NAMESPACE_NAME
  valueFrom:
    fieldRef:
      fieldPath: metadata.namespace
{{- end }}

{{/*
Docker config JSON for dalilacr-secret, built from .Values.imageCredentials.
*/}}
{{- define "ariva-platform.imagePullSecret" -}}
{{- with .Values.imageCredentials }}
{{- printf "{\"auths\":{\"%s\":{\"username\":\"%s\",\"password\":\"%s\",\"auth\":\"%s\"}}}" .registry .username .password (printf "%s:%s" .username .password | b64enc) | b64enc }}
{{- end }}
{{- end }}

{{/*
An Ariva image: imageRepository/<service>:buildNumber, and @<digest> when imageDigests.<service> is set, so the cluster
pulls exactly the image whose signature the release verified (ARV-002 GitHub release; a tag can be moved, a digest cannot).
Usage: {{ include "ariva-platform.image" (list . "api-main") }}
*/}}
{{- define "ariva-platform.image" -}}
{{- $root := index . 0 -}}
{{- $service := index . 1 -}}
{{- $digest := "" -}}
{{- with $root.Values.imageDigests }}{{ $digest = index . $service | default "" }}{{ end -}}
{{- if $digest -}}
{{- printf "%s/%s:%s@%s" $root.Values.imageRepository $service (toString $root.Values.buildNumber) $digest | quote -}}
{{- else -}}
{{- printf "%s/%s:%s" $root.Values.imageRepository $service (toString $root.Values.buildNumber) | quote -}}
{{- end -}}
{{- end }}

{{/*
The servers' CAs (ARV-082a): the CA that issued the TimescaleDB server certificate and the one that issued the Redis
server certificate, each a secret with the PEM key ca.crt (values databaseCaSecretName and redisCaSecretName), for the
workloads that run an Ariva .NET host against PostgreSQL and Redis: the five API hosts and the migration and Kafka topics
jobs (they build the same host). Not the web pod or the simulator. In k8s-prd every host verifies the server against them
(Database:RootCertificate /app/secrets/database-ca/ca.crt, Redis:CaCertificatePath /app/secrets/redis-ca/ca.crt) and
refuses to start without the file; the secrets stay optional here, as kafka-ca, so dev and demo run without them. Only
ca.crt is projected (items), so a secret that also holds a private key (a reused kubernetes.io/tls secret) never brings it
into a host pod. The chart test requires both mounts, read-only, in exactly these workloads.
Usage: {{- include "ariva-platform.serverCaVolumes" . | nindent 8 }} and {{- include "ariva-platform.serverCaMounts" . | nindent 12 }}
*/}}
{{- define "ariva-platform.serverCaVolumes" -}}
# TimescaleDB server CA (ARV-082a): PEM ca.crt; k8s-prd hosts verify the database against it
- name: database-ca
  secret:
    secretName: {{ .Values.databaseCaSecretName | default "ariva-database-ca" }}
    optional: true
    defaultMode: 0444
    items:
      - key: ca.crt
        path: ca.crt
# Redis server CA (ARV-082a): PEM ca.crt; k8s-prd hosts verify Redis against it
- name: redis-ca
  secret:
    secretName: {{ .Values.redisCaSecretName | default "ariva-redis-ca" }}
    optional: true
    defaultMode: 0444
    items:
      - key: ca.crt
        path: ca.crt
{{- end }}

{{- define "ariva-platform.serverCaMounts" -}}
- name: database-ca
  mountPath: /app/secrets/database-ca
  readOnly: true
- name: redis-ca
  mountPath: /app/secrets/redis-ca
  readOnly: true
{{- end }}

{{/*
The collectors' CA secret names (ARV-200): otel.caSecretName and logging.lokiCaSecretName, or empty when unset (or when otel or
logging is not a map, which validate.yaml refuses). Usage: {{- if include "ariva-platform.otlpCaSecretName" . }}
*/}}
{{- define "ariva-platform.otlpCaSecretName" -}}
{{- if kindIs "map" .Values.otel }}{{ toString (.Values.otel.caSecretName | default "") }}{{ end -}}
{{- end }}

{{- define "ariva-platform.lokiCaSecretName" -}}
{{- if kindIs "map" .Values.logging }}{{ toString (.Values.logging.lokiCaSecretName | default "") }}{{ end -}}
{{- end }}

{{/*
The collectors' CAs (ARV-200; values otel.caSecretName and logging.lokiCaSecretName): for a log or telemetry collector whose
certificate chains to the site PKI, the Secret in the release's namespace whose key ca.crt is that CA (or a bundle of the
site's CAs). otlp-ca for the OTLP collector, in the five API hosts and the simulator (both export to it), and loki-ca for Loki,
in the five API hosts only (the simulator sends no logs to Loki); nothing in the web pod or the jobs. Only ca.crt is projected
(items), so a Secret that also holds a private key never brings it into a pod; mode 0444, mounted read-only and whole (no
subPath) at /app/secrets/otlp-ca and /app/secrets/loki-ca, where otelEnv's Otlp__RootCertificate and loggingEnv's
Loki__RootCertificate point. Not optional: a host given the variable refuses to start without the file, so the pods wait for
the Secret instead. Nothing when the value is empty. The chart test requires each mount, read-only, in exactly these
workloads, with its variable, and neither anywhere else.
Usage: {{- with include "ariva-platform.collectorCaVolumes" (list . "api-main") | trim }}{{ . | nindent 8 }}{{ end }} and
{{- with include "ariva-platform.collectorCaMounts" (list . "api-main") | trim }}{{ . | nindent 12 }}{{ end }}; the second
argument is the workload's service (api-main, api-ingest, api-stream, api-cronz, api-integration or simulation).
*/}}
{{- define "ariva-platform.collectorCaVolumes" -}}
{{- $root := index . 0 -}}
{{- $service := index . 1 -}}
{{- $otlp := include "ariva-platform.otlpCaSecretName" $root -}}
{{- $loki := "" -}}
{{- if has $service (list "api-main" "api-ingest" "api-stream" "api-cronz" "api-integration") }}{{ $loki = include "ariva-platform.lokiCaSecretName" $root }}{{ end -}}
{{- if $otlp }}
# The OTLP collector's CA (ARV-200): ca.crt of otel.caSecretName only, read-only; the pods wait for the Secret
- name: otlp-ca
  secret:
    secretName: {{ $otlp | quote }}
    optional: false
    defaultMode: 0444
    items:
      - key: ca.crt
        path: ca.crt
{{- end }}
{{- if $loki }}
# Loki's CA (ARV-200): ca.crt of logging.lokiCaSecretName only, read-only; the pods wait for the Secret
- name: loki-ca
  secret:
    secretName: {{ $loki | quote }}
    optional: false
    defaultMode: 0444
    items:
      - key: ca.crt
        path: ca.crt
{{- end }}
{{- end }}

{{- define "ariva-platform.collectorCaMounts" -}}
{{- $root := index . 0 -}}
{{- $service := index . 1 -}}
{{- $loki := "" -}}
{{- if has $service (list "api-main" "api-ingest" "api-stream" "api-cronz" "api-integration") }}{{ $loki = include "ariva-platform.lokiCaSecretName" $root }}{{ end -}}
{{- if include "ariva-platform.otlpCaSecretName" $root }}
- name: otlp-ca
  mountPath: /app/secrets/otlp-ca
  readOnly: true
{{- end }}
{{- if $loki }}
- name: loki-ca
  mountPath: /app/secrets/loki-ca
  readOnly: true
{{- end }}
{{- end }}

{{/*
Why the Secret a collector's CA value names would stop the hosts or leave their pods waiting (ARV-200), or empty when it is
fine: given a dict of secret (the Secret as lookup returns it; an empty map when there is none to judge), name, value
(otel.caSecretName or logging.lokiCaSecretName), collector (whose CA it is, for the message) and reachable (true when the
release sees the cluster: lookup found the namespace's kube-root-ca.crt ConfigMap, as for the log pseudonym key). Rendering
offline (helm template) or into a namespace that does not exist yet sees neither, and the guard then passes: the pods wait
for the Secret. A reachable namespace without the Secret is refused, as is a Secret whose ca.crt holds a private key (the
hosts refuse a file with PRIVATE KEY anywhere in it: a key pair put there by mistake), one that holds any key but ca.crt
(the Secret holds ca.crt and nothing else: the pods mount ca.crt alone, and no private key or keystore in any form, PEM, DER
or PKCS#12, such as the collector's own cert-manager Certificate Secret, then sits in Ariva's namespace), and one whose ca.crt
holds no PEM certificate (no ca.crt, or one empty, not base64 or not PEM). Each refusal is a fixed message naming the value,
the Secret and, when they are Kubernetes key names, its other keys; none holds a value of the Secret. validate.yaml calls
this with the lookups; chart-security.mjs renders it with stand-in secrets, as helm template cannot run lookup.
Usage: {{ include "ariva-platform.collectorCaRefusal" (dict "secret" (lookup "v1" "Secret" .Release.Namespace "site-otlp-ca") "name" "site-otlp-ca" "value" "otel.caSecretName" "collector" "the OTLP collector" "reachable" true) }}
*/}}
{{- define "ariva-platform.collectorCaRefusal" -}}
{{- $secret := .secret -}}
{{- if not (kindIs "map" $secret) }}{{ $secret = dict }}{{ end -}}
{{- if not $secret -}}
{{- if .reachable -}}
{{ .value }} names the Secret {{ .name }}, which the release's namespace does not hold: create it before the release with the key ca.crt, the PEM CA {{ .collector }}'s certificate chains to, or the pods wait for it (ARV-200)
{{- end -}}
{{- else -}}
{{- $data := $secret.data -}}
{{- if not (kindIs "map" $data) }}{{ $data = dict }}{{ end -}}
{{- $pem := toString (index $data "ca.crt" | default "") | b64dec -}}
{{- $others := list -}}
{{- $named := true -}}
{{- range $key, $_ := $data }}{{ if ne $key "ca.crt" }}{{ $others = append $others $key }}{{ if not (regexMatch "^[-._a-zA-Z0-9]{1,253}$" $key) }}{{ $named = false }}{{ end }}{{ end }}{{ end -}}
{{- if contains "PRIVATE KEY" $pem -}}
{{ .value }}: the Secret {{ .name }} holds a private key under ca.crt, and the hosts refuse to start on it; keep only the certificates of the CA {{ .collector }}'s certificate chains to there (ARV-200)
{{- else if $others -}}
{{ .value }}: the Secret {{ .name }} holds keys other than ca.crt{{ if $named }} ({{ join ", " $others }}){{ end }}; a collector's CA Secret holds ca.crt and nothing else, so no private key or keystore, such as the collector's own cert-manager Certificate Secret, sits in Ariva's namespace: create a Secret with ca.crt only (ARV-200)
{{- else if not (contains "-----BEGIN CERTIFICATE-----" $pem) -}}
{{ .value }}: the Secret {{ .name }} holds no PEM certificate under ca.crt, and the hosts refuse to start on it; put the CA {{ .collector }}'s certificate chains to there, as PEM (ARV-200)
{{- end -}}
{{- end -}}
{{- end }}

{{/*
Pod TLS (ARV-082b; values podTls): "true" when the pods serve TLS to the ingress and to each other, empty otherwise.
Usage: {{- if include "ariva-platform.podTls" . }}
*/}}
{{- define "ariva-platform.podTls" -}}
{{- if (.Values.podTls | default dict).enabled }}true{{ end -}}
{{- end }}

{{/*
The kubernetes.io/tls secret a service serves TLS from (podTls.secretNames.<service>, empty: ariva-<service>-tls).
Usage: {{ include "ariva-platform.podTlsSecretName" (list . "api-main") }}
*/}}
{{- define "ariva-platform.podTlsSecretName" -}}
{{- $root := index . 0 -}}
{{- $service := index . 1 -}}
{{- $names := ($root.Values.podTls | default dict).secretNames | default dict -}}
{{- index $names $service | default (printf "ariva-%s-tls" $service) -}}
{{- end }}

{{/*
The certificate volume of a pod that serves TLS (ARV-082b): its own secret, projecting tls.crt and tls.key only, so a
ca.crt or anything else the secret holds never reaches the pod; 0440, read by the app user through the pod's fsGroup 10001.
Mounted as a whole directory (never a subPath): the kubelet swaps a renewed certificate into it and Kestrel reloads it
without a restart. The chart test requires exactly this mount in every pod that serves TLS.
Usage: {{- include "ariva-platform.podTlsVolume" (list . "api-main") | nindent 8 }}, inside {{- if include "ariva-platform.podTls" . }}
*/}}
{{- define "ariva-platform.podTlsVolume" -}}
{{- $root := index . 0 -}}
{{- $service := index . 1 -}}
# Pod TLS certificate (ARV-082b): tls.crt and tls.key only, read-only, renewed in place
- name: pod-tls
  secret:
    secretName: {{ include "ariva-platform.podTlsSecretName" (list $root $service) | quote }}
    defaultMode: 0440
    items:
      - key: tls.crt
        path: tls.crt
      - key: tls.key
        path: tls.key
{{- end }}

{{/*
Why a pod TLS cluster CA secret would leave the ingress unsafe (ARV-082b), or empty when it is fine: given the Secret object
(as lookup returns it; an empty map when there is none to judge). ingress-nginx writes proxy_ssl_trusted_certificate,
proxy_ssl_verify, proxy_ssl_verify_depth and proxy_ssl_protocols only when the secret yields a CA file, which needs ca.crt:
without it the Ingress still proxies HTTPS, unverified and silently. A tls.key beside ca.crt is copied to the controller's
disk and presented to the pods as a client certificate (a pod's key, or the CA's own). So: ca.crt holding a PEM certificate,
and no tls.key. validate.yaml calls this with the lookup of podTls.caSecretName; chart-security.mjs renders it with
stand-in secrets, as helm template cannot fake lookup.
Usage: {{ include "ariva-platform.podTlsCaRefusal" (lookup "v1" "Secret" .Release.Namespace "ariva-cluster-ca") }}
*/}}
{{- define "ariva-platform.podTlsCaRefusal" -}}
{{- if . -}}
{{- $data := .data | default dict -}}
{{- if not (contains "-----BEGIN CERTIFICATE-----" (toString (index $data "ca.crt" | default "") | b64dec)) -}}
podTls.caSecretName names a secret without ca.crt: the ingress would proxy to the pods without verifying them (ARV-082b); put the cluster CA in it as ca.crt
{{- else if hasKey $data "tls.key" -}}
podTls.caSecretName names a secret that also holds tls.key: ingress-nginx would copy that private key to the controller and present it to the pods (ARV-082b); keep only ca.crt in it
{{- end -}}
{{- end -}}
{{- end }}

{{- define "ariva-platform.podTlsMount" -}}
- name: pod-tls
  mountPath: /app/secrets/pod-tls
  readOnly: true
{{- end }}

{{/*
An API host's container ports: 8080 plain HTTP, or with pod TLS 8443 only, named https (ARV-082b; the host listens there
in k8s-prd, Kestrel:Endpoints:Https). Usage: {{- include "ariva-platform.apiPorts" . | nindent 12 }}
*/}}
{{- define "ariva-platform.apiPorts" -}}
{{- if include "ariva-platform.podTls" . -}}
- name: https
  containerPort: 8443
{{- else -}}
- containerPort: 8080
{{- end -}}
{{- end }}

{{/*
An API host's probe target: path on 8080, or with pod TLS HTTPS on 8443 (the kubelet does not verify the certificate).
Usage: {{ include "ariva-platform.apiProbe" (list . "/health/startup") }}
*/}}
{{- define "ariva-platform.apiProbe" -}}
{{- $root := index . 0 -}}
{{- $path := index . 1 -}}
{{- if include "ariva-platform.podTls" $root -}}
httpGet: { path: {{ $path }}, port: 8443, scheme: HTTPS }
{{- else -}}
httpGet: { path: {{ $path }}, port: 8080 }
{{- end -}}
{{- end }}

{{/*
An API host's Service ports: 80 to 8080, or with pod TLS 443 (name https) to the pods' https port, so the internal
addresses are https://<service> with no port (ARV-082b). Usage: {{- include "ariva-platform.apiServicePorts" (list . "api-main") | nindent 4 }}
*/}}
{{- define "ariva-platform.apiServicePorts" -}}
{{- $root := index . 0 -}}
{{- $service := index . 1 -}}
{{- if include "ariva-platform.podTls" $root -}}
- name: https
  port: 443
  targetPort: https
{{- else -}}
- name: {{ $service }}-service-port
  port: 80
  targetPort: 8080
{{- end -}}
{{- end }}

{{/*
The validation reader login (ARV-104g1): name and password from the secret validationReader.secretName (keys username and
password), for api-main (the validation service) and the database-migration job (which creates the login) only; the chart
test fails if another workload gets them. optional follows validationReader.optional (false in k8s-prd, validate.yaml).
*/}}
{{- define "ariva-platform.validationReaderEnv" -}}
- name: Database__ValidationReader__Username
  valueFrom:
    secretKeyRef:
      name: {{ .Values.validationReader.secretName | quote }}
      key: username
      optional: {{ .Values.validationReader.optional }}
- name: Database__ValidationReader__Password
  valueFrom:
    secretKeyRef:
      name: {{ .Values.validationReader.secretName | quote }}
      key: password
      optional: {{ .Values.validationReader.optional }}
{{- end }}

{{/*
Log shipping and the log pseudonym key of the five API hosts (ARV-091a; values logging), and nothing when neither is set:
- Logging__PseudonymKey from the Secret logging.pseudonymKeySecret names (its key, pseudonym-key by default), as a secretKeyRef
  that is not optional: a host waits for the Secret rather than start with a key of its own. The key itself is never a value.
- with logging.collector loki, Loki__Enabled "true": Loki:Uri comes from the appsettings secret, which the chart cannot read, so
  a k8s-prd host refuses to start until that secret gives an https Loki:Uri. With otlp, otelEnv's OTEL_EXPORTER_OTLP_ENDPOINT
  is the collector (validate.yaml requires it https).
- with logging.lokiCredentialsSecret, Loki__Username and Loki__Password from that Secret (secretKeyRef, not optional), for a
  Loki that authenticates senders: outside vm-local a host refuses them from a file, and sends them over https only.
- with logging.lokiCaSecretName (ARV-200), Loki__RootCertificate, the file of the loki-ca volume (collectorCaVolumes): the Loki
  sink then trusts that CA alone, and a host refuses to start with it unless Loki:Uri is https (validate.yaml requires loki).
Only the five API hosts get them (not the simulator, the web pod or the jobs, whose one-shot commands log before the host's
logger exists); the chart test fails if another workload does.
Usage: {{- with include "ariva-platform.loggingEnv" . | trim }}{{ . | nindent 12 }}{{ end }}
*/}}
{{- define "ariva-platform.loggingEnv" -}}
{{- $logging := .Values.logging -}}
{{- if not (kindIs "map" $logging) }}{{ $logging = dict }}{{ end -}}
{{- $secret := $logging.pseudonymKeySecret -}}
{{- if not (kindIs "map" $secret) }}{{ $secret = dict }}{{ end -}}
{{- if $secret.name }}
# The log pseudonym key (ARV-091a): Logging:PseudonymKey from its Secret only, never optional
- name: Logging__PseudonymKey
  valueFrom:
    secretKeyRef:
      name: {{ toString $secret.name | quote }}
      key: {{ toString ($secret.key | default "pseudonym-key") | quote }}
      optional: false
{{- end }}
{{- if eq (toString ($logging.collector | default "")) "loki" }}
# The logs go to Loki (logging.collector loki, ARV-091a): Loki:Uri comes from the appsettings secret, https in k8s-prd
- name: Loki__Enabled
  value: "true"
{{- end }}
{{- if include "ariva-platform.lokiCaSecretName" . }}
# Loki's CA (ARV-200): the loki-ca volume's file, the only CA the Loki sink then trusts; Loki:Uri must be https with it
- name: Loki__RootCertificate
  value: "/app/secrets/loki-ca/ca.crt"
{{- end }}
{{- $loki := $logging.lokiCredentialsSecret -}}
{{- if not (kindIs "map" $loki) }}{{ $loki = dict }}{{ end -}}
{{- if $loki.name }}
# Loki's sender credentials (ARV-091a): HTTP basic authentication over https, from their Secret only, never optional
- name: Loki__Username
  valueFrom:
    secretKeyRef:
      name: {{ toString $loki.name | quote }}
      key: {{ toString ($loki.usernameKey | default "username") | quote }}
      optional: false
- name: Loki__Password
  valueFrom:
    secretKeyRef:
      name: {{ toString $loki.name | quote }}
      key: {{ toString ($loki.passwordKey | default "password") | quote }}
      optional: false
{{- end }}
{{- end }}

{{/*
Why the Secret logging.pseudonymKeySecret names would stop the API hosts (ARV-091a), or empty when it is fine: given a dict of
secret (the Secret as lookup returns it; an empty map when there is none to judge), name, key (the Secret's key) and reachable
(true when the release sees the cluster: lookup found the ConfigMap kube-root-ca.crt, which Kubernetes puts in every
namespace). Rendering offline (helm template) or into a namespace that does not exist yet sees neither, and the guard then
passes: the pods wait for the Secret. A reachable namespace without the Secret is refused, as are a Secret without the key and
a value the hosts would refuse (LogProtection.From): standard base64 with its padding (space, tab, CR and LF ignored, as .NET
does) of 32 or more bytes. No message ever holds the value. validate.yaml calls this with the lookups; chart-security.mjs
renders it with stand-in secrets, as helm template cannot run lookup.
Usage: {{ include "ariva-platform.pseudonymKeyRefusal" (dict "secret" (lookup "v1" "Secret" .Release.Namespace "ariva-log-pseudonym-key") "name" "ariva-log-pseudonym-key" "key" "pseudonym-key" "reachable" true) }}
*/}}
{{- define "ariva-platform.pseudonymKeyRefusal" -}}
{{- $secret := .secret -}}
{{- if not (kindIs "map" $secret) }}{{ $secret = dict }}{{ end -}}
{{- if not $secret -}}
{{- if .reachable -}}
logging.pseudonymKeySecret names the Secret {{ .name }}, which the release's namespace does not hold: create it before the release with the key {{ .key }}, base64 of 32 or more random bytes, or the API pods wait for it (ARV-091a)
{{- end -}}
{{- else -}}
{{- $data := $secret.data -}}
{{- if not (kindIs "map" $data) }}{{ $data = dict }}{{ end -}}
{{- if not (hasKey $data .key) -}}
logging.pseudonymKeySecret: the Secret {{ .name }} has no key {{ .key }}; put the log pseudonym key there (base64 of 32 or more random bytes) or name its key in logging.pseudonymKeySecret.key (ARV-091a)
{{- else -}}
{{- $text := regexReplaceAll "[ \\t\\r\\n]" (toString (index $data .key) | b64dec) "" -}}
{{- $bytes := 0 -}}
{{- if and (regexMatch "^[A-Za-z0-9+/]*={0,2}$" $text) (eq (int (mod (len $text) 4)) 0) -}}
{{- $bytes = sub (mul (div (len $text) 4) 3) (sub (len $text) (len (trimSuffix "=" (trimSuffix "=" $text)))) -}}
{{- end -}}
{{- if lt (int $bytes) 32 -}}
logging.pseudonymKeySecret: the Secret {{ .name }} holds under its key {{ .key }} no base64 of 32 or more random bytes, and the API hosts refuse to start on it (Logging:PseudonymKey); write a new key there, for example from openssl rand -base64 32 (ARV-091a)
{{- end -}}
{{- end -}}
{{- end -}}
{{- end }}

{{/*
Ingress transport and headers (ARV-084). Plain HTTP for an ingress (values ingresses[].plainHttp): refuse (the default) or
redirect. Anything else is refused by validate.yaml; the template treats it as refuse. Usage:
{{ include "ariva-platform.ingressPlainHttp" $ingress }}
*/}}
{{- define "ariva-platform.ingressPlainHttp" -}}
{{- if eq (toString (.plainHttp | default "refuse")) "redirect" }}redirect{{ else }}refuse{{ end -}}
{{- end }}

{{/*
The chart's server-snippet for an ingress (ARV-084), given its host. ingress-nginx renders it in the host's server block
(templates/nginx.tmpl, template SERVER), so it covers every response of the host, the ingress's own included: the 421 below,
the HTTPS redirect, ModSecurity's 403, 413, 502, 503 and 504. Every Ingress of a host renders the same text, as
ingress-nginx applies one server-snippet per host.
- HSTS over HTTPS only (RFC 6797 section 7.2), one year with includeSubDomains, no preload. more_set_headers (headers-more,
  which ingress-nginx builds in) applies to every status code, is inherited by every location of the server on top of their
  own, and replaces a value set before it: the controller's own HSTS, which a Lua header filter sets in each location from
  its ConfigMap (lua_ingress.header), runs first and is replaced, so the host sends one HSTS header, this one, whatever
  the controller's hsts-max-age. $ariva_hsts is set before the 421 so that response carries it too; over plain HTTP it
  stays empty, which removes the header.
- nosniff with more_set_headers, so a pod's own nosniff is replaced by the same value instead of repeated.
- Content-Security-Policy frame-ancestors 'none' with add_header always (always: on 4xx and 5xx too). add_header adds to a
  pod's own policy instead of replacing it (both are enforced; the pods already forbid framing), and the configuration-snippet
  repeats it in every location, as nginx drops a server's add_header in a location that has one of its own.
- The health probes are private (ARV-085, ASVS 5.0 V13.4.5): /health, /healthz and anything under them, in any case, answer 404
  at the server level, after the HSTS value is set and the 421. The server's rewrite phase runs before nginx picks a location,
  so no location of any Ingress on the host (prefix, exact, regex) can route them, and the 404 is the server's own response,
  as the 421 is: it carries the server's headers, and no location's add_header is involved. $uri is the normalised path
  (percent-decoding, //, /./ and /../ resolved), so /%68ealth or //health are caught as well; the kubelet probes the pods'
  own ports and never passes here.
- The live hub's URL carries the access token as access_token (ARV-035), and Ariva.Api.Main reads it on /hubs in any case
  whatever the host (JwtAuthenticationExtensions). So the token goes only to the hub's own Ingress (tokenInQuery: no access
  log, no audit log, critical errors only; ARV-091, ASVS 5.0 V16.2.5, CWE-532), refused like the probes, after them:
  - on a host with no tokenInQuery Ingress (hubHost false: api-main's own host and every machine host), /hubs and anything
    under it, in any case, answer 404, so https://<api-main host>/hubs/live?access_token=... never reaches a location whose
    logs would write it;
  - on the host of the hub (hubHost true: the web host), every spelling of /hubs but the lowercase one answers 404: nginx
    matches the hub's location by case, so /HUBS/live would reach another location (the web app, or /api) with the token.
Usage: {{ include "ariva-platform.ingressServerSnippet" (dict "host" "api-main-ariva.example.com" "hubHost" false) }}
*/}}
{{- define "ariva-platform.ingressServerSnippet" -}}
set $ariva_hsts "";
if ($scheme = https) {
  set $ariva_hsts "max-age=31536000; includeSubDomains";
}
more_set_headers "Strict-Transport-Security: $ariva_hsts";
more_set_headers "X-Content-Type-Options: nosniff";
add_header Content-Security-Policy "frame-ancestors 'none'" always;
if ($host != "{{ .host }}") {
  return 421;
}
if ($uri ~* "^/healthz?(/|$)") {
  return 404;
}
{{- if .hubHost }}
if ($uri ~ "^/(?!hubs(/|$))[Hh][Uu][Bb][Ss](/|$)") {
  return 404;
}
{{- else }}
if ($uri ~* "^/hubs(/|$)") {
  return 404;
}
{{- end }}
{{ end }}

{{/*
The chart's configuration-snippet for an ingress (ARV-084), rendered by ingress-nginx in each of its locations: the
frame-ancestors policy again (see ingressServerSnippet), and with plainHttp refuse a 403 for any request over plain HTTP
(ASVS 5.0 V4.1.2: machine endpoints never redirect, so a client that sends credentials in clear is told, not silently
redirected). It runs in the rewrite phase, before ingress-nginx's own redirect (rewrite_by_lua, at the end of that phase),
and only in this Ingress's locations, so an ACME HTTP-01 solver Ingress on the same host keeps plain HTTP. The live hub
logs only critical errors (ARV-035, tokenInQuery). Usage: {{ include "ariva-platform.ingressConfigurationSnippet" $ingress }}
*/}}
{{- define "ariva-platform.ingressConfigurationSnippet" -}}
add_header Content-Security-Policy "frame-ancestors 'none'" always;
{{- if eq (include "ariva-platform.ingressPlainHttp" .) "refuse" }}
if ($scheme = http) {
  return 403;
}
{{- end }}
{{- if .tokenInQuery }}
error_log /dev/stderr crit;
{{- end }}
{{ end }}

{{/*
The annotations the chart sets on an ingress (ARV-084, with ARV-035, ARV-082b and ARV-091), as YAML. ingresses.yaml renders them and
appends a site's server-snippet, configuration-snippet and modsecurity-snippet to the chart's own; validate.yaml refuses a
site annotation for any other key here. Usage: {{ include "ariva-platform.ingressChartAnnotations" (list $ $ingress) | fromYaml }}
- plain HTTP: refuse sets ssl-redirect and force-ssl-redirect false (the configuration-snippet answers 403), redirect sets
  both true (308 to HTTPS); ariva.dalil.tech/plain-http records the mode for scripts/ops/tls-check.mjs (ingress-nginx
  ignores it).
- the affinity cookie: Secure, SameSite=Strict, Path=/ (ASVS 5.0 V3.3.1, V3.3.2).
- ssl-prefer-server-ciphers false: the client's order among the controller's suites, as the Mozilla intermediate
  configuration says (guidelines 6.0, server_preferred_order false); ingress-nginx writes ssl_prefer_server_ciphers on at the
  http level whenever ssl-ciphers is set and has no ConfigMap key for it, so the host's server block turns it off.
- ModSecurity with the upload rule; its message is in escaped quotes because ingress-nginx wraps the snippet in
  modsecurity_rules '...', where a bare quote ends the string and nginx refuses the whole configuration.
- The ModSecurity audit log off on every Ingress (ARV-091, ASVS 5.0 V16.2.5, CWE-532): SecAuditEngine Off, last in the
  snippet, so it wins over anything before it in the location. The controller's modsecurity.conf (ModSecurity's recommended
  file) audits 5xx and 4xx but 404 with parts ABIJDEFHZ, whose part B is the request line with its query string and every
  request header (an Authorization bearer token, the refresh cookie); part C, which a site could add, is the request body (a
  sign-in's password). ingress-nginx 1.15 loads that file in a location only when
  the location has no modsecurity-snippet, and at the http level when enable-modsecurity is on for the whole controller;
  libmodsecurity lets the http level's audit settings override a location's, so this Off holds only while the controller
  values keep enable-modsecurity "false" (Helm/ingress-nginx-values.yaml, checked by controllerValuesFindings). Rule matches
  still reach the controller's error log as ModSecurity messages: a deny (the upload rule's 403) at error level, with nginx's
  request line, query string included; other matches at info, which the controller values never log. A site that adds the
  OWASP CRS (enable-owasp-core-rules; ingress-nginx writes its file after this snippet, and the CRS sets no audit engine but
  Off) keeps the audit log off and gets one deny line per blocked request (its anomaly score); the CRS's own matches, with
  excerpts of the matched input (Matched Data ... found within ARGS:...), are info. validate.yaml refuses a site annotation
  that would turn the audit log back on, write a debug log or copy request values into a rule's message.
- The live hub's Ingress (tokenInQuery) also keeps no access log, its configuration-snippet logs only critical errors, and
  OpenTelemetry stays off on it (enable-opentelemetry "false", a key of the chart's, so no site annotation turns it on): the
  ingress's OpenTelemetry module exports the request target with its query string (http.target), the access token included.
  With the controller's OpenTelemetry off, as Ariva's values require, ingress-nginx writes nothing for it.
- hubHost for the server-snippet: true when an Ingress of this host has tokenInQuery (ingressServerSnippet).
- sourceRanges (ARV-085), when given: allowlist-source-range, the CIDRs joined by commas. ingress-nginx 1.15 reads that name
  (internal/ingress/annotations/ipallowlist) and falls back to whitelist-source-range, its alias, only when it is absent or
  empty; it writes allow for each CIDR and deny all in each of the Ingress's locations, so any other client gets 403.
*/}}
{{- define "ariva-platform.ingressChartAnnotations" -}}
{{- $root := index . 0 -}}
{{- $ingress := index . 1 -}}
{{- $host := printf "%s.%s" $ingress.subdomain $root.Values.domain -}}
{{- $hubHost := false -}}
{{- range ($root.Values.ingresses | default list) -}}
{{- if and .tokenInQuery (eq (toString .subdomain) (toString $ingress.subdomain)) }}{{ $hubHost = true }}{{ end -}}
{{- end -}}
{{- $plain := include "ariva-platform.ingressPlainHttp" $ingress -}}
{{- $redirect := ternary "true" "false" (eq $plain "redirect") -}}
{{- $a := dict -}}
{{- $_ := set $a "ariva.dalil.tech/plain-http" $plain -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/ssl-redirect" $redirect -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/force-ssl-redirect" $redirect -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/ssl-prefer-server-ciphers" "false" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/affinity" "cookie" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/session-cookie-name" (printf "ariva-%s" $ingress.name) -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/session-cookie-secure" "true" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/session-cookie-samesite" "Strict" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/session-cookie-path" "/" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/server-snippet" (include "ariva-platform.ingressServerSnippet" (dict "host" $host "hubHost" $hubHost)) -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/configuration-snippet" (include "ariva-platform.ingressConfigurationSnippet" $ingress) -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/enable-modsecurity" "true" -}}
{{- $modsecurity := "SecRuleEngine On\nSecRule FILES \"@rx \\.(exe|dll|bat|cmd|com|msi|sh|ps1|php|jsp|asp|aspx)$\" \"id:100001,phase:2,deny,status:403,log,msg:\\'Dangerous file upload blocked\\'\"\nSecAuditEngine Off\n" -}}
{{- if $ingress.tokenInQuery -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/enable-access-log" "false" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/enable-opentelemetry" "false" -}}
{{- end -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/modsecurity-snippet" $modsecurity -}}
{{- with $ingress.sourceRanges -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/allowlist-source-range" (join "," .) -}}
{{- end -}}
{{- if include "ariva-platform.podTls" $root -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/backend-protocol" "HTTPS" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/proxy-ssl-secret" (printf "%s/%s" $root.Release.Namespace ($root.Values.podTls.caSecretName | default "ariva-cluster-ca")) -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/proxy-ssl-verify" "on" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/proxy-ssl-verify-depth" "2" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/proxy-ssl-name" (printf "%s.%s.svc" $ingress.serviceName $root.Release.Namespace) -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/proxy-ssl-server-name" "on" -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/proxy-ssl-protocols" "TLSv1.2 TLSv1.3" -}}
{{- end -}}
{{- toYaml $a -}}
{{- end }}

{{/*
The trusted proxy networks of every API host (ARV-085; values ingressController.podNetworks): the ingress controller's pod
network as Security:ForwardedHeaders:KnownNetworks, one variable per CIDR in order (__0, __1 ...), the names the hosts bind.
validate.yaml checks the CIDRs (required in k8s-prd, IPv4 /14 and IPv6 /48 at the widest there, never /0).
Usage: {{- include "ariva-platform.forwardedHeadersEnv" . | nindent 12 }}, inside {{- if (.Values.ingressController | default dict).podNetworks }}
*/}}
{{- define "ariva-platform.forwardedHeadersEnv" -}}
{{- range $index, $network := ((.Values.ingressController | default dict).podNetworks | default list) }}
- name: Security__ForwardedHeaders__KnownNetworks__{{ $index }}
  value: {{ toString $network | quote }}
{{- end }}
{{- end }}

{{/*
Why a CIDR is refused (ARV-085), or empty when it is fine: given a dict of cidr, v4 and v6 (the shortest prefix allowed for an
IPv4 and an IPv6 network; 0 allows any). A CIDR is an IPv4 address in dotted decimal or an IPv6 address in its hexadecimal
form (with at most one ::, no zone, no embedded IPv4), then / and the prefix length; its address is the network's (no bit set
after the prefix), as the hosts (System.Net.IPNetwork) and Kubernetes' strict validation require.
Usage: {{ include "ariva-platform.cidrRefusal" (dict "cidr" "10.244.0.0/16" "v4" 14 "v6" 48) }}
*/}}
{{- define "ariva-platform.cidrRefusal" -}}
{{- $cidr := toString .cidr -}}
{{- $v4 := int (.v4 | default 0) -}}
{{- $v6 := int (.v6 | default 0) -}}
{{- $octet := "(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])" -}}
{{- if regexMatch (printf "^(%s\\.){3}%s/([0-9]|[12][0-9]|3[0-2])$" $octet $octet) $cidr -}}
{{- $parts := splitList "/" $cidr -}}
{{- $prefix := atoi (index $parts 1) -}}
{{- $octets := splitList "." (index $parts 0) -}}
{{- $value := add (mul (atoi (index $octets 0)) 16777216) (mul (atoi (index $octets 1)) 65536) (mul (atoi (index $octets 2)) 256) (atoi (index $octets 3)) -}}
{{- $block := 1 -}}
{{- range until (int (sub 32 $prefix)) }}{{ $block = mul $block 2 }}{{ end -}}
{{- if ne (int (mod $value $block)) 0 -}}
{{ $cidr }} is not a network address: the bits after its /{{ $prefix }} prefix must be zero
{{- else if lt $prefix $v4 -}}
{{ $cidr }} is wider than /{{ $v4 }}
{{- end -}}
{{- else if regexMatch "^[0-9A-Fa-f:]+/([0-9]|[1-9][0-9]|1[01][0-9]|12[0-8])$" $cidr -}}
{{- $parts := splitList "/" $cidr -}}
{{- $prefix := atoi (index $parts 1) -}}
{{- $halves := splitList "::" (index $parts 0) -}}
{{- $left := list -}}
{{- $right := list -}}
{{- if ne (index $halves 0) "" }}{{ $left = splitList ":" (index $halves 0) }}{{ end -}}
{{- if and (eq (len $halves) 2) (ne (index $halves 1) "") }}{{ $right = splitList ":" (index $halves 1) }}{{ end -}}
{{- $wellFormed := true -}}
{{- range (concat $left $right) }}{{ if not (regexMatch "^[0-9A-Fa-f]{1,4}$" .) }}{{ $wellFormed = false }}{{ end }}{{ end -}}
{{- $count := add (len $left) (len $right) -}}
{{- if or (gt (len $halves) 2) (not $wellFormed) (and (eq (len $halves) 1) (ne (int $count) 8)) (and (eq (len $halves) 2) (gt (int $count) 7)) -}}
{{ $cidr }} is not a CIDR (an IPv4 or IPv6 network address and its prefix length)
{{- else -}}
{{- $groups := $left -}}
{{- range until (int (sub 8 $count)) }}{{ $groups = append $groups "0" }}{{ end -}}
{{- $groups = concat $groups $right -}}
{{- $digits := dict "0" 0 "1" 1 "2" 2 "3" 3 "4" 4 "5" 5 "6" 6 "7" 7 "8" 8 "9" 9 "a" 10 "b" 11 "c" 12 "d" 13 "e" 14 "f" 15 -}}
{{- $hostBits := false -}}
{{- range $i, $group := $groups -}}
{{- $value := 0 -}}
{{- range (splitList "" (lower $group)) }}{{ $value = add (mul $value 16) (index $digits .) }}{{ end -}}
{{- $start := mul $i 16 -}}
{{- if le (int $prefix) (int $start) -}}
{{- if ne (int $value) 0 }}{{ $hostBits = true }}{{ end -}}
{{- else if lt (int $prefix) (int (add $start 16)) -}}
{{- $block := 1 -}}
{{- range until (int (sub (add $start 16) $prefix)) }}{{ $block = mul $block 2 }}{{ end -}}
{{- if ne (int (mod $value $block)) 0 }}{{ $hostBits = true }}{{ end -}}
{{- end -}}
{{- end -}}
{{- if $hostBits -}}
{{ $cidr }} is not a network address: the bits after its /{{ $prefix }} prefix must be zero
{{- else if lt $prefix $v6 -}}
{{ $cidr }} is wider than /{{ $v6 }}
{{- end -}}
{{- end -}}
{{- else -}}
{{ $cidr }} is not a CIDR (an IPv4 or IPv6 network address and its prefix length)
{{- end -}}
{{- end }}

{{/*
Why a label selector of a NetworkPolicy peer is refused (ARV-085), or empty: given a dict of selector and where. A selector is
a map of matchLabels (a map of strings) and matchExpressions (a list of key, operator In, NotIn, Exists or DoesNotExist, and
values), nothing else.
*/}}
{{- define "ariva-platform.selectorRefusal" -}}
{{- $selector := .selector -}}
{{- if not (kindIs "map" $selector) -}}
{{ .where }} must be a label selector (matchLabels, matchExpressions)
{{- else if without (keys $selector) "matchLabels" "matchExpressions" -}}
{{ .where }} has {{ without (keys $selector) "matchLabels" "matchExpressions" | sortAlpha | join ", " }}: a label selector has matchLabels and matchExpressions only
{{- else if and (hasKey $selector "matchLabels") (not (kindIs "map" $selector.matchLabels)) -}}
{{ .where }}.matchLabels must be a map of label to value
{{- else if and (hasKey $selector "matchExpressions") (not (kindIs "slice" $selector.matchExpressions)) -}}
{{ .where }}.matchExpressions must be a list
{{- else -}}
{{- $refusal := "" -}}
{{- range $label, $value := ($selector.matchLabels | default dict) }}{{ if not (kindIs "string" $value) }}{{ $refusal = printf "%s.matchLabels.%s must be a string" $.where $label }}{{ end }}{{ end -}}
{{- range ($selector.matchExpressions | default list) }}{{ if or (not (kindIs "map" .)) (not (has (toString (.operator | default "")) (list "In" "NotIn" "Exists" "DoesNotExist"))) (empty (toString (.key | default ""))) }}{{ $refusal = printf "%s.matchExpressions: each is a key, an operator In, NotIn, Exists or DoesNotExist and its values" $.where }}{{ end }}{{ end -}}
{{- $refusal -}}
{{- end -}}
{{- end }}

{{/*
True ("true") when a label selector selects everything (no matchLabels entry and no matchExpressions), empty otherwise.
*/}}
{{- define "ariva-platform.selectsAll" -}}
{{- if and (empty ((. | default dict).matchLabels | default dict)) (empty ((. | default dict).matchExpressions | default list)) }}true{{ end -}}
{{- end }}

{{/*
Why a NetworkPolicy peer of the values is refused (ARV-085), or empty: given a dict of peer, where and prd (k8s-prd). A peer is
an ipBlock (cidr and optional except, valid CIDRs) or a podSelector with an optional namespaceSelector (both label selectors);
never both, never empty. In k8s-prd: no ipBlock wider than /8 (IPv4) or /16 (IPv6), so never 0.0.0.0/0 or ::/0, and no
namespaceSelector that selects every namespace without a podSelector that narrows it.
*/}}
{{- define "ariva-platform.peerRefusal" -}}
{{- $peer := .peer -}}
{{- $where := .where -}}
{{- if or (not (kindIs "map" $peer)) (empty $peer) -}}
{{ $where }} must be a NetworkPolicy peer: an ipBlock, or a podSelector with an optional namespaceSelector
{{- else if without (keys $peer) "ipBlock" "namespaceSelector" "podSelector" -}}
{{ $where }} has {{ without (keys $peer) "ipBlock" "namespaceSelector" "podSelector" | sortAlpha | join ", " }}: a peer has ipBlock, podSelector and namespaceSelector only
{{- else if hasKey $peer "ipBlock" -}}
{{- if or (hasKey $peer "podSelector") (hasKey $peer "namespaceSelector") -}}
{{ $where }} gives ipBlock with a selector; a peer is one or the other
{{- else if or (not (kindIs "map" $peer.ipBlock)) (without (keys ($peer.ipBlock | default dict)) "cidr" "except") (not (kindIs "slice" ($peer.ipBlock.except | default list))) -}}
{{ $where }}.ipBlock must be a cidr and an optional list except
{{- else -}}
{{- $v4 := ternary 8 0 (eq (toString .prd) "true") -}}
{{- $v6 := ternary 16 0 (eq (toString .prd) "true") -}}
{{- $refusal := include "ariva-platform.cidrRefusal" (dict "cidr" $peer.ipBlock.cidr "v4" $v4 "v6" $v6) -}}
{{- range ($peer.ipBlock.except | default list) }}{{ if not $refusal }}{{ $refusal = include "ariva-platform.cidrRefusal" (dict "cidr" . "v4" 0 "v6" 0) }}{{ end }}{{ end -}}
{{- if $refusal }}{{ $where }}.ipBlock: {{ $refusal }}{{ if eq (toString .prd) "true" }} (k8s-prd opens no destination wider than /8 for IPv4 or /16 for IPv6){{ end }}{{ end -}}
{{- end -}}
{{- else -}}
{{- $refusal := "" -}}
{{- if hasKey $peer "podSelector" }}{{ $refusal = include "ariva-platform.selectorRefusal" (dict "selector" $peer.podSelector "where" (printf "%s.podSelector" $where)) }}{{ end -}}
{{- if and (not $refusal) (hasKey $peer "namespaceSelector") }}{{ $refusal = include "ariva-platform.selectorRefusal" (dict "selector" $peer.namespaceSelector "where" (printf "%s.namespaceSelector" $where)) }}{{ end -}}
{{- if and (not $refusal) (not (hasKey $peer "podSelector")) (not (hasKey $peer "namespaceSelector")) }}{{ $refusal = printf "%s selects nothing" $where }}{{ end -}}
{{- if and (not $refusal) (eq (toString .prd) "true") (hasKey $peer "namespaceSelector") (include "ariva-platform.selectsAll" $peer.namespaceSelector) (include "ariva-platform.selectsAll" $peer.podSelector) -}}
{{- $refusal = printf "%s selects every pod of every namespace; k8s-prd names the namespace or the pods" $where -}}
{{- end -}}
{{- $refusal -}}
{{- end -}}
{{- end }}

{{/*
Why a list of TCP ports of the values is refused (ARV-085), or empty: given a dict of ports and where. One or more whole
numbers from 1 to 65535.
*/}}
{{- define "ariva-platform.portsRefusal" -}}
{{- if or (not (kindIs "slice" .ports)) (empty .ports) -}}
{{ .where }} must be a list of one or more TCP ports
{{- else -}}
{{- $refusal := "" -}}
{{- range .ports -}}
{{- $number := or (kindIs "float64" .) (kindIs "int64" .) (kindIs "int" .) -}}
{{- if or (not $number) (lt (float64 .) 1.0) (gt (float64 .) 65535.0) (ne (float64 .) (float64 (int .))) -}}
{{- $refusal = printf "%s: %v is not a port (a whole number from 1 to 65535)" $.where . -}}
{{- end -}}
{{- end -}}
{{- $refusal -}}
{{- end -}}
{{- end }}

{{/*
Why an IPv6 trusted proxy network would trust IPv4 clients too widely (ARV-085, the hosts' rule in ForwardedHeadersExtensions,
k8s-prd), or empty: given a CIDR that ariva-platform.cidrRefusal accepts. A dual-stack pod (Kestrel on https://*:8443) sees an
IPv4 client as ::ffff:a.b.c.d, and .NET 10's IPNetwork.Contains judges that address against an IPv6 network with IPv4 arithmetic:
the network's last 32 bits under a mask of (prefix mod 32) bits (32 when that is 0). So a prefix from /65 to /77 or /97 to /109
(a mask under /14) trusts IPv4 clients wider than a /14: fd00:10:244::/65 trusts 0.0.0.0/1, fd00::a00:0/104 trusts 10.0.0.0/8,
and Cilium's default IPv6 pool fd00::/104 trusts 0.0.0.0/8. A network of /96 or wider that holds the whole IPv4-mapped block
::ffff:0:0/96 (::/64, ::/80, ::ffff:0:0/96 itself) trusts every IPv4 client by the addresses it holds. The hosts refuse both
at start; so does the chart. An IPv4 network is never refused here.
Usage: {{ include "ariva-platform.mappedClientsRefusal" "fd00:10::/48" }}
*/}}
{{- define "ariva-platform.mappedClientsRefusal" -}}
{{- $cidr := toString . -}}
{{- if and (contains ":" $cidr) (regexMatch "^[0-9A-Fa-f:]+/[0-9]{1,3}$" $cidr) -}}
{{- $parts := splitList "/" $cidr -}}
{{- $prefix := atoi (index $parts 1) -}}
{{- $mask := mod $prefix 32 | int -}}
{{- if eq $mask 0 }}{{ $mask = 32 }}{{ end -}}
{{- $halves := splitList "::" (index $parts 0) -}}
{{- $left := list -}}
{{- $right := list -}}
{{- if ne (index $halves 0) "" }}{{ $left = splitList ":" (index $halves 0) }}{{ end -}}
{{- if and (eq (len $halves) 2) (ne (index $halves 1) "") }}{{ $right = splitList ":" (index $halves 1) }}{{ end -}}
{{- $groups := $left -}}
{{- range until (int (sub 8 (add (len $left) (len $right)))) }}{{ $groups = append $groups "0" }}{{ end -}}
{{- $groups = concat $groups $right -}}
{{- $digits := dict "0" 0 "1" 1 "2" 2 "3" 3 "4" 4 "5" 5 "6" 6 "7" 7 "8" 8 "9" 9 "a" 10 "b" 11 "c" 12 "d" 13 "e" 14 "f" 15 -}}
{{- $values := list -}}
{{- range $groups -}}
{{- $value := 0 -}}
{{- range (splitList "" (lower .)) }}{{ $value = add (mul $value 16) (index $digits .) }}{{ end -}}
{{- $values = append $values (int $value) -}}
{{- end -}}
{{- /* The mapped block's sixth group (bits 80 to 95, 0xffff) under the prefix; the other groups of a network holding it are 0. */ -}}
{{- $sixth := 0 -}}
{{- if gt $prefix 80 }}{{ $block := 1 }}{{ range until (int (sub 96 $prefix)) }}{{ $block = mul $block 2 }}{{ end }}{{ $sixth = int (mul (div 65535 $block) $block) }}{{ end -}}
{{- $holdsMapped := and (le $prefix 96) (eq (index $values 5) $sixth) -}}
{{- range $i, $value := $values }}{{ if and (ne $i 5) (ne $value 0) }}{{ $holdsMapped = false }}{{ end }}{{ end -}}
{{- if or (lt $mask 14) $holdsMapped -}}
{{ $cidr }} trusts IPv4 clients wider than an IPv4 /14: a dual-stack pod sees them as IPv4-mapped addresses (::ffff:a.b.c.d), and .NET matches those against an IPv6 network with a mask of its prefix mod 32 bits, so k8s-prd takes no IPv6 network that holds the IPv4-mapped block ::ffff:0:0/96 and no prefix from /65 to /77 or from /97 to /109
{{- end -}}
{{- end -}}
{{- end }}
