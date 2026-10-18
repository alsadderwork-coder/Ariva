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
