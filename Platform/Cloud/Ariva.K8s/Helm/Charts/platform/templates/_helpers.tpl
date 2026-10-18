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
OpenTelemetry environment variables shared by the .NET hosts. The service name comes from
Application:Name in appsettings; these add the export target and the resource attributes.
*/}}
{{- define "ariva-platform.otelEnv" -}}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ .Values.otel.endpoint | default "" | quote }}
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: {{ .Values.otel.protocol | default "grpc" | quote }}
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
