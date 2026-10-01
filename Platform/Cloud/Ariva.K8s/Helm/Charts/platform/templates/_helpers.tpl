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
