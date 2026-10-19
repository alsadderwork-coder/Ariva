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
Usage: {{ include "ariva-platform.ingressServerSnippet" "api-main-ariva.example.com" }}
*/}}
{{- define "ariva-platform.ingressServerSnippet" -}}
set $ariva_hsts "";
if ($scheme = https) {
  set $ariva_hsts "max-age=31536000; includeSubDomains";
}
more_set_headers "Strict-Transport-Security: $ariva_hsts";
more_set_headers "X-Content-Type-Options: nosniff";
add_header Content-Security-Policy "frame-ancestors 'none'" always;
if ($host != "{{ . }}") {
  return 421;
}
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
The annotations the chart sets on an ingress (ARV-084, with ARV-035 and ARV-082b), as YAML. ingresses.yaml renders them and
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
*/}}
{{- define "ariva-platform.ingressChartAnnotations" -}}
{{- $root := index . 0 -}}
{{- $ingress := index . 1 -}}
{{- $host := printf "%s.%s" $ingress.subdomain $root.Values.domain -}}
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
{{- $_ := set $a "nginx.ingress.kubernetes.io/server-snippet" (include "ariva-platform.ingressServerSnippet" $host) -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/configuration-snippet" (include "ariva-platform.ingressConfigurationSnippet" $ingress) -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/enable-modsecurity" "true" -}}
{{- $modsecurity := "SecRuleEngine On\nSecRule FILES \"@rx \\.(exe|dll|bat|cmd|com|msi|sh|ps1|php|jsp|asp|aspx)$\" \"id:100001,phase:2,deny,status:403,log,msg:\\'Dangerous file upload blocked\\'\"\n" -}}
{{- if $ingress.tokenInQuery -}}
{{- $modsecurity = printf "%sSecAuditEngine Off\n" $modsecurity -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/enable-access-log" "false" -}}
{{- end -}}
{{- $_ := set $a "nginx.ingress.kubernetes.io/modsecurity-snippet" $modsecurity -}}
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
