// Chart security test (ARV-002, ARV-062, CWE-269). Renders Charts/platform for every environment and Charts/timescaledb,
// and fails when a workload lacks the pod or container security context, a writable /tmp, a pinned image tag (a digest
// for third-party images), carries a credential in its manifest, a StatefulSet has no NetworkPolicy, or an ingress
// lacks TLS. It also proves the release guards: production without a build number, the "latest" tag, the demo seed in
// production (ARV-019), the MQTT transport without its TLS secret (ARV-024), a database image without a digest,
// passwords without a secret or the superuser as the migration login (ARV-062), and the validation reader login without its
// secret or optional in production (ARV-104g1) must fail to render; the reader login reaches only api-main and the migration job.
// ARV-125b: every render ships the SigNoz alert rules the story names and the dashboards, as ConfigMaps only, valid JSON with
// a severity, a runbook link and resolved numbers, labels from tests/monitoring.json and no credential; thresholds out of
// range fail to render; every query reads its own release (the environment, or the namespaces for the external metrics);
// the AMAN feed rule appears where it is turned on and nothing appears with monitoring off.
// ARV-111a: the migration job carries each environment's retention periods (migration.retention) as Database__Retention__*, and a
// period out of range, not a whole number, unknown or missing fails to render.
// ARV-127: the offline bundle's Helmfile renders the same objects as helmfile-k8s, every image from the site's mirror by digest.
// ARV-082a: the five API hosts and the migration and topics jobs, and only they, mount the database and Redis CAs (ca.crt only,
// read-only) where the hosts read them in k8s-prd; production refuses an OTLP endpoint that is not https; TimescaleDB renders
// without TLS by default and in dev, demo and localk8s, and with TLS in prd (ssl on, the certificate and a key PostgreSQL
// accepts from tls.secretName, TLS 1.2 at least, pg_hba.conf taking TCP over TLS only), which no site file and no state value
// turns off.
// ARV-082b: with podTls.enabled (prd, which refuses it off in the chart and in both Helmfiles) the five API hosts and the web
// pod serve TLS only (no 8080 or 3000, probes HTTPS, the certificate read-only as a whole directory with only tls.crt and
// tls.key, nginx's listener a ConfigMap of TLS listeners), every Service serves 443 to https and every Ingress proxies HTTPS
// and verifies the pods against the cluster CA under the backend's in-cluster name; dev, demo and localk8s stay plain; the web
// image's default.conf takes its listener from listen.d and the image ships the plain one.
// ARV-084: every Ingress of every render (dev, demo, localk8s, prd, the Helmfile environments and the offline bundle's) sends
// HSTS of a year with includeSubDomains over HTTPS only, nosniff and frame-ancestors 'none' on every response, its own
// included (ingressHardeningFindings); the machine hosts refuse plain HTTP in prd and the browser hosts redirect; the affinity
// cookie is Secure, SameSite=Strict, Path=/; ssl-prefer-server-ciphers is false; the release guards refuse a machine host that
// redirects in prd, a plainHttp other than refuse or redirect, a host whose Ingresses disagree, a site annotation that sets a
// key of the chart's, from-to-www-redirect, custom-headers or names the headers; the controller values the site applies
// (Helm/ingress-nginx-values.yaml) hold TLS 1.2 and 1.3, Mozilla's intermediate suites, HSTS and the snippet settings.
// ARV-085: with networkPolicies.enabled (prd, which refuses it off in the chart and in both Helmfiles) every Ariva pod is denied
// by default and gets exactly the firewall matrix's flows, compared with the release's values (networkPolicyFindings); no rule
// is a wildcard and production opens no network wider than /8 (IPv4) or /16 (IPv6); dev, demo and localk8s render none. Every
// Ingress routes no health probe and its server-snippet answers them 404 at the server level, after HSTS (ingressPrivacyFindings);
// the Cronz dashboard has its source range in prd; the API hosts get exactly ingressController.podNetworks as their trusted
// proxies, /14 and /48 at the widest in prd (forwardedHeadersFindings); the release guards refuse a malformed CIDR, peer or port.
// ARV-091 (ASVS 5.0 V16.2.5, CWE-532): every Ingress keeps the ModSecurity audit log off (checkManifests); every host without the
// live hub's Ingress answers /hubs 404 in any case at its server level, and the hub's host every other spelling of it
// (hubRefusalFindings); the controller values keep ModSecurity off for the whole controller and write the access log without
// the query string (ingress-nginx 1.15's default format otherwise); the release guards refuse a site annotation that turns the
// audit log back on, writes a modsecurity, access_log or error_log directive, or routes /hubs without tokenInQuery.
// ARV-091a (ASVS 5.0 V16.4.2, V16.4.3, CWE-532): with logging.pseudonymKeySecret (prd, required there by the chart and by both
// Helmfiles through the state value logShipping) the five API hosts, and only they, get Logging__PseudonymKey from that Secret
// (secretKeyRef, not optional, never a literal or a ConfigMap), and from logging.lokiCredentialsSecret Loki__Username and
// Loki__Password alike; with logging.collector loki they get Loki__Enabled "true", and with otlp the https otel.endpoint
// (logShippingFindings); dev, demo and localk8s render neither. The release guards refuse prd
// without a collector or the Secret, otlp without an https endpoint, loki or otlp without their peers under the NetworkPolicies,
// and any value that could hold the key itself; the Secret guard (lookup) is judged on stand-in secrets.
// ARV-200 (ASVS 5.0 V12.3.1, V12.3.2, V16.4.3): with otel.caSecretName the five API hosts and the simulator, and with
// logging.lokiCaSecretName the five API hosts, and only they, mount that Secret's ca.crt alone (read-only and whole, mode 0444,
// not optional) at /app/secrets/otlp-ca or /app/secrets/loki-ca and get Otlp__RootCertificate or Loki__RootCertificate, the
// mounted file (collectorCaFindings); nothing when the values are empty, never in the web pod or the jobs. The release guards
// refuse a name that is not a Kubernetes name, the OTLP collector's CA without an https otel.endpoint and Loki's without
// logging.collector loki; the Secret guard (lookup: the Secret exists and holds ca.crt and nothing else, a PEM certificate
// without a private key) is judged on stand-in secrets.
// ARV-092c (ASVS 5.0 V5.4.3, V12.3.1; CWE-269): with uploadScan on (every shipped environment; prd refuses it off in the chart and
// in both Helmfiles) api-main's pod, and only it, runs clamd beside api-main on 127.0.0.1:3310 and api-main's container gets that
// address; freshclam and an init container that copies the image's signatures with source public or mirror, neither with
// mounted; every scanner container the pinned image by digest, as the image's clamav user, read-only root, no capability, its
// own resources and exactly its own mounts (the signatures read-only in clamd), no Secret but the mirror's CA in freshclam, no
// readiness or startup probe (a liveness probe only, after the load and longer than a reload); clamd.conf on the loopback with
// the repository's limits; the HPA on api-main's own container
// (uploadScanFindings). The release guards refuse prd without the scan or without a signature source, mirror without a URL or
// with one that could carry a credential (never repeated in the message), mounted without a claim, and the NetworkPolicies on
// without the mirror's peers; each rule is self-tested on a render's fixture, a writable root, a missing digest and the
// address on another host among the mutations. prd renders carry a site's choice (tests/fixtures/prd-site.yaml).
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs              needs helm 3 on PATH (or HELM=path)
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs --self-test  checks the rules against fixtures, no helm
// Without helm the test is skipped locally and fails in CI (CI=true).
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parse as parseYaml, parseAllDocuments, stringify } from 'yaml';
import { checkManifests, COLLECTOR_CAS, collectorCaFindings, controllerValuesFindings, DATABASE_TLS12_CIPHERS, databaseTlsFindings, forwardedHeadersFindings, HUB_PATHS, hubRefusalFindings, ingressHardeningFindings, INGRESS_HARDENING,
	INGRESS_LOG_FORMAT, ingressPrivacyFindings, logShippingFindings, API_HOSTS, missingMonitoringRules, networkPolicyFindings, networkValues, POD_TLS, podTlsFindings, podTlsSecrets, serverCaSecrets, trustsMappedClients,
	clamavDirectives, UPLOAD_SCAN, uploadScanFindings } from './checks.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const chart = path.resolve(here, '..', 'Helm', 'Charts', 'platform');
const databaseChart = path.resolve(here, '..', 'Helm', 'Charts', 'timescaledb');
const helm = process.env.HELM || 'helm';
const helmfile = process.env.HELMFILE || 'helmfile';
const helmfileState = path.resolve(here, '..', 'Helm', 'helmfile-k8s.yaml.gotmpl');

const parse = (text) => parseAllDocuments(text).map((doc) => doc.toJS()).filter(Boolean);
// As Kubernetes reads a manifest (YAML 1.1: a file mode such as 0440 is octal), for the checks that read file modes.
const parseAsKubernetes = (text) => parseAllDocuments(text, { version: '1.1' }).map((doc) => doc.toJS()).filter(Boolean);
const fixture = (name) => fs.readFileSync(path.join(here, 'fixtures', name), 'utf8');
/**
 * A production site's own values: its signature source (ARV-092c: its mirror with its CA, and the mirror's peers) and its
 * database's max_connections (ARV-232). k8s-prd has no default for either and the chart refuses a release without them, so every
 * render of values-k8s-prd.yaml takes this file right after it (render, mergedValues, the Helmfile's prd), as a site's own values
 * would; the guard cases clear it to prove the refusals.
 */
const PRD_SITE = path.join(here, 'fixtures', 'prd-site.yaml');
/** ARV-092c: the chart's ClamAV pin (values.yaml uploadScan.image): repository, tag and digest. */
const CHART_SCANNER = parseYaml(fs.readFileSync(path.join(chart, 'values.yaml'), 'utf8')).uploadScan.image;
/** ARV-092c: the repository's clamd.conf (ARV-092b), whose limits the chart's clamd.conf must keep. */
const REPOSITORY_CLAMD = clamavDirectives(fs.readFileSync(path.resolve(here, '..', '..', 'clamav', 'clamd.conf'), 'utf8'));

/** ARV-082a: rules of databaseTlsFindings that did not fire on the TLS fixtures, or a good fixture that produced findings. */
function databaseTlsSelfTest() {
	const failures = [];
	const good = parseAsKubernetes(fixture('database-tls-good.yaml'));
	const bad = parseAsKubernetes(fixture('database-tls-bad.yaml'));
	const clean = [...checkManifests(good, { environment: 'k8s-prd' }), ...databaseTlsFindings(good, { tls: true }),
		...databaseTlsFindings(parseAsKubernetes(fixture('good.yaml')), { tls: false })];
	if (clean.length) failures.push(`the good TLS fixtures produced findings:\n   ${clean.join('\n   ')}`);
	const findings = databaseTlsFindings(bad, { tls: true });
	const expected = [
		'tls-flags container db: ssl is not on', 'tls-flags container db: ssl_cert_file does not come from the secret timescaledb-tls',
		'tls-flags container db: PostgreSQL refuses the key file as mounted (mode 0444)', 'tls-flags container db: ssl_min_protocol_version must be TLSv1.2 or TLSv1.3, not TLSv1.1',
		'tls-hba container db hba_file: the local socket must keep peer authentication, not trust', 'tls-hba container db hba_file: pg_hba.conf accepts TCP connections without TLS (host all all all scram-sha-256)',
		'pg_hba.conf accepts TCP connections without TLS (hostnogssenc all all 10.0.0.0 255.0.0.0 md5)', 'tls-hba container db hba_file: hostssl must use scram-sha-256 or cert, not trust',
		'tls-hba container db hba_file: pg_hba.conf must not include other files', 'tls-hba container db hba_file: pg_hba.conf must reject every TCP connection without TLS',
		'tls-key container db: ssl_key_file does not come from the secret timescaledb-tls (key tls.key)', "tls-key container db: hba_file must be the release's pg_hba.conf",
		'tls-reject-all container db hba_file: pg_hba.conf takes no TCP connection over TLS',
		// V12.1.2: TLS 1.2 with ECDHE and AEAD only, not PostgreSQL's default list and not left unset.
		`tls-hba container db: ssl_ciphers must be ${DATABASE_TLS12_CIPHERS} (TLS 1.2 with ECDHE and AEAD only, V12.1.2), not HIGH:MEDIUM:+3DES:!aNULL`,
		`tls-key container db: ssl_ciphers must be ${DATABASE_TLS12_CIPHERS} (TLS 1.2 with ECDHE and AEAD only, V12.1.2), not unset`
	];
	const missing = expected.filter((rule) => !findings.some((finding) => finding.includes(rule)));
	// The same server rendered for a release without TLS, or for another secret, is caught too.
	if (!databaseTlsFindings(good, { tls: false }).some((finding) => finding.includes('although TLS is off'))) missing.push('TLS settings where TLS is off');
	if (!databaseTlsFindings(good, { tls: true, secretName: 'site-db-tls' }).some((finding) => finding.includes('ssl_key_file does not come from the secret site-db-tls'))) missing.push('the key from another secret than tls.secretName');
	if (missing.length) failures.push(`TLS rules that did not fire: ${missing.join(', ')}`);
	return { failures, count: expected.length + 2 };
}

/** ARV-082b: each case of the bad pod TLS fixture and the one rule it must trip, and nothing else. */
const POD_TLS_CASES = [
	['api-plain-port', 'serves plain HTTP on container port 8080'], ['api-wrong-port', 'needs its TLS port 8443, named https'],
	['api-http-probe', 'readinessProbe must be httpGet with scheme HTTPS on 8443'], ['api-no-readiness', 'needs a readinessProbe'],
	['api-fragile-readiness', 'readinessProbe must tolerate a certificate rotation'], ['api-no-certificate', 'must mount its certificate (tls.crt, tls.key) at /app/secrets/pod-tls'],
	['api-writable-certificate', 'must mount its certificate read-only'], ['api-subpath-certificate', 'mounts its certificate with subPath'],
	['api-subpath-copy', 'mounts its certificate with subPath'], ['api-whole-secret', 'must project only tls.crt and tls.key'],
	['api-readable-key', "the key file's mode 0444 lets others read it"], ['api-no-fs-group', 'the pod needs fsGroup'],
	['api-other-secret', 'serves the certificate of ariva-api-cronz-tls, not ariva-api-main-tls'],
	['svc-plain', 'port 80 reaches plain HTTP on 8080'], ['svc-not-443', "the pods' https port is served on 443 only"],
	['svc-unknown-target', 'targets http, which no container port of Deployment/base-api answers'],
	['ing-plain-backend', 'must proxy HTTPS to the pods (backend-protocol HTTPS)'], ['ing-no-verify', 'proxy-ssl-verify on'],
	['ing-no-ca', 'needs proxy-ssl-secret'], ['ing-other-namespace-ca', 'proxy-ssl-secret is kube-system/ariva-cluster-ca, not ariva-site/ariva-cluster-ca'],
	['ing-wrong-name', 'not its backend base-api-service.ariva-site.svc'], ['ing-no-sni', 'proxy-ssl-server-name on'],
	['ing-old-protocols', 'proxy-ssl-protocols must be TLSv1.2 and TLSv1.3 only'], ['ing-shallow-chain', 'proxy-ssl-verify-depth must be at least 2'],
	['ing-port-80', 'port 80 is not 443'], ['ing-no-retry', 'proxy-next-upstream http_502 drops error or timeout'],
	['ing-two-backends', 'one backend per Ingress'], ['ing-missing-service', 'routes to gone-service, which the render does not hold'],
	['ing-snippet-verify-off', 'configuration-snippet writes proxy_ssl_verify itself'], ['ing-snippet-proxy-pass', 'server-snippet writes proxy_pass itself'],
	['web-plain-port', 'serves plain HTTP on container port 3000'], ['web-plain-listener', 'listens without TLS (listen 3000)'],
	['web-wrong-port', 'listens on 8443, not the https port 3443'], ['web-old-protocols', 'ssl_protocols must be TLSv1.2 and TLSv1.3 only'],
	['web-weak-ciphers', 'ssl_ciphers must be'], ['web-foreign-certificate', "ssl_certificate does not come from the pod's certificate"],
	['web-include', 'must not include other files'], ['web-no-listener', 'must mount the TLS listener over /etc/nginx/listen.d'],
	['web-listener-subpath', "mounts its listener with subPath, which leaves the image's plain listen.d/plain.conf in place"],
	['web-subpath-certificate', 'mounts its certificate with subPath']
];

/** ARV-082b: the pod TLS rules against their fixtures: the good one clean, each bad case tripping exactly its rule. */
function podTlsSelfTest() {
	const failures = [];
	const options = { tls: true, namespace: 'ariva-site', secretNames: { 'api-main': 'ariva-api-main-tls', web: 'ariva-web-tls' } };
	const good = parseAsKubernetes(fixture('pod-tls-good.yaml'));
	const clean = [...podTlsFindings(good, options), ...podTlsFindings(parseAsKubernetes(fixture('good.yaml')), { tls: false })];
	if (clean.length) failures.push(`the good pod TLS fixtures produced findings:\n   ${clean.join('\n   ')}`);
	const findings = podTlsFindings(parseAsKubernetes(fixture('pod-tls-bad.yaml')), options);
	const caseOf = (finding) => /^[A-Za-z]+\/([^\s:]+)/.exec(finding)?.[1];
	for (const [name, rule] of POD_TLS_CASES) {
		const own = findings.filter((finding) => caseOf(finding) === name);
		if (own.length !== 1 || !own[0].includes(rule)) failures.push(`pod TLS case ${name} must trip "${rule}" alone, tripped ${own.length ? own.join(' | ') : 'nothing'}`);
	}
	const stray = findings.filter((finding) => !POD_TLS_CASES.some(([name]) => caseOf(finding) === name));
	if (stray.length) failures.push(`pod TLS findings outside the cases (the base objects must be clean):\n   ${stray.join('\n   ')}`);
	// The good render read as plain, in another namespace or against another CA is caught too.
	const missing = [];
	if (!podTlsFindings(good, { tls: false }).some((finding) => finding.includes('although pod TLS is off'))) missing.push('pod TLS objects where it is off');
	if (!podTlsFindings(good, { ...options, namespace: 'default' }).some((finding) => finding.includes('not its backend api-main-service.default.svc'))) missing.push('proxy-ssl-name of another namespace');
	if (!podTlsFindings(good, { ...options, caSecretName: 'site-cluster-ca' }).some((finding) => finding.includes('not ariva-site/site-cluster-ca'))) missing.push('a CA other than podTls.caSecretName');
	if (missing.length) failures.push(`pod TLS rules that did not fire: ${missing.join(', ')}`);
	return { failures, count: POD_TLS_CASES.length + 3 };
}

/**
 * ARV-082b: the web image's half of the listener swap: default.conf takes its listener from listen.d (the directory the chart
 * mounts its TLS listener over) and declares none itself, the image ships listen.d/plain.conf with "listen 3000;" only, and
 * the Dockerfile copies it there.
 */
function webImageListenFindings() {
	const web = path.resolve(here, '..', '..', '..', 'Frontplane', 'Ariva.Web');
	const directives = (file) => fs.readFileSync(path.join(web, file), 'utf8').split('\n').map((line) => line.replace(/#.*$/, '').trim()).filter(Boolean);
	const findings = [];
	const conf = directives('static/nginx/default.conf');
	const include = `include ${POD_TLS.webListen}/*.conf;`;
	if (conf.some((line) => /^listen\b/.test(line))) findings.push('static/nginx/default.conf declares a listener itself; it comes from listen.d');
	if (conf.filter((line) => line === include).length !== 1 || !(conf.indexOf('server {') >= 0 && conf.indexOf(include) > conf.indexOf('server {'))) {
		findings.push(`static/nginx/default.conf must include ${POD_TLS.webListen}/*.conf once, inside its server block`);
	}
	const plain = directives('static/nginx/listen.d/plain.conf');
	if (plain.join(' ') !== 'listen 3000;') findings.push(`static/nginx/listen.d/plain.conf must hold "listen 3000;" only, not ${plain.join(' ')}`);
	const dockerfile = fs.readFileSync(path.join(web, 'Dockerfile'), 'utf8');
	if (!/^COPY --from=builder \/app\/build\/nginx\/listen\.d\/plain\.conf \/etc\/nginx\/listen\.d\/plain\.conf\s*$/m.test(dockerfile)) {
		findings.push(`the web Dockerfile must copy build/nginx/listen.d/plain.conf to ${POD_TLS.webListen}/plain.conf`);
	}
	findings.push(...webRootConfigFindings(dockerfile));
	return findings;
}

/**
 * ARV-092: the build output holds nginx/ (the server configuration the Dockerfile copies to /etc/nginx), so the image must
 * remove it from the web root after copying the build there; otherwise GET /nginx/default.conf serves the configuration.
 */
function webRootConfigFindings(dockerfile) {
	// The last copy of the build counts: a second copy after the removal would bring nginx/ back.
	const copies = [...dockerfile.matchAll(/^COPY --from=builder \/app\/build \/usr\/share\/nginx\/html\/web\s*$/gm)];
	const copy = copies.length ? copies[copies.length - 1].index : -1;
	const remove = dockerfile.search(/^RUN rm -r \/usr\/share\/nginx\/html\/web\/nginx(\s|\\|$)/m);
	if (copy < 0) return ['the web Dockerfile must copy the build to /usr/share/nginx/html/web'];
	if (remove < copy) return ['the web Dockerfile must remove /usr/share/nginx/html/web/nginx after copying the build there (it would serve the nginx configuration)'];
	return [];
}

/** ARV-092: the web root rule fires on a Dockerfile that keeps the configuration in the web root or removes it too early. */
function webRootConfigSelfTest() {
	const copy = 'COPY --from=builder /app/build /usr/share/nginx/html/web\n';
	const remove = 'RUN rm -r /usr/share/nginx/html/web/nginx \\\n    && adduser -S nonroot\n';
	const failures = [];
	if (webRootConfigFindings(copy + remove).length) failures.push('web root rule fires on a Dockerfile that removes nginx/ after the copy');
	if (!webRootConfigFindings(copy).length) failures.push('web root rule did not fire when nginx/ is never removed');
	if (!webRootConfigFindings(remove + copy).length) failures.push('web root rule did not fire when nginx/ is removed before the copy');
	if (!webRootConfigFindings(copy + remove + copy).length) failures.push('web root rule did not fire when the build is copied again after the removal');
	if (!webRootConfigFindings(copy + remove.replace('/nginx ', '/nginx-old ')).length) failures.push('web root rule did not fire when another folder is removed');
	return { failures, count: 5 };
}

/** ARV-084: each case of the bad ingress hardening fixture (the Ingress's name) and the one rule it must trip. */
const INGRESS_HARDENING_CASES = [
	['hsts-missing', 'sends no Strict-Transport-Security (more_set_headers'], ['hsts-add-header', 'sets Strict-Transport-Security with add_header'],
	['hsts-twice', 'sets Strict-Transport-Security more than once'], ['hsts-short', 'max-age 15724800 is under a year'],
	['hsts-no-subdomains', 'without includeSubDomains'], ['hsts-preload', 'with preload'],
	['hsts-plain', 'sends Strict-Transport-Security over plain HTTP'], ['hsts-after-421', 'answers 421 for other hosts before it sets Strict-Transport-Security'],
	['hsts-never-https', 'sends no Strict-Transport-Security over HTTPS'], ['nosniff-missing', 'sends no X-Content-Type-Options: nosniff'],
	['nosniff-add-header', 'sends no X-Content-Type-Options: nosniff with more_set_headers'], ['csp-server-missing', "server-snippet adds no Content-Security-Policy frame-ancestors 'none'"],
	['csp-server-no-always', 'server-snippet adds Content-Security-Policy without always'], ['csp-server-no-frame', "server-snippet adds a Content-Security-Policy without frame-ancestors 'none'"],
	['csp-server-replace', "replaces the pods' Content-Security-Policy"], ['csp-location-missing', "configuration-snippet adds no Content-Security-Policy frame-ancestors 'none'"],
	['csp-location-no-always', 'configuration-snippet adds Content-Security-Policy without always'], ['plain-mode-missing', 'neither refuse nor redirect'],
	['refuse-ssl-redirect', 'refuses plain HTTP but ssl-redirect is true'], ['refuse-force-ssl-redirect', 'refuses plain HTTP but force-ssl-redirect is true'],
	['refuse-no-answer', 'refuses plain HTTP without answering it'], ['refuse-redirects', 'answers plain HTTP with 301, not an error'],
	['redirect-not-forced', 'redirects plain HTTP but ssl-redirect and force-ssl-redirect are not both "true"'], ['redirect-refuses', 'redirects plain HTTP but its configuration-snippet refuses it'],
	['prd-machine-redirect', 'k8s-prd redirects plain HTTP on a machine host'], ['cookie-not-secure', 'the affinity cookie is not Secure'],
	['cookie-lax', 'the affinity cookie is not SameSite=Strict'], ['cookie-path', "the affinity cookie's path is /api, not /"],
	['prefer-server-ciphers', 'ssl-prefer-server-ciphers is true, not "false"'], ['www-redirect', 'from-to-www-redirect generates a www server'],
	['custom-headers', 'custom-headers can replace the chart\'s headers'], ['clear-headers', 'clears response headers (more_clear_headers)'],
	['header-elsewhere', "auth-snippet names one of the chart's response headers"], ['location-hsts', 'configuration-snippet sets Strict-Transport-Security or X-Content-Type-Options in the location'],
	['host-modes-disagree', 'Ingresses that disagree on plain HTTP (refuse and redirect)'], ['host-snippets-differ', 'Ingresses with different server-snippets']
];

/** ARV-084: the ingress hardening rules against their fixtures (k8s-prd): the good one clean, each bad case tripping exactly its rule. */
function ingressHardeningSelfTest() {
	const failures = [];
	const good = ingressHardeningFindings(parseAsKubernetes(fixture('ingress-hardening-good.yaml')), { environment: 'k8s-prd' });
	if (good.length) failures.push(`the good ingress hardening fixture produced findings:\n   ${good.join('\n   ')}`);
	const findings = ingressHardeningFindings(parseAsKubernetes(fixture('ingress-hardening-bad.yaml')), { environment: 'k8s-prd' });
	const caseOf = (finding) => /^Ingress\/([^\s:]+)/.exec(finding)?.[1];
	for (const [name, rule] of INGRESS_HARDENING_CASES) {
		const own = findings.filter((finding) => caseOf(finding) === name);
		if (own.length !== 1 || !own[0].includes(rule)) failures.push(`ingress hardening case ${name} must trip "${rule}" alone, tripped ${own.length ? own.join(' | ') : 'nothing'}`);
	}
	const stray = findings.filter((finding) => !INGRESS_HARDENING_CASES.some(([name]) => caseOf(finding) === name));
	if (stray.length) failures.push(`ingress hardening findings outside the cases (the base objects must be clean):\n   ${stray.join('\n   ')}`);
	// The machine host rule is production's: the same redirect is allowed in dev.
	const dev = ingressHardeningFindings(parseAsKubernetes(fixture('ingress-hardening-bad.yaml')), { environment: 'k8s-dev' });
	if (dev.some((finding) => finding.startsWith('Ingress/prd-machine-redirect'))) failures.push('the machine host redirect is refused outside k8s-prd');
	return { failures, count: INGRESS_HARDENING_CASES.length + 1 };
}

/** ARV-084: the controller values the site applies, and one change per rule of controllerValuesFindings, each tripping it alone. */
const CONTROLLER_VALUES = path.resolve(here, '..', 'Helm', 'ingress-nginx-values.yaml');
const CONTROLLER_CASES = [
	['snippets not allowed', (v) => { v.controller.allowSnippetAnnotations = false; }, 'allowSnippetAnnotations must be true'],
	['allow-snippet-annotations twice', (v) => { v.controller.config['allow-snippet-annotations'] = 'true'; }, 'must not set allow-snippet-annotations'],
	['the High risk level', (v) => { v.controller.config['annotations-risk-level'] = 'High'; }, 'annotations-risk-level is High, not Critical'],
	['TLS 1.1', (v) => { v.controller.config['ssl-protocols'] = 'TLSv1.1 TLSv1.2 TLSv1.3'; }, 'ssl-protocols is TLSv1.1 TLSv1.2 TLSv1.3'],
	['TLS 1.2 only', (v) => { v.controller.config['ssl-protocols'] = 'TLSv1.2'; }, 'ssl-protocols is TLSv1.2, not'],
	['a CBC suite', (v) => { v.controller.config['ssl-ciphers'] += ':ECDHE-RSA-AES128-SHA256'; }, 'ssl-ciphers is not the Mozilla intermediate'],
	["ingress-nginx's default suites", (v) => { v.controller.config['ssl-ciphers'] = `${INGRESS_HARDENING.mozillaIntermediateTls12.join(':')}:DHE-RSA-AES256-GCM-SHA384:DHE-RSA-AES128-GCM-SHA256`; }, 'ssl-ciphers is not the Mozilla intermediate'],
	['session tickets', (v) => { v.controller.config['ssl-session-tickets'] = 'true'; }, 'ssl-session-tickets is true'],
	['HSTS off', (v) => { v.controller.config.hsts = 'false'; }, 'hsts is false, not "true"'],
	['six months of HSTS', (v) => { v.controller.config['hsts-max-age'] = '15724800'; }, 'hsts-max-age is 15724800, under a year'],
	['HSTS without subdomains', (v) => { v.controller.config['hsts-include-subdomains'] = 'false'; }, 'hsts-include-subdomains is false'],
	['HSTS preload', (v) => { v.controller.config['hsts-preload'] = 'true'; }, 'hsts-preload is true'],
	['forwarded headers trusted', (v) => { v.controller.config['use-forwarded-headers'] = 'true'; }, 'use-forwarded-headers is true'],
	['a value that is not a string', (v) => { v.controller.config['enable-modsecurity'] = false; }, 'enable-modsecurity must be a string'],
	// ARV-091: ModSecurity off for the whole controller, and an access log format that writes no query string or credential.
	['ModSecurity on for every Ingress', (v) => { v.controller.config['enable-modsecurity'] = 'true'; }, 'enable-modsecurity is true, not "false"'],
	['ModSecurity left to the default', (v) => { delete v.controller.config['enable-modsecurity']; }, 'enable-modsecurity is unset, not "false"'],
	["the controller's default access log format", (v) => { delete v.controller.config['log-format-upstream']; }, 'log-format-upstream is unset: the controller\'s default writes $request'],
	['the request line with its query string', (v) => { v.controller.config['log-format-upstream'] = INGRESS_LOG_FORMAT.controllerDefault; }, 'log-format-upstream writes $request:'],
	['the request line in braces', (v) => { v.controller.config['log-format-upstream'] = v.controller.config['log-format-upstream'].replace(INGRESS_LOG_FORMAT.requestLine, '${request}'); }, 'log-format-upstream writes $request:'],
	['the request URI', (v) => { v.controller.config['log-format-upstream'] = v.controller.config['log-format-upstream'].replace('$uri', '$request_uri'); }, 'log-format-upstream writes $request_uri:'],
	['the query string as $args', (v) => { v.controller.config['log-format-upstream'] += ' "$args"'; }, 'log-format-upstream writes $args:'],
	['the query string as $query_string', (v) => { v.controller.config['log-format-upstream'] += ' $QUERY_STRING'; }, 'log-format-upstream writes $query_string:'],
	['one query argument', (v) => { v.controller.config['log-format-upstream'] += ' $arg_access_token'; }, 'log-format-upstream writes $arg_access_token:'],
	['the request body', (v) => { v.controller.config['log-format-upstream'] += ' $request_body'; }, 'log-format-upstream writes $request_body:'],
	['the Authorization header', (v) => { v.controller.config['log-format-upstream'] += ' "$http_authorization"'; }, 'log-format-upstream writes $http_authorization:'],
	['the cookies', (v) => { v.controller.config['log-format-upstream'] += ' "$http_cookie" $cookie_ariva_rt'; }, 'log-format-upstream writes $http_cookie, $cookie_ariva_rt:'],
	// ARV-091, review round 2: no error log below notice, nginx's log escaping kept, the controller's OpenTelemetry off, and no
	// log or ModSecurity directive in the controller's own snippets (quoted names included).
	['the error log at debug', (v) => { v.controller.config['error-log-level'] = 'debug'; }, 'error-log-level is debug, not one of notice, warn, error, crit, alert, emerg'],
	['the error log at info', (v) => { v.controller.config['error-log-level'] = 'info'; }, 'error-log-level is info, not one of'],
	["nginx's log escaping off", (v) => { v.controller.config['log-format-escape-none'] = 'true'; }, 'log-format-escape-none is true:'],
	["nginx's log escaping off as a ParseBool value", (v) => { v.controller.config['log-format-escape-none'] = 'T'; }, 'log-format-escape-none is T:'],
	['OpenTelemetry for every Ingress', (v) => { v.controller.config['enable-opentelemetry'] = 'true'; }, 'enable-opentelemetry is true:'],
	['OpenTelemetry on as 1', (v) => { v.controller.config['enable-opentelemetry'] = '1'; }, 'enable-opentelemetry is 1:'],
	["an http-snippet with nginx's combined access log", (v) => { v.controller.config['http-snippet'] = 'client_body_buffer_size 16k;\naccess_log /dev/stdout combined;'; }, 'http-snippet writes access_log:'],
	['an http-snippet with a log format of its own', (v) => { v.controller.config['http-snippet'] = "log_format site '$request';"; }, 'http-snippet writes log_format:'],
	['a main-snippet with a debug error log', (v) => { v.controller.config['main-snippet'] = 'error_log /dev/stderr debug;'; }, 'main-snippet writes error_log:'],
	['a server-snippet with a quoted access_log', (v) => { v.controller.config['server-snippet'] = '"access_log" /dev/stdout;'; }, 'server-snippet writes access_log:'],
	["a location-snippet loading the controller's modsecurity.conf", (v) => { v.controller.config['location-snippet'] = "'modsecurity_rules_file' /etc/nginx/modsecurity/modsecurity.conf;"; }, 'location-snippet writes modsecurity_rules_file:'],
	['a location-snippet turning ModSecurity on', (v) => { v.controller.config['location-snippet'] = 'more_set_headers "X-Site: 1"; modsecurity on;'; }, 'location-snippet writes modsecurity:']
];

/** ARV-091: controller values the rules accept (each alone): the error levels from notice up, the switches off, harmless snippets. */
const CONTROLLER_ACCEPTED = [
	(v) => { v.controller.config['error-log-level'] = 'warn'; }, (v) => { v.controller.config['error-log-level'] = 'notice'; }, (v) => { v.controller.config['error-log-level'] = 'crit'; },
	(v) => { v.controller.config['log-format-escape-none'] = 'false'; }, (v) => { v.controller.config['log-format-escape-json'] = 'true'; },
	(v) => { v.controller.config['enable-opentelemetry'] = 'false'; },
	(v) => { v.controller.config['http-snippet'] = 'client_body_buffer_size 16k;'; }, (v) => { v.controller.config['server-snippet'] = 'more_set_headers "X-Site: 1";'; }
];

function controllerValuesSelfTest() {
	const failures = [];
	const text = fs.readFileSync(CONTROLLER_VALUES, 'utf8');
	const shipped = controllerValuesFindings(parseYaml(text));
	if (shipped.length) failures.push(`Helm/ingress-nginx-values.yaml:\n   ${shipped.join('\n   ')}`);
	// ARV-091: the shipped format is ingress-nginx 1.15's default with the request line written without its query string, and nothing else changed.
	const expectedFormat = INGRESS_LOG_FORMAT.controllerDefault.replace('"$request"', `"${INGRESS_LOG_FORMAT.requestLine}"`);
	if (parseYaml(text).controller?.config?.['log-format-upstream'] !== expectedFormat) failures.push(`Helm/ingress-nginx-values.yaml: log-format-upstream is not ingress-nginx 1.15's default with "${INGRESS_LOG_FORMAT.requestLine}" for "$request" (${expectedFormat})`);
	for (const [label, change, rule] of CONTROLLER_CASES) {
		const values = parseYaml(text);
		change(values);
		const found = controllerValuesFindings(values);
		if (found.length !== 1 || !found[0].includes(rule)) failures.push(`controller values with ${label} must trip "${rule}" alone, tripped ${found.length ? found.join(' | ') : 'nothing'}`);
	}
	CONTROLLER_ACCEPTED.forEach((change, index) => {
		const values = parseYaml(text);
		change(values);
		const found = controllerValuesFindings(values);
		if (found.length) failures.push(`accepted controller values ${index} produced findings: ${found.join(' | ')}`);
	});
	return { failures, count: CONTROLLER_CASES.length, accepted: CONTROLLER_ACCEPTED.length };
}

/** Each expected substring matches its own finding, and nothing else was found. */
function exactly(findings, expected) {
	const left = [...findings];
	for (const rule of expected) {
		const index = left.findIndex((finding) => finding.includes(rule));
		if (index === -1) return false;
		left.splice(index, 1);
	}
	return left.length === 0;
}

/** ARV-085: a NetworkPolicy of the docs by name, for the cases below. */
const policy = (docs, name) => docs.find((doc) => doc?.kind === 'NetworkPolicy' && doc.metadata?.name === name);
const appsOf = (docs, name) => policy(docs, name).spec.podSelector.matchExpressions[0].values;
const REDIS = '{"podSelector":{"matchLabels":{"app.kubernetes.io/name":"redis"}}}';
const DATABASE = '{"podSelector":{"matchLabels":{"app":"timescaledb"}}}';

/** ARV-085: each change to the good network policy fixture (a production render) and the findings it must give, no more. */
const NETWORK_POLICY_CASES = [
	['the default deny removed', (docs) => docs.splice(docs.indexOf(policy(docs, 'ariva-default-deny')), 1), ['no default deny selects them']],
	['a pod left out of the default deny', (docs) => appsOf(docs, 'ariva-default-deny').splice(appsOf(docs, 'ariva-default-deny').indexOf('kafka-topics'), 1), ['Job/kafka-topics: no default deny selects them']],
	['the default deny without Egress', (docs) => { policy(docs, 'ariva-default-deny').spec.policyTypes = ['Ingress']; }, ['no default deny selects them']],
	['a controller rule without from', (docs) => { delete policy(docs, 'web-from-ingress-controller').spec.ingress[0].from; },
		['ingress[0] has no from, so it takes connections from everyone', 'Deployment/web-deployment: the policies allow what the firewall matrix does not: in {"everyone":true} TCP/3443',
			"Deployment/web-deployment: the policies lack the firewall matrix's in"]],
	['an egress rule without ports', (docs) => { delete policy(docs, 'ariva-redis-egress').spec.egress[0].ports; },
		['egress[0] has no ports, so it opens every port', `allow what the firewall matrix does not: out ${REDIS} every port`, `lack the firewall matrix's out ${REDIS} TCP/6379`]],
	['an outbound endpoint open to every address', (docs) => { policy(docs, 'api-integration-outbound-egress').spec.egress[0].to[0].ipBlock.cidr = '0.0.0.0/0'; },
		['ipBlock cidr: 0.0.0.0/0 is wider than /8', 'allow what the firewall matrix does not: out {"ipBlock":{"cidr":"0.0.0.0/0"}} TCP/443', 'lack the firewall matrix\'s out {"ipBlock":{"cidr":"10.20.30.0/24"}} TCP/443']],
	['an ipBlock with host bits', (docs) => { policy(docs, 'api-integration-outbound-egress').spec.egress[0].to[0].ipBlock.cidr = '10.20.30.1/24'; },
		['10.20.30.1/24 is not a network address', 'allow what the firewall matrix does not: out {"ipBlock":{"cidr":"10.20.30.1/24"}}', 'lack the firewall matrix\'s out {"ipBlock":{"cidr":"10.20.30.0/24"}}']],
	['an except that is not a CIDR', (docs) => { policy(docs, 'api-integration-outbound-egress').spec.egress[1].to[1].ipBlock.except = ['10.21.9.0/33']; },
		['ipBlock except: 10.21.9.0/33 is not a CIDR', 'allow what the firewall matrix does not: out {"ipBlock":{"cidr":"10.21.0.0/16","except":["10.21.9.0/33"]}}', "lack the firewall matrix's out"]],
	['Kafka in every namespace', (docs) => { policy(docs, 'ariva-kafka-egress').spec.egress[0].to.push({ namespaceSelector: {} }); },
		['selects every pod of every namespace', 'allow what the firewall matrix does not: out {"namespaceSelector":{}} TCP/9092']],
	['an extra database port', (docs) => { policy(docs, 'ariva-database-egress').spec.egress[0].ports.push({ protocol: 'TCP', port: 5433 }); },
		[`allow what the firewall matrix does not: out ${DATABASE} TCP/5433`]],
	['the SMTP policy removed', (docs) => docs.splice(docs.indexOf(policy(docs, 'ariva-smtp-egress')), 1),
		['Deployment/api-cronz-deployment, Deployment/api-integration-deployment: the policies lack the firewall matrix\'s out {"ipBlock":{"cidr":"10.40.1.25/32"}} TCP/465 | out {"ipBlock":{"cidr":"10.40.1.25/32"}} TCP/587']],
	// Cronz sends the scheduled reports through the relay itself (ARV-060).
	['the SMTP relay closed to Cronz', (docs) => { const apps = appsOf(docs, 'ariva-smtp-egress'); apps.splice(apps.indexOf('api-cronz'), 1); },
		['Deployment/api-cronz-deployment: the policies lack the firewall matrix\'s out {"ipBlock":{"cidr":"10.40.1.25/32"}} TCP/465 | out {"ipBlock":{"cidr":"10.40.1.25/32"}} TCP/587']],
	['DNS for the web pod', (docs) => appsOf(docs, 'ariva-dns-egress').push('web'), ['Deployment/web-deployment: the policies allow what the firewall matrix does not: out']],
	['the controller to a plain port', (docs) => { policy(docs, 'api-main-from-ingress-controller').spec.ingress[0].ports[0].port = 8080; },
		['Deployment/api-main-deployment: the policies allow what the firewall matrix does not: in', "Deployment/api-main-deployment: the policies lack the firewall matrix's in"]],
	['the controller from another namespace', (docs) => { policy(docs, 'api-cronz-from-ingress-controller').spec.ingress[0].from[0].namespaceSelector.matchLabels = { 'kubernetes.io/metadata.name': 'default' }; },
		['Deployment/api-cronz-deployment: the policies allow what the firewall matrix does not: in', "Deployment/api-cronz-deployment: the policies lack the firewall matrix's in"]],
	['the database policy selecting TimescaleDB too', (docs) => appsOf(docs, 'ariva-database-egress').push('timescaledb'), ['selects Ariva pods together with StatefulSet/timescaledb']],
	['MQTT open to every address', (docs) => { policy(docs, 'api-ingest-from-mqtt-sensors').spec.ingress[0].from = [{ ipBlock: { cidr: '0.0.0.0/0' } }]; },
		['ipBlock cidr: 0.0.0.0/0 is wider than /8', 'allow what the firewall matrix does not: in {"ipBlock":{"cidr":"0.0.0.0/0"}} TCP/8883',
			'lack the firewall matrix\'s in {"ipBlock":{"cidr":"10.30.0.0/16"}} TCP/8883 | in {"ipBlock":{"cidr":"10.31.0.0/16"}} TCP/8883']],
	['a job reaching Redis', (docs) => appsOf(docs, 'ariva-redis-egress').push('kafka-topics'), [`Job/kafka-topics: the policies allow what the firewall matrix does not: out ${REDIS} TCP/6379`]],
	// ARV-092c: the signature mirror is api-main's alone (its freshclam sidecar), and api-main needs it with source mirror.
	['the signature mirror open to api-ingest', (docs) => appsOf(docs, 'ariva-clamav-mirror-egress').push('api-ingest'),
		['Deployment/api-ingest-deployment: the policies allow what the firewall matrix does not: out {"ipBlock":{"cidr":"10.70.0.0/24"}} TCP/443']],
	['the signature mirror closed to api-main', (docs) => docs.splice(docs.indexOf(policy(docs, 'ariva-clamav-mirror-egress')), 1),
		['Deployment/api-main-deployment: the policies lack the firewall matrix\'s out {"ipBlock":{"cidr":"10.70.0.0/24"}} TCP/443']],
	['api-stream reachable from the controller', (docs) => {
		const copy = structuredClone(policy(docs, 'api-main-from-ingress-controller'));
		copy.metadata.name = 'api-stream-from-ingress-controller';
		copy.spec.podSelector.matchLabels.app = 'api-stream';
		docs.push(copy);
	}, ['Deployment/api-stream-deployment: the policies allow what the firewall matrix does not: in']]
];

/** ARV-085: the network policy rules against their fixture: the good render clean, each change giving exactly its findings. */
function networkPolicySelfTest() {
	const failures = [];
	const read = () => parseAsKubernetes(fixture('network-policy-good.yaml'));
	const values = parseYaml(fixture('network-policy-expected.yaml'));
	const options = { enabled: true, environment: 'k8s-prd', values };
	const clean = networkPolicyFindings(read(), options);
	if (clean.length) failures.push(`the good network policy fixture produced findings:\n   ${clean.join('\n   ')}`);
	for (const [label, change, expected] of NETWORK_POLICY_CASES) {
		const docs = read();
		change(docs);
		const found = networkPolicyFindings(docs, options);
		if (!exactly(found, expected)) failures.push(`network policy case ${label} must give exactly ${expected.join(' | ')}; gave ${found.length ? found.join(' | ') : 'nothing'}`);
	}
	// Off, every one of the chart's policies is one too many; the prefix rule is production's.
	const off = networkPolicyFindings(read(), { enabled: false });
	const chartPolicies = read().filter((doc) => doc?.kind === 'NetworkPolicy' && doc.metadata?.name !== 'timescaledb').length;
	if (off.length !== chartPolicies || off.some((finding) => !finding.includes('although networkPolicies is off'))) failures.push(`with networkPolicies off the ${chartPolicies} chart policies must each be found, found ${off.length}`);
	const dev = read();
	policy(dev, 'api-integration-outbound-egress').spec.egress[0].to[0].ipBlock.cidr = '0.0.0.0/0';
	if (networkPolicyFindings(dev, { ...options, environment: 'k8s-dev' }).some((finding) => finding.includes('is wider than'))) failures.push('an ipBlock of 0.0.0.0/0 is refused outside k8s-prd (the rule is production\'s)');
	return { failures, count: NETWORK_POLICY_CASES.length + 2 };
}

/** ARV-085: each case of the bad ingress privacy fixture (the Ingress's name) and the one rule it must trip in k8s-prd. */
const INGRESS_PRIVACY_CASES = [
	['health-routed', 'routes /health/readiness'], ['healthz-routed', 'routes /healthz'],
	['health-not-refused', 'does not answer /health, /health/, /health/startup'], ['health-case-sensitive', 'does not answer /HEALTH/READINESS, /Health/Startup with 404'],
	['health-raw-request', 'does not answer /health, /health/'], ['health-before-hsts', 'refuses the probes before it sets Strict-Transport-Security'],
	['health-too-wide', 'refuses /healthy, /healthcheck, which are not health probes'], ['health-redirected', 'does not answer /health, /health/'],
	['health-in-location', 'does not answer /health, /health/'], ['whitelist-alias', 'sets whitelist-source-range, the alias'],
	['allowlist-not-cidr', '192.0.2.0/33 is not a CIDR'], ['allowlist-host-bits', '10.10.0.1/24 is not a network address'],
	['allowlist-everyone', '0.0.0.0/0 is wider than /8'], ['allowlist-ipv6-everyone', '::/0 is wider than /16'],
	['cronz-open', 'the Cronz dashboard has no allowlist-source-range'],
	['rewrite-target-site', 'sets rewrite-target; the probes\' 404 is judged on the request\'s own path'], ['use-regex-site', 'sets use-regex; the probes\' 404 is judged'],
	['snippet-rewrite', 'configuration-snippet writes a rewrite directive']
	];
const PRODUCTION_ONLY_PRIVACY = ['allowlist-everyone', 'allowlist-ipv6-everyone', 'cronz-open'];

/** ARV-085: the private probe and source range rules against their fixtures: the good one clean, each bad case tripping exactly its rule. */
function ingressPrivacySelfTest() {
	const failures = [];
	const good = ingressPrivacyFindings(parseAsKubernetes(fixture('ingress-privacy-good.yaml')), { environment: 'k8s-prd' });
	if (good.length) failures.push(`the good ingress privacy fixture produced findings:\n   ${good.join('\n   ')}`);
	const caseOf = (finding) => /^Ingress\/([^\s:]+)/.exec(finding)?.[1];
	for (const environment of ['k8s-prd', 'k8s-dev']) {
		const findings = ingressPrivacyFindings(parseAsKubernetes(fixture('ingress-privacy-bad.yaml')), { environment });
		for (const [name, rule] of INGRESS_PRIVACY_CASES) {
			const own = findings.filter((finding) => caseOf(finding) === name);
			const expected = environment === 'k8s-prd' || !PRODUCTION_ONLY_PRIVACY.includes(name) ? 1 : 0;
			if (own.length !== expected || (expected && !own[0].includes(rule))) failures.push(`ingress privacy case ${name} in ${environment} must trip ${expected ? `"${rule}" alone` : 'nothing'}, tripped ${own.length ? own.join(' | ') : 'nothing'}`);
		}
		const stray = findings.filter((finding) => !INGRESS_PRIVACY_CASES.some(([name]) => caseOf(finding) === name));
		if (stray.length) failures.push(`ingress privacy findings outside the cases:\n   ${stray.join('\n   ')}`);
	}
	return { failures, count: INGRESS_PRIVACY_CASES.length };
}

/**
 * The chart's server-snippet for a host (ariva-platform.ingressServerSnippet), as the renders below must carry it: the headers
 * and the 421 (ARV-084), the probes' 404 (ARV-085), and the live hub's refusal (ARV-091): on a host without the hub's Ingress
 * /hubs in any case (HUB_REFUSAL), on the hub's host every spelling but the lowercase one (HUB_VARIANT_REFUSAL).
 */
const HUB_REFUSAL = 'if ($uri ~* "^/hubs(/|$)") {\n  return 404;\n}\n';
const HUB_VARIANT_REFUSAL = 'if ($uri ~ "^/(?!hubs(/|$))[Hh][Uu][Bb][Ss](/|$)") {\n  return 404;\n}\n';
const chartServerSnippet = (host, hub = false) => 'set $ariva_hsts "";\nif ($scheme = https) {\n  set $ariva_hsts "max-age=31536000; includeSubDomains";\n}\n' +
	'more_set_headers "Strict-Transport-Security: $ariva_hsts";\nmore_set_headers "X-Content-Type-Options: nosniff";\nadd_header Content-Security-Policy "frame-ancestors \'none\'" always;\n' +
	`if ($host != "${host}") {\n  return 421;\n}\n` + 'if ($uri ~* "^/healthz?(/|$)") {\n  return 404;\n}\n' + (hub ? HUB_VARIANT_REFUSAL : HUB_REFUSAL);

/**
 * ARV-091: each case of the hub refusal (an Ingress on a host of its own: the hub's host when hub is true, its path /hubs, else
 * a host without the hub, its path /), the change to the chart's server-snippet, and the one rule it must trip.
 */
const HUB_REFUSAL_CASES = [
	['main-open', false, (text) => text.replace(HUB_REFUSAL, ''), `does not answer ${HUB_PATHS.all.join(', ')} with 404 at the server level (if ($uri ~* "^/hubs(/|$)")`],
	['main-case-sensitive', false, (text) => text.replace('$uri ~* "^/hubs', '$uri ~ "^/hubs'), `does not answer ${HUB_PATHS.variants.join(', ')} with 404 at the server level (if`],
	['main-too-wide', false, (text) => text.replace('"^/hubs(/|$)"', '"^/hubs"'), 'refuses /hubsx, /hubs-old, /HUBSX, which are not the live hub'],
	['main-before-hsts', false, (text) => HUB_REFUSAL + text.replace(HUB_REFUSAL, ''), 'refuses /hubs before it sets Strict-Transport-Security'],
	['main-redirected', false, (text) => text.replace(HUB_REFUSAL, HUB_REFUSAL.replace('return 404', 'return 301 /')), `does not answer ${HUB_PATHS.all.join(', ')} with 404`],
	['main-variants-only', false, (text) => text.replace(HUB_REFUSAL, HUB_VARIANT_REFUSAL), `does not answer ${HUB_PATHS.served.join(', ')} with 404 at the server level (if`],
	['web-variants-open', true, (text) => text.replace(HUB_VARIANT_REFUSAL, ''), `does not answer ${HUB_PATHS.variants.join(', ')} with 404 at the server level: the host's live hub Ingress takes /hubs in lowercase only`],
	['web-hub-refused', true, (text) => text.replace(HUB_VARIANT_REFUSAL, HUB_REFUSAL), `refuses ${HUB_PATHS.served.join(', ')}, which the host's live hub serves`],
	['web-variants-before-hsts', true, (text) => HUB_VARIANT_REFUSAL + text.replace(HUB_VARIANT_REFUSAL, ''), 'refuses /hubs before it sets Strict-Transport-Security']
];

/** ARV-091: an Ingress of the hub refusal cases. */
const hubIngress = (name, host, paths, serverSnippet) => ({ apiVersion: 'networking.k8s.io/v1', kind: 'Ingress', metadata: { name, annotations: { 'nginx.ingress.kubernetes.io/server-snippet': serverSnippet } },
	spec: { rules: [{ host, http: { paths: paths.map((value) => ({ path: value, pathType: 'Prefix', backend: { service: { name: 'api-main-service', port: { number: 80 } } } })) } }] } });

/** ARV-091: the hub refusal rules: the chart's text clean on a host with and without the hub, each case tripping exactly its rule. */
function hubRefusalSelfTest() {
	const failures = [];
	const good = [hubIngress('api-main', 'api-main.example', ['/'], chartServerSnippet('api-main.example')),
		hubIngress('web', 'web.example', ['/'], chartServerSnippet('web.example', true)), hubIngress('web-hubs', 'web.example', ['/hubs'], chartServerSnippet('web.example', true))];
	const clean = hubRefusalFindings(good);
	if (clean.length) failures.push(`the chart's server-snippets produced hub refusal findings:\n   ${clean.join('\n   ')}`);
	const docs = HUB_REFUSAL_CASES.map(([name, hub, change]) => hubIngress(name, `${name}.example`, hub ? ['/hubs'] : ['/'], change(chartServerSnippet(`${name}.example`, hub))));
	const findings = hubRefusalFindings(docs);
	for (const [name, hub, change, rule] of HUB_REFUSAL_CASES) {
		const own = findings.filter((finding) => finding.startsWith(`Ingress/${name}:`));
		if (change(chartServerSnippet(`${name}.example`, hub)) === chartServerSnippet(`${name}.example`, hub)) failures.push(`hub refusal case ${name} changes nothing`);
		if (own.length !== 1 || !own[0].includes(rule)) failures.push(`hub refusal case ${name} must trip "${rule}" alone, tripped ${own.length ? own.join(' | ') : 'nothing'}`);
	}
	return { failures, count: HUB_REFUSAL_CASES.length };
}

/** ARV-085: API host Deployments (and a simulator and a job) carrying the given trusted proxy networks, for the cases below. */
function proxyDocs(networks) {
	const env = networks.map((value, index) => ({ name: `Security__ForwardedHeaders__KnownNetworks__${index}`, value }));
	const deployment = (name, image, variables) => ({ kind: 'Deployment', metadata: { name }, spec: { template: { spec: { containers: [{ name: 'app', image, env: structuredClone(variables) }] } } } });
	return [deployment('api-main-deployment', 'r.example/ariva/api-main:1', env), deployment('api-ingest-deployment', 'r.example/ariva/api-ingest:1', env),
		deployment('simulation-deployment', 'r.example/ariva/simulation:1', []),
		{ kind: 'Job', metadata: { name: 'database-migration' }, spec: { template: { spec: { containers: [{ name: 'database-migration', image: 'r.example/ariva/api-main:1', env: [] }] } } } }];
}
const envOf = (docs, name) => docs.find((doc) => doc.metadata.name === name).spec.template.spec.containers[0].env;
const NETWORKS = ['10.244.0.0/16', 'fd00:10::/48'];
/** ARV-085: trusted proxy networks the hosts take in k8s-prd, at the edges of what they refuse (a /14, IPv6 /64, /78, /96, /110, ::ffff:0:0/112). */
const ACCEPTED_IPV6 = ['10.244.0.0/14', 'fd00::/64', 'fd00::/78', 'fd00::/96', 'fd00::/110', '::ffff:0:0/112'];

/** ARV-085: each change to the trusted proxies of the API hosts and the findings it must give, no more: [label, environment, networks, change, expected]. */
const FORWARDED_HEADERS_CASES = [
	['a host without them', 'k8s-prd', NETWORKS, (docs) => { envOf(docs, 'api-ingest-deployment').length = 0; }, ['api-ingest-deployment container app: Security__ForwardedHeaders__KnownNetworks is unset']],
	['their order changed', 'k8s-prd', NETWORKS, (docs) => { const env = envOf(docs, 'api-main-deployment'); [env[0].value, env[1].value] = [env[1].value, env[0].value]; }, ['api-main-deployment container app: Security__ForwardedHeaders__KnownNetworks is 0=fd00:10::/48']],
	['one network more', 'k8s-prd', NETWORKS, (docs) => envOf(docs, 'api-main-deployment').push({ name: 'Security__ForwardedHeaders__KnownNetworks__2', value: '10.0.0.0/8' }), ['not ingressController.podNetworks']],
	['from a secret', 'k8s-prd', NETWORKS, (docs) => { envOf(docs, 'api-main-deployment')[0] = { name: 'Security__ForwardedHeaders__KnownNetworks__0', valueFrom: { secretKeyRef: { name: 's', key: 'k' } } }; }, ['0=(not a literal)']],
	['a known proxy', 'k8s-prd', NETWORKS, (docs) => envOf(docs, 'api-main-deployment').push({ name: 'Security__ForwardedHeaders__KnownProxies__0', value: '0.0.0.0' }), ["the chart passes the ingress controller's networks only"]],
	['the simulator trusting them', 'k8s-prd', NETWORKS, (docs) => envOf(docs, 'simulation-deployment').push({ name: 'Security__ForwardedHeaders__KnownNetworks__0', value: NETWORKS[0] }), ['only the API hosts get the trusted proxy networks']],
	['a job trusting them', 'k8s-prd', NETWORKS, (docs) => envOf(docs, 'database-migration').push({ name: 'Security__ForwardedHeaders__ForwardLimit', value: '2' }), ['Job/database-migration container database-migration: sets Security__ForwardedHeaders__ForwardLimit']],
	['production without them', 'k8s-prd', [], () => {}, ["ingressController.podNetworks is empty: k8s-prd needs the ingress controller's pod network"]],
	['a /13 in production', 'k8s-prd', ['10.0.0.0/13'], () => {}, ['10.0.0.0/13 is wider than /14']],
	['an IPv6 /47 in production', 'k8s-prd', ['fd00::/47'], () => {}, ['fd00::/47 is wider than /48']],
	['every address outside production', 'k8s-dev', ['0.0.0.0/0'], () => {}, ['0.0.0.0/0 is wider than /1']],
	['every IPv6 address outside production', 'k8s-dev', ['::/0'], () => {}, ['::/0 is wider than /1']],
	['host bits', 'k8s-dev', ['10.244.0.1/16'], () => {}, ['10.244.0.1/16 is not a network address']],
	// The hosts' rules (ForwardedHeadersExtensions): no address bit after the prefix, and no IPv6 network that trusts IPv4 clients
	// (IPv4-mapped) wider than a /14, in production.
	['an address bit after the prefix in production', 'k8s-prd', ['10.244.1.0/16'], () => {}, ['10.244.1.0/16 is not a network address']],
	['an IPv6 /65 in production', 'k8s-prd', ['fd00:10:244::/65'], () => {}, ['fd00:10:244::/65 trusts IPv4 clients wider than an IPv4 /14']],
	["Cilium's default IPv6 pool in production", 'k8s-prd', ['10.244.0.0/16', 'fd00::/104'], () => {}, ['fd00::/104 trusts IPv4 clients wider than an IPv4 /14']],
	['an IPv6 network ending in an IPv4 /8 in production', 'k8s-prd', ['fd00::a00:0/104'], () => {}, ['fd00::a00:0/104 trusts IPv4 clients wider than an IPv4 /14']],
	['the IPv4-mapped block in production', 'k8s-prd', ['::ffff:0:0/96'], () => {}, ['::ffff:0:0/96 trusts IPv4 clients wider than an IPv4 /14']],
	['a network holding the IPv4-mapped block in production', 'k8s-prd', ['::/64'], () => {}, ['::/64 trusts IPv4 clients wider than an IPv4 /14']]
	];

/** ARV-085: the trusted proxy rules: the good hosts clean (in production, and a /13 allowed outside it), each change giving exactly its findings. */
function forwardedHeadersSelfTest() {
	const failures = [];
	const clean = [...forwardedHeadersFindings(proxyDocs(NETWORKS), { environment: 'k8s-prd', podNetworks: NETWORKS }),
		...forwardedHeadersFindings(proxyDocs(['10.0.0.0/13']), { environment: 'k8s-dev', podNetworks: ['10.0.0.0/13'] }),
		...forwardedHeadersFindings(proxyDocs([]), { environment: 'k8s-dev', podNetworks: [] }),
		// The IPv6 prefixes the hosts take in production, at the edges of the refused ranges; and the rule is production's.
		...forwardedHeadersFindings(proxyDocs(ACCEPTED_IPV6), { environment: 'k8s-prd', podNetworks: ACCEPTED_IPV6 }),
		...forwardedHeadersFindings(proxyDocs(['fd00::/104']), { environment: 'k8s-dev', podNetworks: ['fd00::/104'] })];
	if (clean.length) failures.push(`the good trusted proxies produced findings:\n   ${clean.join('\n   ')}`);
	for (const [label, environment, networks, change, expected] of FORWARDED_HEADERS_CASES) {
		const docs = proxyDocs(networks);
		change(docs);
		const found = forwardedHeadersFindings(docs, { environment, podNetworks: networks });
		if (!exactly(found, expected)) failures.push(`trusted proxy case ${label} must give exactly ${expected.join(' | ')}; gave ${found.length ? found.join(' | ') : 'nothing'}`);
	}
	return { failures, count: FORWARDED_HEADERS_CASES.length };
}

/** ARV-091a: the log pseudonym key's Secret of the shipped production values, and a stand-in key (base64 of 32 bytes, not a real one). */
const LOG_SECRET = { name: 'ariva-log-pseudonym-key', key: 'pseudonym-key' };
const STAND_IN_KEY = Buffer.alloc(32, 7).toString('base64');

/**
 * ARV-091a: the five API host Deployments with the log pseudonym key (from secret, or none) and the Loki switch, and the
 * simulator, the web pod, the migration job and a host ConfigMap without them, for the cases below.
 */
function logDocs({ secret = LOG_SECRET, loki = true, endpoint = '', lokiSecret, headersSecret } = {}) {
	const headers = headersSecret ? [{ name: 'OTEL_EXPORTER_OTLP_HEADERS', valueFrom: { secretKeyRef: { name: headersSecret.name, key: headersSecret.key, optional: false } } }] : [];
	const env = [{ name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: endpoint },
		...(secret ? [{ name: 'Logging__PseudonymKey', valueFrom: { secretKeyRef: { name: secret.name, key: secret.key, optional: false } } }] : []),
		...(loki ? [{ name: 'Loki__Enabled', value: 'true' }] : []),
		...(lokiSecret ? [{ name: 'Loki__Username', valueFrom: { secretKeyRef: { name: lokiSecret.name, key: lokiSecret.usernameKey, optional: false } } },
			{ name: 'Loki__Password', valueFrom: { secretKeyRef: { name: lokiSecret.name, key: lokiSecret.passwordKey, optional: false } } }] : []), ...headers];
	const workload = (kind, name, image, variables) => ({ kind, metadata: { name }, spec: { template: { spec: { containers: [{ name: 'app', image, env: structuredClone(variables) }], volumes: [] } } } });
	return [...API_HOSTS.map((host) => workload('Deployment', `${host}-deployment`, `r.example/ariva/${host}:1`, env)),
		workload('Deployment', 'simulation-deployment', 'r.example/ariva/simulation:1', [{ name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: endpoint }, ...headers]),
		workload('Deployment', 'web-deployment', 'r.example/ariva/web:1', []), workload('Job', 'database-migration', 'r.example/ariva/api-main:1', []),
		{ kind: 'ConfigMap', metadata: { name: 'api-main-configmap' }, data: { DOTNET_ENVIRONMENT: 'k8s-prd' } }];
}
const workloadOf = (docs, name) => docs.find((doc) => doc.metadata.name === name);
const containerOf = (docs, name) => workloadOf(docs, name).spec.template.spec.containers[0];
const keyOf = (docs, name) => containerOf(docs, name).env.find((variable) => variable.name === 'Logging__PseudonymKey');
const LOKI_PRD = { environment: 'k8s-prd', collector: 'loki', pseudonymKeySecret: LOG_SECRET };
const OTLP_PRD = { environment: 'k8s-prd', collector: 'otlp', otelEndpoint: 'https://otel.example:4317', pseudonymKeySecret: LOG_SECRET };
const LOG_REFS = `refers to the log pseudonym key's Secret ${LOG_SECRET.name}`;
/** ARV-091a: a Secret of Loki's sender credentials, and production to Loki with it. */
const LOKI_SECRET = { name: 'ariva-loki-credentials', usernameKey: 'username', passwordKey: 'password' };
const LOKI_AUTH = { ...LOKI_PRD, lokiCredentialsSecret: LOKI_SECRET };
const variableOf = (docs, workload, name) => containerOf(docs, workload).env.find((variable) => variable.name === name);
/** ARV-091a: a Secret of the OTLP collector's sender headers, and production to the OTLP collector with it. */
const HEADERS_SECRET = { name: 'ariva-otlp-headers', key: 'headers' };
const OTLP_HEADERS = { ...OTLP_PRD, otelHeadersSecret: HEADERS_SECRET };
const HEADERS_DOCS = { loki: false, endpoint: OTLP_PRD.otelEndpoint, headersSecret: HEADERS_SECRET };
const HEADERS_REFS = "refers to the Secret of the OTLP collector's headers ariva-otlp-headers";

/** ARV-091a: each change to the log shipping of a render and the findings it must give, no more: [label, options, docs, change, expected]. */
const LOG_SHIPPING_CASES = [
	['a host without the key', LOKI_PRD, {}, (docs) => { const env = containerOf(docs, 'api-stream-deployment').env; env.splice(env.indexOf(keyOf(docs, 'api-stream-deployment')), 1); },
		['Deployment/api-stream-deployment container app: must get Logging__PseudonymKey once, from the Secret ariva-log-pseudonym-key (key pseudonym-key), not 0 times']],
	['the key as a literal', LOKI_PRD, {}, (docs) => { Object.assign(keyOf(docs, 'api-main-deployment'), { value: STAND_IN_KEY, valueFrom: undefined }); },
		['Deployment/api-main-deployment container app: Logging__PseudonymKey must come from the Secret ariva-log-pseudonym-key (secretKeyRef), never a literal value']],
	['the key twice, once as a literal', LOKI_PRD, {}, (docs) => containerOf(docs, 'api-ingest-deployment').env.push({ name: 'Logging__PseudonymKey', value: STAND_IN_KEY }),
		['Deployment/api-ingest-deployment container app: must get Logging__PseudonymKey once, from the Secret ariva-log-pseudonym-key (key pseudonym-key), not 2 times']],
	['the key from another Secret', LOKI_PRD, {}, (docs) => { keyOf(docs, 'api-cronz-deployment').valueFrom.secretKeyRef.name = 'ariva-kafka'; },
		['Deployment/api-cronz-deployment container app: Logging__PseudonymKey comes from ariva-kafka (key pseudonym-key), not ariva-log-pseudonym-key (key pseudonym-key)']],
	['another key of the Secret', LOKI_PRD, {}, (docs) => { keyOf(docs, 'api-integration-deployment').valueFrom.secretKeyRef.key = 'password'; },
		['Deployment/api-integration-deployment container app: Logging__PseudonymKey comes from ariva-log-pseudonym-key (key password)']],
	['the key optional', LOKI_PRD, {}, (docs) => { keyOf(docs, 'api-main-deployment').valueFrom.secretKeyRef.optional = true; }, ['Deployment/api-main-deployment container app: Logging__PseudonymKey must not be optional']],
	['the key in lower case on the simulator', LOKI_PRD, {}, (docs) => containerOf(docs, 'simulation-deployment').env.push({ name: 'logging__pseudonymkey', valueFrom: { secretKeyRef: { ...LOG_SECRET } } }),
		['Deployment/simulation-deployment container app: gets logging__pseudonymkey; only the five API hosts get the log pseudonym key', `Deployment/simulation-deployment: ${LOG_REFS} (app env logging__pseudonymkey)`]],
	['the key in the migration job', LOKI_PRD, {}, (docs) => containerOf(docs, 'database-migration').env.push({ name: 'Logging__PseudonymKey', valueFrom: { secretKeyRef: { ...LOG_SECRET } } }),
		['Job/database-migration container app: gets Logging__PseudonymKey; only the five API hosts', `Job/database-migration: ${LOG_REFS} (app env Logging__PseudonymKey)`]],
	['the key in a ConfigMap', LOKI_PRD, {}, (docs) => { workloadOf(docs, 'api-main-configmap').data.Logging__PseudonymKey = STAND_IN_KEY; },
		['ConfigMap/api-main-configmap: Logging__PseudonymKey names the log pseudonym key']],
	['the Secret mounted by the web pod', LOKI_PRD, {}, (docs) => workloadOf(docs, 'web-deployment').spec.template.spec.volumes.push({ name: 'log-key', secret: { secretName: LOG_SECRET.name } }),
		[`Deployment/web-deployment: ${LOG_REFS} (volume log-key)`]],
	['the Secret as a job\'s envFrom', LOKI_PRD, {}, (docs) => { containerOf(docs, 'database-migration').envFrom = [{ secretRef: { name: LOG_SECRET.name } }]; }, [`Job/database-migration: ${LOG_REFS} (app envFrom)`]],
	['the Secret under another variable of a host', LOKI_PRD, {}, (docs) => containerOf(docs, 'api-main-deployment').env.push({ name: 'Signing__Key', valueFrom: { secretKeyRef: { ...LOG_SECRET } } }),
		[`Deployment/api-main-deployment: ${LOG_REFS} (app env Signing__Key)`]],
	['the Secret rendered by the chart', LOKI_PRD, {}, (docs) => docs.push({ kind: 'Secret', metadata: { name: LOG_SECRET.name }, data: { 'pseudonym-key': Buffer.from(STAND_IN_KEY).toString('base64') } }),
		['Secret/ariva-log-pseudonym-key: the chart renders the log pseudonym key\'s Secret']],
	['production without the key\'s Secret', { ...LOKI_PRD, pseudonymKeySecret: undefined }, { secret: null }, () => {}, ["k8s-prd renders without the log pseudonym key's Secret"]],
	['production without a collector', { ...LOKI_PRD, collector: '' }, { loki: false }, () => {}, ['k8s-prd renders without a log collector']],
	['a release with logging.required and neither', { environment: 'k8s-dev', required: true }, { secret: null, loki: false }, () => {},
		['logging.required renders without a log collector', "logging.required renders without the log pseudonym key's Secret"]],
	['Loki without its switch on a host', LOKI_PRD, {}, (docs) => { const env = containerOf(docs, 'api-cronz-deployment').env; env.splice(env.findIndex((variable) => variable.name === 'Loki__Enabled'), 1); },
		['Deployment/api-cronz-deployment container app: must set Loki__Enabled "true" with logging.collector loki']],
	['the Loki switch without Loki', OTLP_PRD, { endpoint: OTLP_PRD.otelEndpoint }, () => {}, API_HOSTS.map((host) => `Deployment/${host}-deployment container app: sets Loki__Enabled true without logging.collector loki`)],
	['the Loki switch on the simulator', LOKI_PRD, {}, (docs) => containerOf(docs, 'simulation-deployment').env.push({ name: 'LOKI__ENABLED', value: 'true' }),
		['Deployment/simulation-deployment container app: sets LOKI__ENABLED; only the five API hosts send their logs to Loki']],
	['the OTLP collector over plain HTTP', { ...OTLP_PRD, otelEndpoint: 'http://otel.example:4317' }, { loki: false, endpoint: 'http://otel.example:4317' }, () => {},
		['logging.collector otlp exports to http://otel.example:4317, not an https:// otel.endpoint']],
	['the OTLP collector not reaching a host', OTLP_PRD, { loki: false, endpoint: OTLP_PRD.otelEndpoint }, (docs) => { containerOf(docs, 'api-ingest-deployment').env[0].value = ''; },
		['Deployment/api-ingest-deployment container app: exports to no endpoint, not otel.endpoint https://otel.example:4317']],
	['a key where no Secret is named', { environment: 'k8s-dev' }, { loki: false }, () => {}, API_HOSTS.map((host) => `Deployment/${host}-deployment container app: gets Logging__PseudonymKey although logging.pseudonymKeySecret names no Secret`)],
	['a host missing', LOKI_PRD, {}, (docs) => docs.splice(docs.indexOf(workloadOf(docs, 'api-stream-deployment')), 1), ['the render holds no Deployment/api-stream-deployment']],
	// Loki's sender credentials: from their Secret on the five API hosts, never a literal, never optional, nowhere else.
	['a host without Loki\'s password', LOKI_AUTH, { lokiSecret: LOKI_SECRET }, (docs) => { const env = containerOf(docs, 'api-cronz-deployment').env; env.splice(env.indexOf(variableOf(docs, 'api-cronz-deployment', 'Loki__Password')), 1); },
		['Deployment/api-cronz-deployment container app: must get Loki__Password once, from the Secret ariva-loki-credentials (key password), not 0 times']],
	['Loki\'s password as a literal', LOKI_AUTH, { lokiSecret: LOKI_SECRET }, (docs) => { Object.assign(variableOf(docs, 'api-main-deployment', 'Loki__Password'), { value: 'stand-in', valueFrom: undefined }); },
		['Deployment/api-main-deployment container app: Loki__Password must come from the Secret ariva-loki-credentials (secretKeyRef), never a literal value']],
	['Loki\'s user name from another key', LOKI_AUTH, { lokiSecret: LOKI_SECRET }, (docs) => { variableOf(docs, 'api-ingest-deployment', 'Loki__Username').valueFrom.secretKeyRef.key = 'password'; },
		['Deployment/api-ingest-deployment container app: Loki__Username comes from ariva-loki-credentials (key password), not ariva-loki-credentials (key username)']],
	['Loki\'s credentials optional', LOKI_AUTH, { lokiSecret: LOKI_SECRET }, (docs) => { variableOf(docs, 'api-stream-deployment', 'Loki__Username').valueFrom.secretKeyRef.optional = true; },
		['Deployment/api-stream-deployment container app: Loki__Username must not be optional: the host waits for its Secret rather than send to Loki without them']],
	['Loki\'s password on the simulator', LOKI_AUTH, { lokiSecret: LOKI_SECRET }, (docs) => containerOf(docs, 'simulation-deployment').env.push({ name: 'Loki__Password', valueFrom: { secretKeyRef: { name: LOKI_SECRET.name, key: 'password' } } }),
		["Deployment/simulation-deployment container app: gets Loki__Password; only the five API hosts get Loki's credentials",
			"Deployment/simulation-deployment: refers to the Secret of Loki's credentials ariva-loki-credentials (app env Loki__Password)"]],
	['Loki\'s credentials in a ConfigMap', LOKI_AUTH, { lokiSecret: LOKI_SECRET }, (docs) => { workloadOf(docs, 'api-main-configmap').data.Loki__Password = 'stand-in'; },
		["ConfigMap/api-main-configmap: Loki__Password is one of Loki's credentials"]],
	['Loki\'s credentials where no Secret is named', LOKI_PRD, { lokiSecret: LOKI_SECRET }, () => {},
		API_HOSTS.flatMap((host) => ['Username', 'Password'].map((part) => `Deployment/${host}-deployment container app: gets Loki__${part} although logging.lokiCredentialsSecret names no Secret`))],
	['the Secret of Loki\'s credentials rendered by the chart', LOKI_AUTH, { lokiSecret: LOKI_SECRET }, (docs) => docs.push({ kind: 'Secret', metadata: { name: LOKI_SECRET.name }, stringData: { username: 'u', password: 'p' } }),
		["Secret/ariva-loki-credentials: the chart renders the Secret of Loki's credentials"]],
	// The OTLP collector's sender headers: from their Secret on the five API hosts and the simulator, over https, nowhere else.
	['a host without the collector\'s headers', OTLP_HEADERS, HEADERS_DOCS, (docs) => { const env = containerOf(docs, 'api-integration-deployment').env; env.splice(env.indexOf(variableOf(docs, 'api-integration-deployment', 'OTEL_EXPORTER_OTLP_HEADERS')), 1); },
		['Deployment/api-integration-deployment container app: must get OTEL_EXPORTER_OTLP_HEADERS once, from the Secret ariva-otlp-headers (key headers), not 0 times']],
	['the simulator without the collector\'s headers', OTLP_HEADERS, HEADERS_DOCS, (docs) => { containerOf(docs, 'simulation-deployment').env.splice(1, 1); },
		['Deployment/simulation-deployment container app: must get OTEL_EXPORTER_OTLP_HEADERS once, from the Secret ariva-otlp-headers (key headers), not 0 times']],
	['the collector\'s headers as a literal', OTLP_HEADERS, HEADERS_DOCS, (docs) => { Object.assign(variableOf(docs, 'api-main-deployment', 'OTEL_EXPORTER_OTLP_HEADERS'), { value: 'x-api-key=stand-in', valueFrom: undefined }); },
		['Deployment/api-main-deployment container app: OTEL_EXPORTER_OTLP_HEADERS must come from the Secret ariva-otlp-headers (secretKeyRef), never a literal value']],
	['the collector\'s headers optional', OTLP_HEADERS, HEADERS_DOCS, (docs) => { variableOf(docs, 'simulation-deployment', 'OTEL_EXPORTER_OTLP_HEADERS').valueFrom.secretKeyRef.optional = true; },
		['Deployment/simulation-deployment container app: OTEL_EXPORTER_OTLP_HEADERS must not be optional: the host waits for its Secret rather than export to the collector without them']],
	['the collector\'s headers from another key', OTLP_HEADERS, HEADERS_DOCS, (docs) => { variableOf(docs, 'api-stream-deployment', 'OTEL_EXPORTER_OTLP_HEADERS').valueFrom.secretKeyRef.key = 'token'; },
		['Deployment/api-stream-deployment container app: OTEL_EXPORTER_OTLP_HEADERS comes from ariva-otlp-headers (key token), not ariva-otlp-headers (key headers)']],
	['the collector\'s headers in the migration job', OTLP_HEADERS, HEADERS_DOCS, (docs) => containerOf(docs, 'database-migration').env.push({ name: 'otel_exporter_otlp_headers', valueFrom: { secretKeyRef: { ...HEADERS_SECRET } } }),
		["Job/database-migration container app: gets otel_exporter_otlp_headers; only the five API hosts and the simulator get the OTLP collector's sender headers",
			`Job/database-migration: ${HEADERS_REFS} (app env otel_exporter_otlp_headers); only the five API hosts and the simulator read it, as OTEL_EXPORTER_OTLP_HEADERS`]],
	['the collector\'s headers mounted by the web pod', OTLP_HEADERS, HEADERS_DOCS, (docs) => workloadOf(docs, 'web-deployment').spec.template.spec.volumes.push({ name: 'otlp', projected: { sources: [{ secret: { name: HEADERS_SECRET.name } }] } }),
		[`Deployment/web-deployment: ${HEADERS_REFS} (volume otlp)`]],
	['the collector\'s headers in a ConfigMap', OTLP_HEADERS, HEADERS_DOCS, (docs) => { workloadOf(docs, 'api-main-configmap').data.OTEL_EXPORTER_OTLP_HEADERS = 'x-api-key=stand-in'; },
		["ConfigMap/api-main-configmap: OTEL_EXPORTER_OTLP_HEADERS carries the OTLP collector's sender headers"]],
	['the collector\'s headers where no Secret is named', OTLP_PRD, HEADERS_DOCS, () => {},
		[...API_HOSTS, 'simulation'].map((host) => `Deployment/${host}-deployment container app: gets OTEL_EXPORTER_OTLP_HEADERS although otel.headersSecret names no Secret`)],
	['the collector\'s headers over plain HTTP', { ...OTLP_HEADERS, collector: 'loki', otelEndpoint: 'http://otel.example:4318' }, { ...HEADERS_DOCS, loki: true, endpoint: 'http://otel.example:4318' }, () => {},
		["otel.headersSecret sends the collector's headers to http://otel.example:4318, not an https:// otel.endpoint"]]
];

/**
 * ARV-091a: the log shipping rules: the good renders clean (production to Loki and to the OTLP collector, a site's Secret and
 * key, dev with neither), each change giving exactly its findings; and checkManifests refuses a literal Logging__PseudonymKey, in
 * any case, as the credential it is.
 */
function logShippingSelfTest() {
	const failures = [];
	const site = { name: 'site-log-key', key: 'master' };
	const clean = [...logShippingFindings(logDocs(), LOKI_PRD), ...logShippingFindings(logDocs({ loki: false, endpoint: OTLP_PRD.otelEndpoint }), OTLP_PRD),
		...logShippingFindings(logDocs({ secret: site }), { ...LOKI_PRD, pseudonymKeySecret: site }), ...logShippingFindings(logDocs({ secret: null, loki: false }), { environment: 'k8s-dev' }),
		...logShippingFindings(logDocs({ lokiSecret: LOKI_SECRET }), LOKI_AUTH), ...logShippingFindings(logDocs(HEADERS_DOCS), OTLP_HEADERS),
		// One Secret for the key and Loki's credentials alike is allowed: each variable from its own key.
		...logShippingFindings(logDocs({ lokiSecret: { ...LOKI_SECRET, name: LOG_SECRET.name } }), { ...LOKI_PRD, lokiCredentialsSecret: { ...LOKI_SECRET, name: LOG_SECRET.name } })];
	if (clean.length) failures.push(`the good log shipping renders produced findings:\n   ${clean.join('\n   ')}`);
	for (const [label, options, build, change, expected] of LOG_SHIPPING_CASES) {
		const docs = logDocs(build);
		change(docs);
		const found = logShippingFindings(docs, options);
		if (!exactly(found, expected)) failures.push(`log shipping case ${label} must give exactly ${expected.join(' | ')}; gave ${found.length ? found.join(' | ') : 'nothing'}`);
	}
	for (const name of ['Logging__PseudonymKey', 'logging__pseudonymkey', 'Loki__Password', 'OTEL_EXPORTER_OTLP_HEADERS']) {
		const docs = logDocs();
		containerOf(docs, 'api-main-deployment').env.push({ name, value: STAND_IN_KEY });
		if (!checkManifests(docs, { environment: 'k8s-prd' }).some((finding) => finding.includes(`api-main-deployment container app: ${name} carries a literal value`))) failures.push(`checkManifests does not refuse a literal ${name}`);
	}
	return { failures, count: LOG_SHIPPING_CASES.length + 4 };
}

/** ARV-200: the collectors' CA Secrets of the cases below (a site's names) and the options naming both. */
const OTLP_CA = 'site-otlp-ca';
const LOKI_CA = 'site-loki-ca';
const BOTH_CAS = { otlpCaSecretName: OTLP_CA, lokiCaSecretName: LOKI_CA };

/**
 * ARV-200: the five API host Deployments with the collectors' CAs named (otlp, loki: a Secret name, or empty), the simulator with
 * the OTLP collector's, and the web pod, the two jobs and a host ConfigMap without either, as the chart renders them.
 */
function collectorCaDocs({ otlp = OTLP_CA, loki = LOKI_CA, simulator = true } = {}) {
	const cas = (withLoki) => COLLECTOR_CAS.filter((ca) => (ca.volume === 'otlp-ca' ? otlp : withLoki && loki));
	const nameOf = (ca) => (ca.volume === 'otlp-ca' ? otlp : loki);
	const workload = (kind, name, image, list) => ({ kind, metadata: { name }, spec: { template: { spec: {
		volumes: [{ name: 'tmp', emptyDir: {} }, ...list.map((ca) => ({ name: ca.volume, secret: { secretName: nameOf(ca), optional: false, defaultMode: 0o444, items: [{ key: 'ca.crt', path: 'ca.crt' }] } }))],
		containers: [{ name: 'app', image, volumeMounts: [{ name: 'tmp', mountPath: '/tmp' }, ...list.map((ca) => ({ name: ca.volume, mountPath: ca.path, readOnly: true }))],
			env: [{ name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: 'https://otel.example:4317' }, ...list.map((ca) => ({ name: ca.variable, value: ca.file }))] }] } } } });
	return [...API_HOSTS.map((host) => workload('Deployment', `${host}-deployment`, `r.example/ariva/${host}:1`, cas(true))),
		...(simulator ? [workload('Deployment', 'simulation-deployment', 'r.example/ariva/simulation:1', cas(false))] : []),
		workload('Deployment', 'web-deployment', 'r.example/ariva/web:1', []), workload('Job', 'database-migration', 'r.example/ariva/api-main:1', []),
		workload('Job', 'kafka-topics', 'r.example/ariva/api-main:1', []), { kind: 'ConfigMap', metadata: { name: 'api-main-configmap' }, data: { DOTNET_ENVIRONMENT: 'k8s-dev' } }];
}
const podOf = (docs, name) => workloadOf(docs, name).spec.template.spec;
const caVolumeOf = (docs, name, volume) => podOf(docs, name).volumes.find((entry) => entry.name === volume);
const caMountOf = (docs, name, volume) => containerOf(docs, name).volumeMounts.find((entry) => entry.name === volume);
const caVariableOf = (docs, name, variable) => containerOf(docs, name).env.find((entry) => entry.name === variable);
/** ARV-200: a workload given a collector's CA as the chart gives it to an API host (volume, mount and variable). */
function giveCa(docs, name, volume, secretName) {
	const ca = COLLECTOR_CAS.find((entry) => entry.volume === volume);
	podOf(docs, name).volumes.push({ name: volume, secret: { secretName, optional: false, defaultMode: 0o444, items: [{ key: 'ca.crt', path: 'ca.crt' }] } });
	containerOf(docs, name).volumeMounts.push({ name: volume, mountPath: ca.path, readOnly: true });
	containerOf(docs, name).env.push({ name: ca.variable, value: ca.file });
}

/** ARV-200: each change to the collectors' CAs of a render and the findings it must give, no more: [label, options, docs, change, expected]. */
const COLLECTOR_CA_CASES = [
	['a host without the OTLP collector\'s CA mount', BOTH_CAS, {}, (docs) => { const mounts = containerOf(docs, 'api-stream-deployment').volumeMounts; mounts.splice(mounts.indexOf(caMountOf(docs, 'api-stream-deployment', 'otlp-ca')), 1); },
		["Deployment/api-stream-deployment container app: must mount the OTLP collector's CA once at /app/secrets/otlp-ca (volume otlp-ca), as otel.caSecretName names site-otlp-ca, not 0 times"]],
	['Loki\'s CA mounted writable', BOTH_CAS, {}, (docs) => { caMountOf(docs, 'api-main-deployment', 'loki-ca').readOnly = false; },
		['Deployment/api-main-deployment container app: must mount the volume loki-ca read-only and whole (no subPath) at /app/secrets/loki-ca']],
	['the OTLP collector\'s CA mounted as a subPath', BOTH_CAS, {}, (docs) => { Object.assign(caMountOf(docs, 'api-ingest-deployment', 'otlp-ca'), { mountPath: '/app/secrets/otlp-ca/ca.crt', subPath: 'ca.crt' }); },
		["Deployment/api-ingest-deployment container app: must mount the volume otlp-ca read-only and whole (no subPath) at /app/secrets/otlp-ca"]],
	['the OTLP collector\'s CA mounted twice', BOTH_CAS, {}, (docs) => containerOf(docs, 'api-cronz-deployment').volumeMounts.push({ name: 'otlp-ca', mountPath: '/etc/ssl/otlp', readOnly: true }),
		["Deployment/api-cronz-deployment container app: must mount the OTLP collector's CA once at /app/secrets/otlp-ca (volume otlp-ca), as otel.caSecretName names site-otlp-ca, not 2 times"]],
	['Loki\'s CA volume optional', BOTH_CAS, {}, (docs) => { caVolumeOf(docs, 'api-cronz-deployment', 'loki-ca').secret.optional = true; },
		['Deployment/api-cronz-deployment: the volume loki-ca must not be optional: the pods wait for the Secret, as a host given Loki__RootCertificate refuses to start without the file']],
	['the whole Secret projected', BOTH_CAS, {}, (docs) => { delete caVolumeOf(docs, 'api-integration-deployment', 'otlp-ca').secret.items; },
		['Deployment/api-integration-deployment: the volume otlp-ca must project only ca.crt (items), so a Secret that also holds a private key never brings it']],
	['a private key projected beside ca.crt', BOTH_CAS, {}, (docs) => caVolumeOf(docs, 'api-main-deployment', 'loki-ca').secret.items.push({ key: 'tls.key', path: 'tls.key' }),
		['Deployment/api-main-deployment: the volume loki-ca must project only ca.crt (items), so a Secret that also holds a private key never brings it']],
	['the CA from another Secret', BOTH_CAS, {}, (docs) => { caVolumeOf(docs, 'api-stream-deployment', 'otlp-ca').secret.secretName = 'ariva-database-ca'; },
		['Deployment/api-stream-deployment: the volume otlp-ca comes from ariva-database-ca, not otel.caSecretName site-otlp-ca']],
	['the CA writable by its owner', BOTH_CAS, {}, (docs) => { caVolumeOf(docs, 'api-ingest-deployment', 'loki-ca').secret.defaultMode = 0o644; },
		["Deployment/api-ingest-deployment: the volume loki-ca must let no one write or execute Loki's CA (defaultMode 0444), not 0644"]],
	['the CA with the default mode', BOTH_CAS, {}, (docs) => { delete caVolumeOf(docs, 'simulation-deployment', 'otlp-ca').secret.defaultMode; },
		["Deployment/simulation-deployment: the volume otlp-ca must let no one write or execute the OTLP collector's CA (defaultMode 0444), not the default 0644"]],
	['a host without its OTLP volume', BOTH_CAS, {}, (docs) => { const volumes = podOf(docs, 'api-main-deployment').volumes; volumes.splice(volumes.indexOf(caVolumeOf(docs, 'api-main-deployment', 'otlp-ca')), 1); },
		["Deployment/api-main-deployment: needs one volume otlp-ca, the OTLP collector's CA from the Secret site-otlp-ca, not 0"]],
	['the simulator without the OTLP collector\'s CA variable', BOTH_CAS, {}, (docs) => { const env = containerOf(docs, 'simulation-deployment').env; env.splice(env.indexOf(caVariableOf(docs, 'simulation-deployment', 'Otlp__RootCertificate')), 1); },
		['Deployment/simulation-deployment container app: must set Otlp__RootCertificate once, to /app/secrets/otlp-ca/ca.crt, not 0 times']],
	['the variable twice, once in lower case', BOTH_CAS, {}, (docs) => containerOf(docs, 'api-stream-deployment').env.push({ name: 'loki__rootcertificate', value: '/etc/ssl/certs/ca-certificates.crt' }),
		['Deployment/api-stream-deployment container app: must set Loki__RootCertificate once, to /app/secrets/loki-ca/ca.crt, not 2 times']],
	['the variable pointing at another file', BOTH_CAS, {}, (docs) => { caVariableOf(docs, 'api-main-deployment', 'Loki__RootCertificate').value = '/app/secrets/loki-ca/tls.crt'; },
		['Deployment/api-main-deployment container app: Loki__RootCertificate must be /app/secrets/loki-ca/ca.crt, the file the volume loki-ca mounts, not /app/secrets/loki-ca/tls.crt']],
	['the variable from a Secret', BOTH_CAS, {}, (docs) => { Object.assign(caVariableOf(docs, 'api-integration-deployment', 'Otlp__RootCertificate'), { value: undefined, valueFrom: { secretKeyRef: { name: OTLP_CA, key: 'path' } } }); },
		['Deployment/api-integration-deployment container app: Otlp__RootCertificate must be /app/secrets/otlp-ca/ca.crt, the file the volume otlp-ca mounts, not (not a literal)']],
	['Loki\'s CA on the simulator', BOTH_CAS, {}, (docs) => giveCa(docs, 'simulation-deployment', 'loki-ca', LOKI_CA),
		["Deployment/simulation-deployment: has the volume loki-ca; only the five API hosts read Loki's CA", "Deployment/simulation-deployment container app: mounts loki-ca at /app/secrets/loki-ca; only the five API hosts read Loki's CA",
			"Deployment/simulation-deployment container app: sets Loki__RootCertificate; only the five API hosts read Loki's CA"]],
	['the OTLP collector\'s CA in the migration job', BOTH_CAS, {}, (docs) => giveCa(docs, 'database-migration', 'otlp-ca', OTLP_CA),
		["Job/database-migration: has the volume otlp-ca; only the five API hosts and the simulator read the OTLP collector's CA",
			"Job/database-migration container app: mounts otlp-ca at /app/secrets/otlp-ca; only the five API hosts and the simulator read the OTLP collector's CA",
			"Job/database-migration container app: sets Otlp__RootCertificate; only the five API hosts and the simulator read the OTLP collector's CA"]],
	['Loki\'s CA path in the topics job', BOTH_CAS, {}, (docs) => containerOf(docs, 'kafka-topics').volumeMounts.push({ name: 'site-ca', mountPath: '/app/secrets/loki-ca', readOnly: true }),
		["Job/kafka-topics container app: mounts site-ca at /app/secrets/loki-ca; only the five API hosts read Loki's CA"]],
	['the OTLP collector\'s CA variable in the web pod', BOTH_CAS, {}, (docs) => containerOf(docs, 'web-deployment').env = [{ name: 'OTLP__ROOTCERTIFICATE', value: '/app/secrets/otlp-ca/ca.crt' }],
		["Deployment/web-deployment container app: sets OTLP__ROOTCERTIFICATE; only the five API hosts and the simulator read the OTLP collector's CA"]],
	['a sidecar mounting the CA', BOTH_CAS, {}, (docs) => podOf(docs, 'api-main-deployment').containers.push({ name: 'shipper', image: 'r.example/shipper@sha256:' + '0'.repeat(64), volumeMounts: [{ name: 'loki-ca', mountPath: '/ca', readOnly: true }], env: [] }),
		["Deployment/api-main-deployment container shipper: mounts loki-ca at /ca; only the host's own container reads Loki's CA"]],
	['the variable in a ConfigMap', BOTH_CAS, {}, (docs) => { workloadOf(docs, 'api-main-configmap').data.Otlp__RootCertificate = '/app/secrets/otlp-ca/ca.crt'; },
		["ConfigMap/api-main-configmap: Otlp__RootCertificate points a host at the OTLP collector's CA; the chart sets it only in the env of the workloads that mount it"]],
	['the Secret rendered by the chart', BOTH_CAS, {}, (docs) => docs.push({ kind: 'Secret', metadata: { name: LOKI_CA }, data: { 'ca.crt': Buffer.from('stand-in').toString('base64') } }),
		["Secret/site-loki-ca: the chart renders Loki's CA's Secret; the site creates it from its PKI and the chart only names it"]],
	['the CAs where no Secret is named', {}, {}, () => {},
		[...API_HOSTS.map((host) => `Deployment/${host}-deployment`), 'Deployment/simulation-deployment'].flatMap((id) => COLLECTOR_CAS.filter((ca) => ca.simulator || id !== 'Deployment/simulation-deployment').flatMap((ca) => [
			`${id}: has the volume ${ca.volume} although ${ca.value} names no Secret`, `${id} container app: mounts ${ca.volume} at ${ca.path} although ${ca.value} names no Secret`,
			`${id} container app: sets ${ca.variable} although ${ca.value} names no Secret`]))],
	['Loki\'s CA where only the OTLP collector\'s is named', { otlpCaSecretName: OTLP_CA }, {}, () => {},
		API_HOSTS.flatMap((host) => [`Deployment/${host}-deployment: has the volume loki-ca although logging.lokiCaSecretName names no Secret`,
			`Deployment/${host}-deployment container app: mounts loki-ca at /app/secrets/loki-ca although logging.lokiCaSecretName names no Secret`,
			`Deployment/${host}-deployment container app: sets Loki__RootCertificate although logging.lokiCaSecretName names no Secret`])],
	['a host missing', BOTH_CAS, {}, (docs) => docs.splice(docs.indexOf(workloadOf(docs, 'api-cronz-deployment')), 1), ['the render holds no Deployment/api-cronz-deployment']]
];

/**
 * ARV-200: the collectors' CA rules: the good renders clean (both CAs, each alone, neither, production without the simulator),
 * each change giving exactly its findings.
 */
function collectorCaSelfTest() {
	const failures = [];
	const clean = [...collectorCaFindings(collectorCaDocs(), BOTH_CAS), ...collectorCaFindings(collectorCaDocs({ loki: '' }), { otlpCaSecretName: OTLP_CA }),
		...collectorCaFindings(collectorCaDocs({ otlp: '' }), { lokiCaSecretName: LOKI_CA }), ...collectorCaFindings(collectorCaDocs({ otlp: '', loki: '' }), {}),
		...collectorCaFindings(collectorCaDocs({ simulator: false }), BOTH_CAS),
		// One site CA for both collectors, in one Secret, is allowed: each volume of its own.
		...collectorCaFindings(collectorCaDocs({ otlp: 'site-ca', loki: 'site-ca' }), { otlpCaSecretName: 'site-ca', lokiCaSecretName: 'site-ca' })];
	if (clean.length) failures.push(`the good collector CA renders produced findings:\n   ${clean.join('\n   ')}`);
	for (const [label, options, build, change, expected] of COLLECTOR_CA_CASES) {
		const docs = collectorCaDocs(build);
		change(docs);
		const found = collectorCaFindings(docs, options);
		if (!exactly(found, expected)) failures.push(`collector CA case ${label} must give exactly ${expected.join(' | ')}; gave ${found.length ? found.join(' | ') : 'nothing'}`);
	}
	return { failures, count: COLLECTOR_CA_CASES.length };
}

/** ARV-092c: the scanner fixture (a render with the site's mirror over https and its CA) and the values it was rendered with. */
const scanDocs = () => parseAsKubernetes(fixture('upload-scan-good.yaml'));
const SCAN_OPTIONS = { enabled: true, source: 'mirror', mirrorUrl: 'https://clamav-mirror.site.example', mirrorCaSecretName: 'site-clamav-mirror-ca', claimName: '', checksPerDay: 12,
	image: CHART_SCANNER, limits: REPOSITORY_CLAMD };
const scanPod = (docs) => podOf(docs, UPLOAD_SCAN.deployment);
const scanner = (docs, name) => [...(scanPod(docs).initContainers ?? []), ...scanPod(docs).containers].find((container) => container.name === name);
const scanVolume = (docs, name) => scanPod(docs).volumes.find((volume) => volume.name === name);
const scanConfig = (docs, file, from, to) => {
	const map = docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === UPLOAD_SCAN.configMap);
	map.data[file] = from === null ? `${map.data[file]}${to}\n` : map.data[file].replace(from, to);
};
const MAIN = `Deployment/${UPLOAD_SCAN.deployment}`;
const MAP = `ConfigMap/${UPLOAD_SCAN.configMap}`;

/** ARV-092c: each change to the scanner fixture and the findings it must give, no more. */
const UPLOAD_SCAN_CASES = [
	['a writable root for clamd', (docs) => { scanner(docs, 'clamd').securityContext.readOnlyRootFilesystem = false; }, [`${MAIN} container clamd: readOnlyRootFilesystem must be true`]],
	['clamd from its tag without the digest', (docs) => { scanner(docs, 'clamd').image = `clamav/clamav:${CHART_SCANNER.tag}`; }, [`${MAIN} container clamd: must run clamav/clamav:${CHART_SCANNER.tag}@sha256:`]],
	['the init container from another image', (docs) => { scanner(docs, 'clamav-signatures').image = 'clamav/clamav:1.4.5@sha256:' + 'e'.repeat(64); },
		[`${MAIN} container clamav-signatures: must run clamav/clamav:${CHART_SCANNER.tag}@sha256:`]],
	['the scanner\'s address on another host', (docs) => { containerOf(docs, 'api-ingest-deployment').env.push({ name: 'MalwareScan__Host', value: UPLOAD_SCAN.host }); },
		["Deployment/api-ingest-deployment container api-ingest-container: gets MalwareScan__Host; only api-main's own container is told the scanner's address"]],
	['the scanner\'s address in the migration job', (docs) => { containerOf(docs, 'database-migration').env.push({ name: 'MalwareScan__Port', value: UPLOAD_SCAN.port }); },
		['Job/database-migration container database-migration: gets MalwareScan__Port']],
	['api-main pointed at a scanner off its loopback', (docs) => { docs.find((doc) => doc?.metadata?.name === 'api-main-configmap').data.MalwareScan__Host = 'clamav-service'; },
		[`${MAIN} container api-main-container: must set MalwareScan__Host to 127.0.0.1, clamd on the pod's loopback, not clamav-service`]],
	['api-main without the scanner\'s port', (docs) => { delete docs.find((doc) => doc?.metadata?.name === 'api-main-configmap').data.MalwareScan__Port; },
		[`${MAIN} container api-main-container: must set MalwareScan__Port to 3310`]],
	['clamd as root', (docs) => { scanner(docs, 'clamd').securityContext.runAsUser = 0; }, [`${MAIN} container clamd: must run as the image's clamav user`]],
	['freshclam with a capability', (docs) => { scanner(docs, 'freshclam').securityContext.capabilities.add = ['NET_RAW']; }, [`${MAIN} container freshclam: must drop every capability and add none`]],
	['freshclam that may escalate', (docs) => { scanner(docs, 'freshclam').securityContext.allowPrivilegeEscalation = true; }, [`${MAIN} container freshclam: allowPrivilegeEscalation must be false`]],
	['a readiness probe on clamd', (docs) => { scanner(docs, 'clamd').readinessProbe = { exec: { command: UPLOAD_SCAN.ping } }; }, [`${MAIN} container clamd: has a readiness probe`]],
	['clamd reading the token signing key', (docs) => { scanner(docs, 'clamd').volumeMounts.push({ name: 'token-signing', mountPath: '/app/secrets/token-signing', readOnly: true }); },
		[`${MAIN} container clamd: must mount exactly`]],
	['clamd writing its signatures', (docs) => { scanner(docs, 'clamd').volumeMounts.find((mount) => mount.name === 'clamav-signatures').readOnly = false; }, [`${MAIN} container clamd: must mount exactly`]],
	['the mirror\'s CA in clamd', (docs) => { scanner(docs, 'clamd').volumeMounts.push({ name: 'clamav-mirror-ca', mountPath: UPLOAD_SCAN.mirrorCaPath, readOnly: true }); },
		[`${MAIN} container clamd: must mount exactly`]],
	['clamd given a Secret', (docs) => { scanner(docs, 'clamd').env = [{ name: 'Kafka__SaslPassword', valueFrom: { secretKeyRef: { name: 'ariva-kafka', key: 'password' } } }]; },
		[`${MAIN} container clamd: sets Kafka__SaslPassword; the scanner's containers get no variable from a Secret or a field`]],
	['clamd given the host\'s settings', (docs) => { scanner(docs, 'clamd').envFrom = [{ configMapRef: { name: 'api-main-configmap' } }]; },
		[`${MAIN} container clamd: gets MalwareScan__Host, MalwareScan__Port`, `${MAIN} container clamd: has envFrom`]],
	['freshclam trusting another CA file', (docs) => { scanner(docs, 'freshclam').env.push({ name: 'SSL_CERT_FILE', value: '/tmp/ca.crt' }); }, [`${MAIN} container freshclam: sets SSL_CERT_FILE`]],
	['clamd in a time zone of its own', (docs) => { scanner(docs, 'clamd').env = [{ name: 'TZ', value: 'Asia/Dubai' }]; },
		[`${MAIN} container clamd: sets TZ; clamd reports its signatures' build time in its local time, which Ariva.Api.Main reads as UTC`]],
	['clamd reading the node\'s zone file', (docs) => { scanner(docs, 'clamd').volumeMounts.push({ name: 'clamav-config', mountPath: '/etc/localtime', readOnly: true }); },
		[`${MAIN} container clamd: must mount exactly`]],
	['freshclam without the mirror\'s CA', (docs) => { scanner(docs, 'freshclam').env = []; }, [`${MAIN} container freshclam: must set CURL_CA_BUNDLE to /etc/ariva/mirror-ca/ca.crt`]],
	['a port on clamd', (docs) => { scanner(docs, 'clamd').ports = [{ containerPort: 3310 }]; }, [`${MAIN} container clamd: declares a port`]],
	['clamd started through the image\'s init script', (docs) => { scanner(docs, 'clamd').command = ['/init']; }, [`${MAIN} container clamd: must run clamd --foreground --config-file=/etc/clamav/clamd.conf`]],
	['clamd too small for its signatures', (docs) => { scanner(docs, 'clamd').resources.requests.memory = '512Mi'; }, [`${MAIN} container clamd: its memory request must hold the signatures clamd keeps loaded`]],
	['clamd without limits', (docs) => { delete scanner(docs, 'clamd').resources.limits; }, [`${MAIN} container clamd: needs requests and limits of cpu and memory`]],
	['freshclam limited below a whole download', (docs) => { scanner(docs, 'freshclam').resources.limits.memory = '96Mi'; }, [`${MAIN} container freshclam: its memory limit must hold a whole download`]],
	['a startup probe on clamd', (docs) => { scanner(docs, 'clamd').startupProbe = { exec: { command: UPLOAD_SCAN.ping }, periodSeconds: 5, failureThreshold: 60 }; },
		[`${MAIN} container clamd: has a startup probe; Kubernetes counts a container whose startup probe has not passed as not ready`]],
	['clamd without a liveness probe', (docs) => { delete scanner(docs, 'clamd').livenessProbe; }, [`${MAIN} container clamd: needs a livenessProbe that runs clamdscan`]],
	['a liveness probe before the signatures load', (docs) => { scanner(docs, 'clamd').livenessProbe.initialDelaySeconds = 10; }, [`${MAIN} container clamd: its livenessProbe must wait at least 60 s`]],
	['a liveness probe that kills a reload', (docs) => { scanner(docs, 'clamd').livenessProbe.failureThreshold = 1; }, [`${MAIN} container clamd: its livenessProbe must tolerate more than 60 s without an answer`]],
	['a liveness window of exactly 60 s', (docs) => { scanner(docs, 'clamd').livenessProbe.failureThreshold = 2; }, [`${MAIN} container clamd: its livenessProbe must tolerate more than 60 s without an answer`]],
	['a liveness probe through a shell', (docs) => { scanner(docs, 'clamd').livenessProbe.exec.command = ['sh', '-c', 'clamdscan --ping 1']; }, [`${MAIN} container clamd: needs a livenessProbe that runs clamdscan`]],
	['clamd before api-main', (docs) => { const pod = scanPod(docs); pod.containers.unshift(pod.containers.splice(1, 1)[0]); }, [`${MAIN}: api-main-container must be the pod's first container`]],
	['the service account token in the pod', (docs) => { delete scanPod(docs).automountServiceAccountToken; }, [`${MAIN}: must set automountServiceAccountToken false`]],
	['api-main reading the signatures', (docs) => { containerOf(docs, UPLOAD_SCAN.deployment).volumeMounts.push({ name: 'clamav-signatures', mountPath: '/signatures', readOnly: true }); },
		[`${MAIN} container api-main-container: mounts the scanner's clamav-signatures`]],
	['clamd\'s scratch space without a limit', (docs) => { delete scanVolume(docs, 'clamd-tmp').emptyDir.sizeLimit; }, [`${MAIN}: the volume clamd-tmp must be an emptyDir whose sizeLimit holds MaxQueue 4 times StreamMaxLength plus MaxThreads 2 times MaxScanSize (1220 MiB`]],
	['the mirror\'s CA optional', (docs) => { scanVolume(docs, 'clamav-mirror-ca').secret.optional = true; }, [`${MAIN}: the volume clamav-mirror-ca must be the Secret site-clamav-mirror-ca`]],
	['the mirror\'s CA with a private key', (docs) => { scanVolume(docs, 'clamav-mirror-ca').secret.items.push({ key: 'tls.key', path: 'tls.key' }); }, [`${MAIN}: the volume clamav-mirror-ca must be the Secret site-clamav-mirror-ca`]],
	['the scanner\'s configuration writable', (docs) => { scanVolume(docs, 'clamav-config').configMap.defaultMode = 0o644; }, [`${MAIN}: the volume clamav-config must be the ConfigMap api-main-clamav-configmap, mode 0444`]],
	['clamd in another host\'s pod', (docs) => { podOf(docs, 'api-ingest-deployment').containers.push(structuredClone(scanner(docs, 'clamd'))); },
		[`Deployment/api-ingest-deployment container clamd: runs clamav/clamav:${CHART_SCANNER.tag}@sha256:`]],
	['clamd listening on every address', (docs) => scanConfig(docs, 'clamd.conf', 'TCPAddr 127.0.0.1', 'TCPAddr 0.0.0.0'), [`${MAP}: clamd.conf must set TCPAddr 127.0.0.1 once`]],
	['clamd on a second address too', (docs) => scanConfig(docs, 'clamd.conf', null, 'TCPAddr 10.0.0.9'), [`${MAP}: clamd.conf must set TCPAddr 127.0.0.1 once`]],
	['clamd taking what it cannot scan whole as clean', (docs) => scanConfig(docs, 'clamd.conf', 'AlertExceedsMax yes', 'AlertExceedsMax no'),
		[`${MAP}: clamd.conf must set AlertExceedsMax yes as Platform/Cloud/clamav/clamd.conf does`]],
	['clamd cutting the largest upload short', (docs) => scanConfig(docs, 'clamd.conf', 'StreamMaxLength 105M', 'StreamMaxLength 25M'), [`${MAP}: clamd.conf must set StreamMaxLength 105M`]],
	['clamd holding two signature sets through a reload', (docs) => scanConfig(docs, 'clamd.conf', 'ConcurrentDatabaseReload no', 'ConcurrentDatabaseReload yes'),
		[`${MAP}: clamd.conf must set ConcurrentDatabaseReload no once`]],
	['clamd never checking its signatures', (docs) => scanConfig(docs, 'clamd.conf', 'SelfCheck 600', 'SelfCheck 0'), [`${MAP}: clamd.conf must set SelfCheck once`]],
	['clamd on a socket file', (docs) => scanConfig(docs, 'clamd.conf', null, 'LocalSocket /tmp/clamd.sock'), [`${MAP}: clamd.conf sets LocalSocket`]],
	['freshclam loading every update to test it', (docs) => scanConfig(docs, 'freshclam.conf', 'TestDatabases no', 'TestDatabases yes'), [`${MAP}: freshclam.conf must set TestDatabases no once`]],
	['freshclam from another mirror', (docs) => scanConfig(docs, 'freshclam.conf', 'PrivateMirror https://clamav-mirror.site.example', 'PrivateMirror https://elsewhere.example'),
		[`${MAP}: freshclam.conf must set PrivateMirror https://clamav-mirror.site.example once`]],
	['freshclam also asking the public mirrors', (docs) => scanConfig(docs, 'freshclam.conf', null, 'DatabaseMirror database.clamav.net'), [`${MAP}: freshclam.conf sets DatabaseMirror with source mirror`]],
	['freshclam notifying clamd', (docs) => scanConfig(docs, 'freshclam.conf', null, 'NotifyClamd /etc/clamav/clamd.conf'), [`${MAP}: freshclam.conf sets NotifyClamd`]],
	['freshclam rebuilding databases uncompressed', (docs) => scanConfig(docs, 'freshclam.conf', 'CompressLocalDatabase yes', 'CompressLocalDatabase no'),
		[`${MAP}: freshclam.conf must set CompressLocalDatabase yes once`]],
	['clamd loading unsigned files from its folder', (docs) => scanConfig(docs, 'clamd.conf', 'OfficialDatabaseOnly yes', 'OfficialDatabaseOnly no'),
		[`${MAP}: clamd.conf must set OfficialDatabaseOnly yes once`, `${MAP}: clamd.conf must set OfficialDatabaseOnly yes as Platform/Cloud/clamav/clamd.conf does`]],
	['clamd with its default ten threads', (docs) => scanConfig(docs, 'clamd.conf', 'MaxThreads 2\n', ''), [`${MAP}: clamd.conf must set MaxThreads 1 to 2`]],
	['clamd with a queue shorter than its threads', (docs) => scanConfig(docs, 'clamd.conf', 'MaxQueue 4', 'MaxQueue 1'), [`${MAP}: clamd.conf must set MaxThreads 1 to 2`]],
	['clamd queueing more uploads than its scratch space holds', (docs) => scanConfig(docs, 'clamd.conf', 'MaxQueue 4', 'MaxQueue 8'),
		[`${MAIN}: the volume clamd-tmp must be an emptyDir whose sizeLimit holds MaxQueue 8 times StreamMaxLength plus MaxThreads 2 times MaxScanSize (1640 MiB`]],
	['clamd\'s scratch space at 1Gi', (docs) => { scanVolume(docs, 'clamd-tmp').emptyDir.sizeLimit = '1Gi'; },
		[`${MAIN}: the volume clamd-tmp must be an emptyDir whose sizeLimit holds MaxQueue 4 times StreamMaxLength plus MaxThreads 2 times MaxScanSize (1220 MiB`]],
	['the signature volume too small for an update', (docs) => { scanVolume(docs, 'clamav-signatures').emptyDir.sizeLimit = '512Mi'; },
		[`${MAIN}: the volume clamav-signatures must be an emptyDir whose sizeLimit is at least twice the most an update holds there (420 MiB`]],
	['the HPA on the whole pod\'s memory', (docs) => {
		const hpa = docs.find((doc) => doc?.kind === 'HorizontalPodAutoscaler');
		hpa.spec.metrics[1] = { type: 'Resource', resource: { name: 'memory', target: { type: 'Utilization', averageUtilization: 70 } } };
	}, [`HorizontalPodAutoscaler/${UPLOAD_SCAN.hpa}: must scale on api-main-container's own cpu and memory`]]
];

/** ARV-092c: the scanner rules against their fixture: the render clean, each change giving exactly its findings. */
function uploadScanSelfTest() {
	const failures = [];
	const clean = uploadScanFindings(scanDocs(), SCAN_OPTIONS);
	if (clean.length) failures.push(`the good scanner fixture produced findings:\n   ${clean.join('\n   ')}`);
	for (const [label, change, expected] of UPLOAD_SCAN_CASES) {
		const docs = scanDocs();
		change(docs);
		const found = uploadScanFindings(docs, SCAN_OPTIONS);
		if (!exactly(found, expected)) failures.push(`scanner case ${label} must give exactly ${expected.join(' | ')}; gave ${found.length ? found.join(' | ') : 'nothing'}`);
	}
	// Below Kubernetes 1.27 the HPA keeps the pod's Resource metrics: the fixture's ContainerResource is a finding there, and the
	// pod's metrics are none.
	const below = uploadScanFindings(scanDocs(), { ...SCAN_OPTIONS, containerMetrics: false });
	if (!exactly(below, [`HorizontalPodAutoscaler/${UPLOAD_SCAN.hpa}: below Kubernetes 1.27 must scale on the pod's Resource metrics`])) failures.push(`scanner case ContainerResource below 1.27 gave ${below.join(' | ') || 'nothing'}`);
	const podMetrics = scanDocs();
	podMetrics.find((doc) => doc?.kind === 'HorizontalPodAutoscaler').spec.metrics = [{ type: 'Resource', resource: { name: 'cpu', target: { type: 'Utilization', averageUtilization: 60 } } }];
	const belowClean = uploadScanFindings(podMetrics, { ...SCAN_OPTIONS, containerMetrics: false });
	if (belowClean.length) failures.push(`scanner case the pod's metrics below 1.27 gave ${belowClean.join(' | ')}`);
	return { failures, count: UPLOAD_SCAN_CASES.length + 2 };
}

function selfTest() {
	const good = checkManifests(parse(fs.readFileSync(path.join(here, 'fixtures', 'good.yaml'), 'utf8')), { environment: 'k8s-prd' });
	const bad = checkManifests(parse(fs.readFileSync(path.join(here, 'fixtures', 'bad.yaml'), 'utf8')), { environment: 'k8s-prd' });
	const expected = [
		'runAsNonRoot', 'runAsUser', 'seccompProfile', 'hostNetwork', 'allowPrivilegeEscalation', 'privileged',
		'capabilities.drop', 'readOnlyRootFilesystem', '/tmp', 'latest', 'needs a tls section', 'not covered by tls', 'production must pin',
		'token signing key', 'integration token signing key', 'integration token key ring', 'needs securityContext.fsGroup', 'token public keys', 'disable the access log', 'audit log off', 'only critical errors', 'its own ingress',
		// ARV-091: the ModSecurity audit log off on every Ingress, not only the hub's.
		'Ingress/bad-no-tls: the ingress must turn the ModSecurity audit log off (its modsecurity-snippet ends with SecAuditEngine Off, not unset)',
		'Ingress/bad-audit-relevant: the ingress must turn the ModSecurity audit log off (its modsecurity-snippet ends with SecAuditEngine Off, not Off then RelevantOnly)',
		'Ingress/bad-audit-ctl: the ingress must turn the ModSecurity audit log off (its modsecurity-snippet ends with SecAuditEngine Off, not Off, and no rule sets ctl:auditEngine=On)',
		'Ingress/bad-debug-log: the modsecurity-snippet sets a ModSecurity debug log (SecDebugLog...)',
		'Ingress/bad-hubs-logged: /hubs carries tokens in its URL; the ingress must keep OpenTelemetry off',
		'pinned by digest', 'carries a literal value', 'needs a NetworkPolicy', 'automountServiceAccountToken', 'Kafka__ProvisionTopics',
		'POSTGRES_INITDB_ARGS', 'POSTGRES_HOST_AUTH_METHOD', 'without NOSUPERUSER', 'must set DOTNET_ENVIRONMENT',
		'Deployment/no-environment', 'Job/argument-overrides', 'Deployment/later-config-map-overrides', 'Deployment/wrong-environment', 'Job/bare-argument-overrides',
		'may get the validation reader login', 'comes from its secret (secretKeyRef)', 'must get the validation reader login',
		'must not mark the validation reader secret optional',
		'terminationGracePeriodSeconds of at least 60', 'must set Hosting__GracePeriodSeconds',
		'needs a startupProbe', 'must set Hosting__StartupAllowanceSeconds',
		// ARV-085: never the framework's switch that trusts every proxy.
		'ASPNETCORE_FORWARDEDHEADERS_ENABLED is set',
		"api-ingest-deployment container app: must set Hosting__StartupAllowanceSeconds to its startup probe's allowance at its worst, initialDelaySeconds plus periodSeconds times (failureThreshold - 1) (115)",
		// ARV-082a: the servers' CAs.
		'Job/kafka-topics container kafka-topics: must mount the database CA at /app/secrets/database-ca', 'Job/database-migration container database-migration: must mount the Redis CA at /app/secrets/redis-ca',
		'Deployment/api-main-deployment container app: must mount the database CA', 'must mount the database CA read-only and whole', 'the database CA volume must project only ca.crt',
		'the Redis CA at /app/secrets/redis-ca must come from a secret',
		// ARV-125b: the monitoring ConfigMaps.
		'holds an unresolved token', 'queries the label user_id, which may hold a secret or a person identifier', 'queries the label client_secret',
		'queries the label email', 'not on the allowlist', 'threshold page is not a severity', 'has no resolved number', 'has no runbook link',
		'has no evaluation window', 'is not valid JSON', 'field apiKey looks like a credential', 'is not a SigNoz dashboard of schemaVersion v6',
		'has no panel', 'a monitoring object must be a ConfigMap',
		"reads ariva.health.status without the filter deployment.environment = 'k8s-dev'", "reads ariva.outbox.unsent without the filter",
		'reads k8s.volume.available outside a namespace filter', 'a PromQL query reads no namespace', 'has no environment annotation'
	];
	const missing = expected.filter((rule) => !bad.some((finding) => finding.includes(rule)));
	// ARV-125b: a render without a rule the story names is caught (the good fixture holds one rule of them).
	const absent = missingMonitoringRules(parse(fs.readFileSync(path.join(here, 'fixtures', 'good.yaml'), 'utf8')));
	if (!absent.some((finding) => finding.includes('rule hostDown is missing')) || absent.some((finding) => finding.includes('rule kafkaUnavailable is missing'))) {
		missing.push('missingMonitoringRules names the absent rules and only them');
	}
	const tls = databaseTlsSelfTest();
	const podTls = podTlsSelfTest();
	const webImage = webImageListenFindings();
	const webRoot = webRootConfigSelfTest();
	const hardening = ingressHardeningSelfTest();
	const controller = controllerValuesSelfTest();
	const network = networkPolicySelfTest();
	const privacy = ingressPrivacySelfTest();
	const proxies = forwardedHeadersSelfTest();
	const hubs = hubRefusalSelfTest();
	const logs = logShippingSelfTest();
	const collectorCas = collectorCaSelfTest();
	const scanning = uploadScanSelfTest();
	const others = [...tls.failures, ...podTls.failures, ...webImage, ...webRoot.failures, ...hardening.failures, ...controller.failures, ...network.failures, ...privacy.failures, ...proxies.failures, ...hubs.failures, ...logs.failures,
		...collectorCas.failures, ...scanning.failures];
	if (good.length || missing.length || others.length) {
		console.error('chart-security self-test FAILED');
		if (good.length) console.error('  good fixture produced findings:\n   ' + good.join('\n   '));
		if (missing.length) console.error('  rules that did not fire on the bad fixture: ' + missing.join(', '));
		for (const failure of others) console.error(`  ${failure}`);
		process.exit(1);
	}
	console.log(`chart-security self-test: ${expected.length} rules fire on the bad fixture, ${tls.count} TimescaleDB TLS rules on the TLS fixtures, ${podTls.count} pod TLS rules ` +
		`on the pod TLS fixtures, ${hardening.count} ingress hardening rules and ${privacy.count} private probe and source range rules on theirs (each bad case trips exactly its rule), ` +
		`${hubs.count} live hub refusal cases on the chart's server-snippet (each tripping exactly its rule), ` +
		`${network.count} network policy cases, ${proxies.count} trusted proxy cases, ${logs.count} log shipping cases, ${collectorCas.count} collector CA cases and ${scanning.count} malware scanner cases each giving exactly their findings; the good fixtures are clean; the web image takes ` +
		`its listener from ${POD_TLS.webListen} and keeps no nginx configuration in its web root (${webRoot.count} cases); the shipped controller values (Helm/ingress-nginx-values.yaml) hold every rule, their access log format is ingress-nginx 1.15's ` +
		`without the query string, ${controller.count} changes each trip theirs and ${controller.accepted} accepted changes trip none.`);
}

function helmAvailable() {
	return spawnSync(helm, ['version', '--short'], { encoding: 'utf8', shell: false }).status === 0;
}

function render(valuesFile, sets = [], namespace = '', extraFiles = [], helmArgs = []) {
	const site = valuesFile === 'values-k8s-prd.yaml' ? ['-f', PRD_SITE] : [];
	const args = ['template', 'ariva-platform', chart, '-f', path.join(chart, valuesFile), ...site, ...extraFiles.flatMap((file) => ['-f', file]), '--set', 'imageCredentials.password=', ...helmArgs];
	if (namespace) args.push('--namespace', namespace);
	for (const set of sets) args.push('--set', set);
	return spawnSync(helm, args, { encoding: 'utf8', shell: false, maxBuffer: 32 * 1024 * 1024 });
}

function renderDatabase(sets = []) {
	const args = ['template', 'timescaledb', databaseChart];
	for (const set of sets) args.push('--set', set);
	return spawnSync(helm, args, { encoding: 'utf8', shell: false, maxBuffer: 8 * 1024 * 1024 });
}

/** ARV-125b: the monitoring ConfigMaps of a render read the given deployment.environment (checkManifests checks every query). */
function monitoringEnvironment(docs, environment) {
	const findings = [];
	for (const name of ['ariva-monitoring-alerts', 'ariva-monitoring-dashboards']) {
		const scope = docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === name)?.metadata?.annotations?.['ariva.dalil.tech/environment'];
		if (scope !== environment) findings.push(`ConfigMap/${name}: reads deployment.environment ${scope}, not ${environment}`);
	}
	return findings;
}

/** ARV-111a: the retention periods the values give (values.yaml under the environment's file), by key. */
function retentionValues(valuesFile) {
	const read = (file) => parseAllDocuments(fs.readFileSync(path.join(chart, file), 'utf8'))[0].toJS()?.migration?.retention ?? {};
	return { ...read('values.yaml'), ...read(valuesFile) };
}

/**
 * ARV-085: a release's values as Helm merges them: values.yaml, then each file (the chart's environment file by name, or a path)
 * or object (what a release sets); maps merge, a list or a value replaces, null removes the key. For the network rules, which
 * compare a render with its values.
 */
function mergedValues(files, { prdSite = true } = {}) {
	const merge = (base, over) => {
		if (over === null) return undefined;
		const map = (value) => value && typeof value === 'object' && !Array.isArray(value);
		if (!map(base) || !map(over)) return over;
		const out = { ...base };
		for (const [key, value] of Object.entries(over)) {
			const merged = merge(base[key], value);
			if (merged === undefined) delete out[key];
			else out[key] = merged;
		}
		return out;
	};
	const read = (file) => (typeof file === 'object' ? file : parseYaml(fs.readFileSync(path.isAbsolute(file) ? file : path.join(chart, file), 'utf8')) ?? {});
	// ARV-092c: a render of values-k8s-prd.yaml carries the site's signature source (PRD_SITE), unless the caller gives its own.
	const listed = files.flatMap((file) => (prdSite && file === 'values-k8s-prd.yaml' ? [file, PRD_SITE] : [file]));
	return ['values.yaml', ...listed].map(read).reduce((a, b) => merge(a, b), {});
}

/**
 * ARV-085: the network findings of a render against its values: the NetworkPolicies (on as the values say, or as the release
 * sets them), the private probes and source ranges of every Ingress, and the API hosts' trusted proxy networks. ARV-091: and
 * the live hub's refusal on every host (hubRefusalFindings), refused at the server level like the probes.
 */
function networkFindings(text, values, environment, { enabled } = {}) {
	const docs = parseAsKubernetes(text);
	const network = networkValues(values);
	const on = enabled ?? network.networkPolicies.enabled === true;
	const findings = [];
	if (environment === 'k8s-prd' && !on) findings.push('k8s-prd renders without its NetworkPolicies');
	findings.push(...networkPolicyFindings(docs, { enabled: on, environment, values: network }), ...ingressPrivacyFindings(docs, { environment }),
		...forwardedHeadersFindings(docs, { environment, podNetworks: values.ingressController?.podNetworks ?? [] }), ...hubRefusalFindings(docs));
	return findings;
}

/** ARV-085: how a render reads in the PASS lines: its NetworkPolicies and the trusted proxy networks. */
function describeNetwork(text, values) {
	const policies = parseAsKubernetes(text).filter((doc) => doc?.kind === 'NetworkPolicy' && doc.metadata?.name !== 'timescaledb').length;
	const networks = values.ingressController?.podNetworks ?? [];
	const docs = parseAsKubernetes(text).filter((doc) => doc?.kind === 'Ingress');
	const hubHosts = new Set(docs.filter((doc) => (doc.spec?.rules ?? []).some((rule) => (rule.http?.paths ?? []).some((entry) => HUB_PATHS.route.test(String(entry.path ?? ''))))).flatMap((doc) => doc.spec.rules.map((rule) => rule.host)));
	const otherHosts = new Set(docs.flatMap((doc) => (doc.spec?.rules ?? []).map((rule) => rule.host)).filter((host) => !hubHosts.has(host)));
	return `${policies ? `${policies} NetworkPolicies (default deny and the firewall matrix's flows)` : 'no NetworkPolicies'}, ` +
		`${networks.length ? `trusted proxies ${networks.join(', ')}` : "the appsettings' trusted proxies"}, /health refused on every Ingress, ` +
		`/hubs refused on the ${otherHosts.size} hosts without the live hub and its other spellings on the ${hubHosts.size} with it`;
}

/**
 * ARV-091a: the log shipping values of a release (values.yaml under its files), as logShippingFindings takes them; required when
 * the release sets logging.required (the Helmfiles' prd).
 */
function logShippingOptions(values, environment, required = false) {
	const logging = values.logging ?? {};
	const secret = logging.pseudonymKeySecret ?? {};
	const loki = logging.lokiCredentialsSecret ?? {};
	return { environment, collector: String(logging.collector ?? ''), otelEndpoint: String(values.otel?.endpoint ?? ''),
		pseudonymKeySecret: secret.name ? { name: String(secret.name), key: String(secret.key || 'pseudonym-key') } : undefined,
		lokiCredentialsSecret: loki.name ? { name: String(loki.name), usernameKey: String(loki.usernameKey || 'username'), passwordKey: String(loki.passwordKey || 'password') } : undefined,
		otelHeadersSecret: values.otel?.headersSecret?.name ? { name: String(values.otel.headersSecret.name), key: String(values.otel.headersSecret.key || 'headers') } : undefined,
		required: required || logging.required === true };
}

/**
 * ARV-092c: the malware scan values of a release (values.yaml under its files), as uploadScanFindings takes them; image overrides
 * the values' image (a release that sets another repository, such as the offline bundle's mirror).
 */
function uploadScanOptions(values, { image } = {}) {
	const scan = values.uploadScan ?? {};
	const signatures = scan.signatures ?? {};
	return { enabled: scan.enabled === true, source: String(signatures.source ?? ''), mirrorUrl: String(signatures.mirrorUrl ?? ''),
		mirrorCaSecretName: String(signatures.mirrorCaSecretName ?? ''), claimName: String(signatures.claimName ?? ''), checksPerDay: signatures.checksPerDay ?? 12,
		image: { ...(scan.image ?? {}), ...(image ?? {}) }, limits: REPOSITORY_CLAMD };
}

/** ARV-092c: how a render scans uploads, for the PASS lines. */
function describeUploadScan(values) {
	const { enabled, source, mirrorUrl, mirrorCaSecretName, claimName } = uploadScanOptions(values);
	if (!enabled) return 'no malware scanner';
	const from = { public: 'freshclam from the ClamAV mirrors', mirror: `freshclam from ${mirrorUrl}${mirrorCaSecretName ? ` with the CA of ${mirrorCaSecretName}` : ''}`, mounted: `signatures mounted read-only from ${claimName}` }[source];
	return `clamd beside api-main on ${UPLOAD_SCAN.host}:${UPLOAD_SCAN.port} only, ${from}`;
}

/** ARV-200: the collectors' CA Secrets a release's values name (values.yaml under its files), as collectorCaFindings takes them. */
function collectorCaOptions(values) {
	return { otlpCaSecretName: String(values.otel?.caSecretName ?? ''), lokiCaSecretName: String(values.logging?.lokiCaSecretName ?? '') };
}

/** ARV-200: the workloads that mount each collector's CA in a render, by volume (otlp-ca, loki-ca). */
function collectorCaReach(docs) {
	return Object.fromEntries(COLLECTOR_CAS.map((ca) => [ca.volume, docs.filter((doc) => (doc?.spec?.template?.spec?.volumes ?? []).some((volume) => volume.name === ca.volume))
		.map((doc) => `${doc.kind}/${doc.metadata?.name}`).sort()]));
}

/** ARV-200: which collectors' CAs a render trusts, for the PASS lines. */
function describeCollectorCas(values) {
	const { otlpCaSecretName, lokiCaSecretName } = collectorCaOptions(values);
	const one = (name, what, where) => (name ? `${what} against ${name} in ${where}` : `${what} against the image's public roots`);
	return `${one(otlpCaSecretName, 'the OTLP collector verified', 'the five API hosts and the simulator')}, ${one(lokiCaSecretName, 'Loki verified', 'the five API hosts')}`;
}

/** ARV-091a: how a render ships its logs, for the PASS lines. */
function describeLogShipping(values) {
	const { collector, otelEndpoint, pseudonymKeySecret, lokiCredentialsSecret, otelHeadersSecret } = logShippingOptions(values, '');
	const where = { otlp: `logs to the OTLP collector ${otelEndpoint}`, loki: 'logs to Loki (Loki__Enabled on the five API hosts)' }[collector] ?? 'no log collector declared';
	return `${where}${lokiCredentialsSecret ? ` with its credentials from ${lokiCredentialsSecret.name}` : ''}, ` +
		`${otelHeadersSecret ? `the OTLP collector's headers from ${otelHeadersSecret.name} (key ${otelHeadersSecret.key}), ` : ''}` +
		`${pseudonymKeySecret ? `the log pseudonym key from ${pseudonymKeySecret.name} (key ${pseudonymKeySecret.key}) in the five API hosts only` : 'no log pseudonym key (one per process)'}`;
}

/** ARV-111a: the migration job of a render carries every retention period of its values, as the job reads them. */
function retentionFindings(docs, valuesFile) {
	const job = docs.find((doc) => doc?.kind === 'Job' && doc.metadata?.name === 'database-migration');
	if (!job) return ['the database-migration job is not rendered'];
	const env = Object.fromEntries((job.spec?.template?.spec?.containers?.[0]?.env ?? []).map((variable) => [variable.name, variable.value]));
	const findings = [];
	const expected = retentionValues(valuesFile);
	for (const key of ['rawSensingDays', 'deskZoneReadingDays', 'laneStaffingDays', 'alertEmailDays', 'integrationCallDays', 'reportDeliveryDays', 'advertisingPlayDays']) {
		const name = `Database__Retention__${key[0].toUpperCase()}${key.slice(1)}`;
		if (expected[key] === undefined) findings.push(`${valuesFile}: migration.retention.${key} has no value`);
		else if (env[name] !== String(expected[key])) findings.push(`Job/database-migration: ${name} is ${env[name]}, not migration.retention.${key} (${expected[key]})`);
	}
	return findings;
}

/** ARV-082a: the workloads that run an Ariva .NET host against PostgreSQL and Redis, and only they, mount the servers' CAs. */
const SERVER_CA_WORKLOADS = ['Deployment/api-main-deployment', 'Deployment/api-ingest-deployment', 'Deployment/api-stream-deployment',
	'Deployment/api-cronz-deployment', 'Deployment/api-integration-deployment', 'Job/database-migration', 'Job/kafka-topics'];

/** ARV-082a: the servers' CAs reach exactly SERVER_CA_WORKLOADS (checkManifests checks each mount), from the given secrets. */
function serverCaReach(docs, names = { 'database-ca': 'ariva-database-ca', 'redis-ca': 'ariva-redis-ca' }) {
	const found = serverCaSecrets(docs);
	const findings = [];
	const reached = Object.keys(found).sort();
	if (reached.join(',') !== [...SERVER_CA_WORKLOADS].sort().join(',')) {
		findings.push(`the servers' CAs reach ${reached.join(', ') || 'no workload'}, not exactly the five API hosts and the migration and topics jobs`);
	}
	for (const [id, secrets] of Object.entries(found)) {
		for (const [volume, name] of Object.entries(names)) {
			if (secrets[volume] !== name) findings.push(`${id}: ${volume} comes from the secret ${secrets[volume]}, not ${name}`);
		}
	}
	return findings;
}

/** ARV-082b: the default certificate secret of each service that serves TLS (values.yaml podTls.secretNames). */
const POD_TLS_SECRETS = Object.fromEntries(POD_TLS.services.map((service) => [service, `ariva-${service}-tls`]));

/**
 * ARV-082b: a pod TLS render: podTlsFindings with the given namespace, CA and secrets, the certificates in exactly the five
 * API Deployments and the web Deployment (no job, no simulator) from those secrets, and each of their Services on 443 to https.
 */
function podTlsRender(text, { namespace = 'default', caSecretName = POD_TLS.caSecretName, secretNames = POD_TLS_SECRETS } = {}) {
	const docs = parseAsKubernetes(text);
	const findings = podTlsFindings(docs, { tls: true, namespace, caSecretName, secretNames });
	const reached = podTlsSecrets(docs);
	const expected = Object.fromEntries(POD_TLS.services.map((service) => [`Deployment/${service}-deployment`, secretNames[service]]));
	if (JSON.stringify(Object.keys(reached).sort()) !== JSON.stringify(Object.keys(expected).sort())) {
		findings.push(`the pod certificates reach ${Object.keys(reached).sort().join(', ') || 'no workload'}, not exactly the five API hosts and the web pod`);
	}
	for (const [id, name] of Object.entries(reached)) if (expected[id] && name !== expected[id]) findings.push(`${id}: serves the certificate of ${name}, not ${expected[id]}`);
	for (const service of POD_TLS.services) {
		const object = docs.find((doc) => doc?.kind === 'Service' && doc.metadata?.name === `${service}-service`);
		const ports = object?.spec?.ports ?? [];
		if (ports.length !== 1 || ports[0].port !== 443 || ports[0].name !== 'https' || ports[0].targetPort !== 'https') findings.push(`Service/${service}-service: must serve 443 (https) to the pods' https port and nothing else`);
	}
	return findings;
}

/** ARV-125b: the text of one rule of a render's ariva-monitoring-alerts. */
function monitoringRule(docs, key) {
	return docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === 'ariva-monitoring-alerts')?.data?.[key] ?? '';
}

if (process.argv.includes('--self-test')) {
	selfTest();
	process.exit(0);
}

selfTest();
if (!helmAvailable()) {
	const message = 'chart-security: helm not found (set HELM or install Helm 3)';
	if (process.env.CI) {
		console.error(`${message}; failing because CI is set.`);
		process.exit(1);
	}
	console.warn(`${message}; chart render SKIPPED locally (rules self-tested above).`);
	process.exit(0);
}
console.log(execFileSync(helm, ['version', '--short'], { encoding: 'utf8' }).trim());

/** ARV-084: the plain HTTP mode of each Ingress of a render, by name. */
const modesOf = (docs) => Object.fromEntries(docs.filter((doc) => doc?.kind === 'Ingress').map((doc) => [doc.metadata?.name, doc.metadata?.annotations?.[INGRESS_HARDENING.plainHttpAnnotation]]));

/**
 * ARV-084: the browser Ingresses (the web app, its /api and /hubs, the Cronz dashboard) redirect plain HTTP and every other
 * one refuses it, in every environment file the chart ships.
 */
function plainHttpModes(docs, file) {
	const redirecting = new Set(['web-ingress', 'web-api-ingress', 'web-hubs-ingress', 'api-cronz-ingress']);
	return Object.entries(modesOf(docs)).filter(([name, mode]) => mode !== (redirecting.has(name) ? 'redirect' : 'refuse'))
		.map(([name, mode]) => `${file}: Ingress/${name} ${mode === 'redirect' ? 'redirects' : 'refuses'} plain HTTP; the browser hosts (web, Cronz) redirect and the others refuse`);
}

const describeModes = (docs) => {
	const modes = Object.values(modesOf(docs));
	return `refused on ${modes.filter((mode) => mode === 'refuse').length} and redirected on ${modes.filter((mode) => mode === 'redirect').length} Ingresses`;
};

const environments = [
	{ file: 'values-k8s-dev.yaml', environment: 'k8s-dev', sets: [] },
	{ file: 'values-k8s-demo.yaml', environment: 'k8s-demo', sets: [] },
	{ file: 'values-localk8s.yaml', environment: 'k8s-dev', sets: [] },
	{ file: 'values-k8s-prd.yaml', environment: 'k8s-prd', sets: ['buildNumber=main-20261001.1'] }
];

let failed = false;
for (const env of environments) {
	const result = render(env.file, env.sets);
	if (result.status !== 0) {
		console.error(`FAIL ${env.file}: helm template failed\n${result.stderr}`);
		failed = true;
		continue;
	}
	const docs = parse(result.stdout);
	const findings = checkManifests(docs, { environment: env.environment });
	// ARV-125b: every environment ships the alert rules the story names and the dashboards, reading its own environment.
	findings.push(...missingMonitoringRules(docs), ...monitoringEnvironment(docs, env.environment));
	// ARV-104g1: the validation reader login reaches exactly api-main and the migration job, in every environment.
	const readers = docs.filter((doc) => (doc?.spec?.template?.spec?.containers ?? []).some((container) =>
		(container.env ?? []).some((variable) => variable.name === 'Database__ValidationReader__Password' && variable.valueFrom?.secretKeyRef))).map((doc) => `${doc.kind}/${doc.metadata?.name}`).sort();
	if (readers.join(',') !== 'Deployment/api-main-deployment,Job/database-migration') findings.push(`the validation reader login reaches ${readers.join(', ') || 'no workload'}, not exactly api-main-deployment and the database-migration job`);
	// ARV-111a: the migration job applies this environment's retention periods.
	findings.push(...retentionFindings(docs, env.file));
	// ARV-082a: the servers' CAs reach exactly the five API hosts and the two jobs, from the default secrets.
	findings.push(...serverCaReach(docs));
	// ARV-082b: production serves every pod over TLS (values-k8s-prd.yaml); the other environments stay plain.
	const podTls = env.environment === 'k8s-prd';
	findings.push(...(podTls ? podTlsRender(result.stdout) : podTlsFindings(parseAsKubernetes(result.stdout), { tls: false })));
	// ARV-084: every Ingress's headers, plain HTTP and cookie; and the modes this environment's values give each host.
	findings.push(...ingressHardeningFindings(parseAsKubernetes(result.stdout), { environment: env.environment }), ...plainHttpModes(docs, env.file));
	// ARV-085: the NetworkPolicies (production only, by default), the private probes, the Cronz source range, the trusted proxies.
	const values = mergedValues([env.file]);
	findings.push(...networkFindings(result.stdout, values, env.environment));
	// ARV-091a: the log pseudonym key and the collector, as the values give them (production only, by default).
	findings.push(...logShippingFindings(parseAsKubernetes(result.stdout), logShippingOptions(values, env.environment)));
	// ARV-200: the collectors' CAs as the values name them (none in the shipped files: the image's public roots).
	findings.push(...collectorCaFindings(parseAsKubernetes(result.stdout), collectorCaOptions(values)));
	// ARV-092c: the malware scanner beside api-main, as the values give it (prd: the site's mirror, PRD_SITE).
	findings.push(...uploadScanFindings(parseAsKubernetes(result.stdout), uploadScanOptions(values)));
	const workloads = docs.filter((doc) => doc?.kind === 'Deployment').length;
	const ingresses = docs.filter((doc) => doc?.kind === 'Ingress').length;
	const monitoring = (name) => Object.keys(docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === name)?.data ?? {}).length;
	if (findings.length) {
		failed = true;
		console.error(`FAIL ${env.file}:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS ${env.file}: ${workloads} deployments, ${ingresses} ingresses, validation reader login in api-main and the migration job only, ` +
			'database and Redis CAs in the five API hosts and the two jobs only, ' +
			(podTls ? 'every pod, Service and Ingress over TLS with the cluster CA, ' : 'plain HTTP to the pods, ') +
			`HSTS, nosniff and frame-ancestors on every Ingress's responses, the ModSecurity audit log off on every Ingress, plain HTTP ${describeModes(docs)}, ${describeNetwork(result.stdout, values)}, ` +
			`${describeLogShipping(values)}, ${describeCollectorCas(values)}, ${describeUploadScan(values)}, ` +
			`retention periods ${Object.values(retentionValues(env.file)).join('/')} days in the migration job, ` +
			`${monitoring('ariva-monitoring-alerts')} alert rules and ${monitoring('ariva-monitoring-dashboards')} dashboards`);
	}
}

{
	const result = renderDatabase();
	if (result.status !== 0) {
		console.error(`FAIL timescaledb: helm template failed\n${result.stderr}`);
		failed = true;
	} else {
		const docs = parse(result.stdout);
		const findings = checkManifests(docs, { environment: 'k8s-prd' });
		if (!docs.some((doc) => doc?.kind === 'StatefulSet')) findings.push('timescaledb: no StatefulSet rendered');
		// ARV-082a: TLS is off unless a release turns it on.
		findings.push(...databaseTlsFindings(parseAsKubernetes(result.stdout), { tls: false }));
		if (findings.length) {
			failed = true;
			console.error(`FAIL timescaledb:\n  ${findings.join('\n  ')}`);
		} else {
			console.log(`PASS timescaledb: ${docs.filter((doc) => doc?.kind === 'StatefulSet').length} statefulset, ${docs.filter((doc) => doc?.kind === 'NetworkPolicy').length} network policy, no TLS by default`);
		}
	}
}

// ARV-232: the server's max_connections comes from maxConnections on its command line, with TLS and without.
for (const [label, sets, expected] of [
	['timescaledb max_connections without TLS', [], 'max_connections=200'],
	['timescaledb max_connections with TLS', ['tls.enabled=true'], 'max_connections=200'],
	['timescaledb max_connections raised', ['maxConnections=400'], 'max_connections=400']
]) {
	const result = renderDatabase(sets);
	const args = result.status === 0 ? (parse(result.stdout).find((doc) => doc.kind === 'StatefulSet')?.spec?.template?.spec?.containers?.[0]?.args ?? []) : [];
	const flag = args.indexOf(expected);
	if (result.status !== 0 || args[0] !== 'postgres' || flag < 1 || args[flag - 1] !== '-c') {
		failed = true;
		console.error(`FAIL ${label}: the server's arguments do not set ${expected}\n${result.stderr ?? ''}`);
	} else {
		console.log(`PASS ${label}: ${expected} on the server's command line`);
	}
}

// ARV-232: the two charts' defaults name the same server limit (the platform chart counts against what the database chart sets).
{
	const platformLimit = parseYaml(fs.readFileSync(path.join(chart, 'values.yaml'), 'utf8')).databaseConnections?.serverMaxConnections;
	const databaseLimit = parseYaml(fs.readFileSync(path.join(databaseChart, 'values.yaml'), 'utf8')).maxConnections;
	if (platformLimit !== databaseLimit || !Number.isInteger(platformLimit)) {
		failed = true;
		console.error(`FAIL database connection defaults: the platform chart's databaseConnections.serverMaxConnections (${platformLimit}) is not the timescaledb chart's maxConnections (${databaseLimit})`);
	} else {
		console.log(`PASS database connection defaults: both charts name a server limit of ${platformLimit}`);
	}
}

// ARV-232: a rolling update's extra pod counts for api-main only when it does not recreate (its upload volume shared), so a limit
// that holds dev with api-main recreating refuses the same release with the volume ReadWriteMany.
for (const [label, sets, accepted] of [
	['dev at a limit of 180 with api-main recreating', ['databaseConnections.serverMaxConnections=180'], true],
	['dev at a limit of 180 with api-main rolling on a shared volume', ['databaseConnections.serverMaxConnections=180', 'fileStorage.accessMode=ReadWriteMany'], false]
]) {
	const result = render('values-k8s-dev.yaml', sets);
	const refusedForMain = result.status !== 0 && (result.stderr ?? '').includes('main (1 + 1) x (14 + 4)');
	if (accepted ? result.status !== 0 : !refusedForMain) {
		failed = true;
		console.error(`FAIL database connections, ${label}: ${accepted ? 'refused' : 'not refused for api-main\'s extra pod'}\n${result.stderr ?? ''}`);
	} else {
		console.log(`PASS database connections, ${label}: ${accepted ? 'rendered' : 'refused, api-main\'s extra pod counted'}`);
	}
}

// ARV-232: a fractional replica count (a number, as a values file gives it) is refused too, not counted as its whole part.
{
	const result = render('values-k8s-dev.yaml', [], '', [], ['--set-json', 'apiStreamHpaMax=1.5']);
	if (result.status === 0 || !(result.stderr ?? '').includes('apiStreamHpaMax must be a whole number of replicas')) {
		failed = true;
		console.error(`FAIL release guard: a fractional HPA maximum must be refused but ${result.status === 0 ? 'rendered' : `failed otherwise\n${result.stderr}`}`);
	} else {
		console.log('PASS release guard: a fractional HPA maximum (1.5 as a number) is refused');
	}
}

// ARV-232: every API host's deployment takes its runtime pool from databaseConnections.poolSize, as Database:MaxPoolSize.
{
	const result = render('values-k8s-dev.yaml', ['databaseConnections.poolSize.main=17', 'databaseConnections.poolSize.stream=9']);
	const findings = [];
	if (result.status !== 0) findings.push(`template failed\n${result.stderr}`);
	else {
		const docs = parse(result.stdout);
		const expected = { 'api-main': '17', 'api-ingest': undefined, 'api-stream': '9', 'api-cronz': undefined, 'api-integration': undefined };
		const values = parseAllDocuments(fs.readFileSync(path.join(chart, 'values.yaml'), 'utf8'))[0].toJS().databaseConnections.poolSize;
		for (const [deployment, pool] of Object.entries(expected)) {
			const container = docs.find((doc) => doc.kind === 'Deployment' && doc.metadata?.name === `${deployment}-deployment`)?.spec?.template?.spec?.containers?.find((c) => c.name === `${deployment}-container`);
			const value = container?.env?.find((variable) => variable.name === 'Database__MaxPoolSize')?.value;
			const want = pool ?? String(values[deployment.replace('api-', '')]);
			if (value !== want) findings.push(`${deployment}: Database__MaxPoolSize is ${value}, not ${want}`);
		}
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL database pools reach the hosts:\n  ${findings.join('\n  ')}`);
	} else {
		console.log('PASS database pools reach the hosts: every API deployment sets Database__MaxPoolSize from databaseConnections.poolSize');
	}
}

// ARV-082a: TimescaleDB with TLS: the defaults (secret timescaledb-tls, TLS 1.2 at least), and a site's secret with TLS 1.3.
for (const [label, sets, secretName, floor] of [
	['timescaledb with tls.enabled', ['tls.enabled=true'], 'timescaledb-tls', 'TLSv1.2'],
	['timescaledb with TLS from a site secret and TLS 1.3', ['tls.enabled=true', 'tls.secretName=site-db-tls', 'tls.minProtocolVersion=TLSv1.3'], 'site-db-tls', 'TLSv1.3']
]) {
	const result = renderDatabase(sets);
	const findings = [];
	if (result.status !== 0) findings.push(`template failed\n${result.stderr}`);
	else {
		findings.push(...checkManifests(parse(result.stdout), { environment: 'k8s-prd' }), ...databaseTlsFindings(parseAsKubernetes(result.stdout), { tls: true, secretName }));
		if (!result.stdout.includes(`"ssl_min_protocol_version=${floor}"`)) findings.push(`ssl_min_protocol_version is not ${floor}`);
		// V12.1.2: the server container passes the TLS 1.2 suites, ECDHE with AEAD only, as one -c argument.
		const args = parseAsKubernetes(result.stdout).find((doc) => doc?.kind === 'StatefulSet')?.spec?.template?.spec?.containers?.[0]?.args ?? [];
		if (!args.some((word, index) => word === `ssl_ciphers=${DATABASE_TLS12_CIPHERS}` && args[index - 1] === '-c')) findings.push(`the server is not given -c ssl_ciphers=${DATABASE_TLS12_CIPHERS}`);
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL ${label}:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS ${label}: ssl on, certificate and key from ${secretName} as PostgreSQL accepts them, ${floor} at least, TLS 1.2 suites ${DATABASE_TLS12_CIPHERS}, pg_hba.conf takes TCP over TLS only`);
	}
}

for (const [label, sets] of [
	['a database image without a digest', ['image.digest=']],
	['the latest database image', ['image.tag=latest']],
	['database passwords without a secret', ['existingSecret=']],
	['the superuser as the migration login', ['migrationLogin=postgres']],
	['a migration login that is not an identifier', ['migrationLogin=ariva; drop']],
	// ARV-082a: TLS needs its secret by name and a floor of TLS 1.2 at least.
	['database TLS without a secret name', ['tls.enabled=true', 'tls.secretName=']],
	['a database TLS secret name that is not a Kubernetes name', ['tls.enabled=true', 'tls.secretName=Db_TLS']],
	['database TLS with TLS 1.1 as the floor', ['tls.enabled=true', 'tls.minProtocolVersion=TLSv1.1']],
	['a tls.enabled that is not true or false', ['tls.enabled=yes']],
	['the database tls values removed', ['tls=null']],
	// ARV-232: the server's max_connections is a whole number in range.
	['database max_connections below 20', ['maxConnections=5']],
	['database max_connections above 5000', ['maxConnections=5001']],
	['database max_connections that is not a whole number', ['maxConnections=100.5']],
	['database max_connections that is not a number', ['maxConnections=abc']]
]) {
	if (renderDatabase(sets).status === 0) {
		failed = true;
		console.error(`FAIL release guard: rendering ${label} must fail but succeeded`);
	} else {
		console.log(`PASS release guard: ${label} is refused`);
	}
}

/** ARV-082b: a values file whose one Ingress (to api-main) carries the given annotation line, in a folder of its own. */
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-chart-values-'));
process.on('exit', () => fs.rmSync(scratch, { recursive: true, force: true }));
function ingressValues(annotation) {
	const file = path.join(scratch, `ingress-${createHash('sha256').update(annotation).digest('hex').slice(0, 12)}.yaml`);
	fs.writeFileSync(file, `ingresses:\n  - name: api-main\n    comment: test\n    subdomain: api-main-test\n    serviceName: api-main-service\n    annotations:\n      nginx.ingress.kubernetes.io/${annotation}\n`);
	return file;
}

/** ARV-084: a values file of the given values (for example a list of ingresses), in the same folder. */
function valuesFile(values) {
	const text = stringify(values);
	const file = path.join(scratch, `values-${createHash('sha256').update(text).digest('hex').slice(0, 12)}.yaml`);
	fs.writeFileSync(file, text);
	return file;
}

/** A production build number, for the renders of values-k8s-prd.yaml (ARV-002 refuses it without one). */
const PRD = 'buildNumber=main-20261001.1';

/** ARV-084: an ingresses entry for the guards and the site cases below. */
const ingressEntry = (name, serviceName, subdomain, more = {}) => ({ name, comment: 'test', subdomain, serviceName, ...more });

for (const [label, file, sets, message, annotation] of [
	['production without a build number', 'values-k8s-prd.yaml', []],
	// ARV-232: the connections every replica may hold fit the server's max_connections, and each host's pool is in range.
	['production replicas that need more database connections than the server allows', 'values-k8s-prd.yaml', [PRD, 'databaseConnections.serverMaxConnections=100'],
		'pass what the server allows'],
	['production at the chart\'s default server limit (values-k8s-prd.yaml sets none; the Helmfile does)', 'values-k8s-prd.yaml', [PRD, 'databaseConnections.serverMaxConnections=200'],
		'ingest (4 + 1) x (26 + 0)'],
	['one replica of each host at a server limit of 50', 'values-k8s-dev.yaml', ['databaseConnections.serverMaxConnections=50'], 'pass what the server allows'],
	['an HPA maximum that takes api-ingest past the server limit', 'values-k8s-dev.yaml', ['apiIngestHpaMax=5'], 'ingest (5 + 2) x (26 + 0)'],
	['fixed replicas that take api-ingest past the server limit', 'values-k8s-dev.yaml', ['apiIngestReplicas=3'], 'ingest (3 + 1) x (26 + 0)'],
	['a replica count that is not a number', 'values-k8s-dev.yaml', ['apiIngestHpaMax=abc'], 'apiIngestHpaMax must be a whole number of replicas'],
	['a negative replica count', 'values-k8s-dev.yaml', ['apiStreamReplicas=-5'], 'apiStreamReplicas must be a whole number of replicas'],
	// Helm's --set reads 1.5 as text; the fraction itself is the --set-json case after this table.
	['a replica count Helm reads as text', 'values-k8s-dev.yaml', ['replicas=1.5'], 'replicas must be a whole number of replicas'],
	['a pool below 2', 'values-k8s-dev.yaml', ['databaseConnections.poolSize.stream=1'], 'databaseConnections.poolSize.stream must be a whole number from 2 to 100'],
	['a pool above 100', 'values-k8s-dev.yaml', ['databaseConnections.poolSize.main=101'], 'databaseConnections.poolSize.main must be a whole number from 2 to 100'],
	['a pool for a host that does not exist', 'values-k8s-dev.yaml', ['databaseConnections.poolSize.web=10'], 'databaseConnections.poolSize.web is not a host'],
	['a host without a pool', 'values-k8s-dev.yaml', ['databaseConnections.poolSize.cronz=null'], 'databaseConnections.poolSize.cronz must be a whole number'],
	['the database connection values removed', 'values-k8s-dev.yaml', ['databaseConnections=null'], 'databaseConnections must be a map'],
	['a negative operators share', 'values-k8s-dev.yaml', ['databaseConnections.operators=-1'], 'databaseConnections.operators must be a whole number'],
	['production with trunk', 'values-k8s-prd.yaml', ['buildNumber=trunk']],
	['the latest tag', 'values-k8s-dev.yaml', ['buildNumber=latest']],
	['production with the demo seed', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'demoSeed=true']],
	['MQTT without its TLS secret', 'values-k8s-dev.yaml', ['mqtt.enabled=true', 'mqtt.tlsSecretName=']],
	// ARV-104g1: the validation reader login's secret is named, and required in production.
	['the validation reader login without a secret name', 'values-k8s-dev.yaml', ['validationReader.secretName=']],
	['a validation reader secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['validationReader.secretName=Reader_Secret']],
	['production with an optional validation reader secret', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'validationReader.optional=true']],
	// ARV-111a: the retention periods are whole days within RetentionSettings' bounds, and only the six known keys.
	['a raw sensing retention below 7 days', 'values-k8s-dev.yaml', ['migration.retention.rawSensingDays=6']],
	['a raw sensing retention above 3650 days', 'values-k8s-dev.yaml', ['migration.retention.rawSensingDays=3651']],
	['a desk zone reading retention above 90 days', 'values-k8s-dev.yaml', ['migration.retention.deskZoneReadingDays=91']],
	['a lane staffing retention below the alert backtest history', 'values-k8s-dev.yaml', ['migration.retention.laneStaffingDays=30']],
	['an alert email retention below 30 days in production', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'migration.retention.alertEmailDays=29']],
	['a retention period that is not a whole number', 'values-k8s-dev.yaml', ['migration.retention.integrationCallDays=30.5']],
	['a retention period that is not a number', 'values-k8s-dev.yaml', ['migration.retention.reportDeliveryDays=forever']],
	['an unknown retention period', 'values-k8s-dev.yaml', ['migration.retention.auditDays=40']],
	['the retention periods removed', 'values-k8s-dev.yaml', ['migration.retention=null']],
	// ARV-125b: monitoring thresholds out of range, an unknown rule, a channel name that could break the JSON, a rule
	// without its values, a claim that is not a name.
	['a disk threshold above 99 percent', 'values-k8s-dev.yaml', ['monitoring.alerts.disk.usedPercent=150']],
	['a critical certificate expiry not below the warning', 'values-k8s-dev.yaml', ['monitoring.alerts.certificateExpiry.criticalDays=40']],
	// ARV-092c: the scanner's signature age limit stays within the hosts' MalwareScan:MaxSignatureAgeHours bounds, in whole hours.
	['a signature age limit under 6 hours', 'values-k8s-dev.yaml', ['monitoring.alerts.scannerSignaturesStale.maxAgeHours=5'], 'monitoring.alerts.scannerSignaturesStale.maxAgeHours must be a whole number of hours from 6 to 168'],
	['a signature age limit over a week', 'values-k8s-dev.yaml', ['monitoring.alerts.scannerSignaturesStale.maxAgeHours=169'], 'must be a whole number of hours from 6 to 168'],
	['a signature age limit in part hours', 'values-k8s-dev.yaml', ['monitoring.alerts.scannerSignaturesStale.maxAgeHours=48.5'], 'must be a whole number of hours from 6 to 168'],
	['the scanner rule without its values', 'values-k8s-dev.yaml', ['monitoring.alerts.scannerNotAnswering=null'], 'monitoring.alerts.scannerNotAnswering is missing'],
	['a devices offline share above 1', 'values-k8s-dev.yaml', ['monitoring.alerts.devicesOffline.share=2']],
	// ARV-254: the consumer hold rule's limit is whole seconds from a minute to a day.
	['a consumer hold limit under a minute', 'values-k8s-dev.yaml', ['monitoring.alerts.consumerHeld.holdLimit=59'], 'monitoring.alerts.consumerHeld.holdLimit must be a whole number of seconds from 60 to 86400'],
	['a consumer hold limit over a day', 'values-k8s-dev.yaml', ['monitoring.alerts.consumerHeld.holdLimit=86401'], 'must be a whole number of seconds from 60 to 86400'],
	['a consumer hold limit in part seconds', 'values-k8s-dev.yaml', ['monitoring.alerts.consumerHeld.holdLimit=420.5'], 'must be a whole number of seconds from 60 to 86400'],
	['a rule window of 0 minutes', 'values-k8s-dev.yaml', ['monitoring.alerts.hostDown.minutes=0']],
	// ARV-257: the AMAN feed rule's window is 2 minutes at least, above a healthy feed's age.
	['an AMAN feed rule window of 1 minute', 'values-k8s-dev.yaml', ['monitoring.alerts.amanFeedStale.minutes=1'], 'monitoring.alerts.amanFeedStale.minutes must be 2 or more'],
	['a rule window that is not a whole number', 'values-k8s-dev.yaml', ['monitoring.alerts.streamConsumerLag.minutes=2.5']],
	['a severity that is not warning or critical', 'values-k8s-dev.yaml', ['monitoring.alerts.busDegraded.severity=page']],
	['a values entry for a rule that does not exist', 'values-k8s-dev.yaml', ['monitoring.alerts.unknownRule.minutes=5']],
	['a channel name with a quote', 'values-k8s-dev.yaml', ['monitoring.channels={bad"name}']],
	['a disk rule without its claims', 'values-k8s-dev.yaml', ['monitoring.alerts.disk.claims=null']],
	['a claim that is not a Kubernetes name', 'values-k8s-dev.yaml', ["monitoring.alerts.disk.claims={data') OR (1}"]],
	// ARV-231: the queue refusal rule's kinds are QueueRejections counts, so a value cannot break its filter.
	['a queue refusal kind that is not one', 'values-k8s-dev.yaml', ["monitoring.alerts.queueRefused.kinds={BufferFull,x') OR (1}"], 'is not a queue engine refusal kind'],
	['a queue refusal rule with an empty kind', 'values-k8s-dev.yaml', ['monitoring.alerts.queueRefused.kinds={}'], '"" is not a queue engine refusal kind'],
	['a queue refusal rule whose kinds are not a list', 'values-k8s-dev.yaml', ['monitoring.alerts.queueRefused.kinds=BufferFull'], 'monitoring.alerts.queueRefused.kinds must be a list'],
	// ARV-125b: the scopes that keep a rule on its own release: the environment and the namespaces.
	['a monitoring environment that is not a plain name', 'values-k8s-dev.yaml', ["monitoring.environment=k8s-dev' OR deployment.environment = 'k8s-prd"]],
	['an environment that is not a plain name when monitoring reads it', 'values-k8s-dev.yaml', ['environment=k8s dev']],
	['a disk namespace that is not a Kubernetes name', 'values-k8s-dev.yaml', ['monitoring.alerts.disk.namespaces={Ariva_NS}']],
	['a disk namespaces value that is not a list', 'values-k8s-dev.yaml', ['monitoring.alerts.disk.namespaces=ariva']],
	['a disk rule without its namespaces', 'values-k8s-dev.yaml', ['monitoring.alerts.disk.namespaces=null']],
	['a certificate namespace that is a PromQL pattern', 'values-k8s-dev.yaml', ['monitoring.alerts.ingressCertificateExpiry.namespaces={.*}']],
	['a certificate namespace label other than namespace or exported_namespace', 'values-k8s-dev.yaml', ['monitoring.alerts.ingressCertificateExpiry.namespaceLabel=job']],
	// ARV-082a: production sends telemetry over TLS only; the servers' CA secrets are Kubernetes names.
	['production with an http OTLP endpoint', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'otel.endpoint=http://otel.example:4317'], 'otel.endpoint must be an https:// URL'],
	['production with an OTLP endpoint without a scheme', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'otel.endpoint=otel.example:4317'], 'otel.endpoint must be an https:// URL'],
	['a database CA secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['databaseCaSecretName=Database_CA'], 'databaseCaSecretName must be a Kubernetes secret name'],
	['a Redis CA secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['redisCaSecretName=redis ca'], 'redisCaSecretName must be a Kubernetes secret name'],
	// ARV-082b: production serves the pods over TLS; the secret names are Kubernetes names, for the services that serve TLS;
	// with pod TLS the simulator and an Ingress to it are refused, and an Ingress keeps the chart's TLS annotations and retries.
	['production with pod TLS off', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'podTls.enabled=false'], 'k8s-prd serves the pods over TLS'],
	['production without the pod TLS values', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'podTls=null'], 'podTls must be a map'],
	['a podTls.enabled that is not true or false', 'values-k8s-dev.yaml', ['podTls.enabled=yes'], 'podTls.enabled must be true or false'],
	['a pod certificate secret name that is not a Kubernetes name', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'podTls.secretNames.api-main=Main_TLS'], 'podTls.secretNames.api-main must be a Kubernetes secret name'],
	['a web certificate secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['podTls.secretNames.web=web tls'], 'podTls.secretNames.web must be a Kubernetes secret name'],
	['a pod certificate for a service that serves no TLS', 'values-k8s-dev.yaml', ['podTls.secretNames.simulation=ariva-simulation-tls'], 'podTls.secretNames.simulation is not a service that serves TLS'],
	['a cluster CA secret name that is not a Kubernetes name', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'podTls.caSecretName=cluster ca'], 'podTls.caSecretName must be a Kubernetes secret name'],
	['pod TLS with the simulator', 'values-k8s-dev.yaml', ['podTls.enabled=true'], 'podTls.enabled needs simulationEnabled false'],
	['pod TLS with an Ingress to the simulator', 'values-k8s-dev.yaml', ['podTls.enabled=true', 'simulationEnabled=false'], 'an Ingress routes only to a service that serves TLS'],
	['an Ingress with its own backend-protocol under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'nginx.ingress.kubernetes.io/backend-protocol is set by the chart', 'backend-protocol: "HTTP"'],
	['an Ingress with its own proxy-ssl-verify under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'nginx.ingress.kubernetes.io/proxy-ssl-verify is set by the chart', 'proxy-ssl-verify: "off"'],
	['an Ingress whose proxy-next-upstream drops error and timeout under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'proxy-next-upstream must keep error and timeout', 'proxy-next-upstream: "http_502 http_503"'],
	// ARV-082b: no site snippet writes proxy_ssl or proxy_pass, in any annotation and in any case.
	['a configuration-snippet that turns the verification off under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'configuration-snippet writes a proxy_ssl, grpc_ssl, _pass or _by_lua directive itself', 'configuration-snippet: "proxy_ssl_verify off;"'],
	['a server-snippet that proxies around the TLS backend under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'server-snippet writes a proxy_ssl, grpc_ssl, _pass or _by_lua directive itself', 'server-snippet: "location /plain { proxy_pass http://api-main-service:80; }"'],
	['a location-snippet that trusts the system store under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'location-snippet writes a proxy_ssl, grpc_ssl, _pass or _by_lua directive itself', 'location-snippet: "Proxy_SSL_Trusted_Certificate /etc/ssl/certs/ca-certificates.crt;"'],
	['a configuration-snippet that passes to the pod through gRPC under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'configuration-snippet writes a proxy_ssl, grpc_ssl, _pass or _by_lua directive itself', 'configuration-snippet: "grpc_pass grpc://api-main-service:80;"'],
	['a server-snippet with a Lua block under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'server-snippet writes a proxy_ssl, grpc_ssl, _pass or _by_lua directive itself', 'server-snippet: "access_by_lua_block { ngx.exit(200) }"'],
	// ARV-084: the machine hosts refuse plain HTTP in production; plainHttp is refuse or redirect and one per host; a site
	// annotation sets no key of the chart's, no www redirect, no custom headers, nothing that names or clears the headers,
	// no rule or audit engine, and one server-snippet per host.
	['production with the ingest host redirecting plain HTTP', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'k8s-prd refuses plain HTTP on the machine hosts',
		{ ingresses: [ingressEntry('api-ingest', 'api-ingest-service', 'api-ingest-test', { plainHttp: 'redirect' })] }],
	['production with the integration feeds redirecting plain HTTP', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'k8s-prd refuses plain HTTP on the machine hosts',
		{ ingresses: [ingressEntry('api-integration', 'api-integration-service', 'api-integration-test', { plainHttp: 'redirect' }),
			ingressEntry('api-integration-feeds', 'api-integration-service', 'api-integration-test', { plainHttp: 'redirect', paths: ['/api/v1/integration/'] })] }],
	['production with the main API host redirecting plain HTTP', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'k8s-prd refuses plain HTTP on the machine hosts',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { plainHttp: 'redirect' })] }],
	['a plainHttp other than refuse or redirect', 'values-k8s-dev.yaml', [], 'plainHttp must be refuse or redirect, not allow',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { plainHttp: 'allow' })] }],
	['the Ingresses of one host on different plainHttp', 'values-k8s-dev.yaml', [], 'the Ingresses of host web-test must share one plainHttp',
		{ ingresses: [ingressEntry('web', 'web-service', 'web-test', { plainHttp: 'redirect' }), ingressEntry('web-api', 'api-main-service', 'web-test', { paths: ['/api'] })] }],
	['a site annotation that turns the HTTPS redirect back on', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'nginx.ingress.kubernetes.io/ssl-redirect is set by the chart (ARV-084)', 'ssl-redirect: "true"'],
	['a site annotation that forces the HTTPS redirect', 'values-k8s-dev.yaml', [], 'nginx.ingress.kubernetes.io/force-ssl-redirect is set by the chart (ARV-084)', 'force-ssl-redirect: "true"'],
	['a site annotation that sends the affinity cookie cross site', 'values-k8s-dev.yaml', [], 'nginx.ingress.kubernetes.io/session-cookie-samesite is set by the chart (ARV-084)', 'session-cookie-samesite: "None"'],
	['a site annotation that drops Secure from the affinity cookie', 'values-k8s-dev.yaml', [], 'nginx.ingress.kubernetes.io/session-cookie-secure is set by the chart (ARV-084)', 'session-cookie-secure: "false"'],
	["a site annotation that prefers the server's cipher order", 'values-k8s-dev.yaml', [], 'nginx.ingress.kubernetes.io/ssl-prefer-server-ciphers is set by the chart (ARV-084)', 'ssl-prefer-server-ciphers: "true"'],
	['a site annotation that marks a machine host as redirecting', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'ariva.dalil.tech/plain-http is set by the chart (ARV-084)',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { annotations: { 'ariva.dalil.tech/plain-http': 'redirect' } })] }],
	['the www redirect', 'values-k8s-dev.yaml', [], 'from-to-www-redirect makes ingress-nginx generate a server of its own', 'from-to-www-redirect: "true"'],
	['custom headers from a ConfigMap', 'values-k8s-dev.yaml', [], 'custom-headers names a ConfigMap the chart cannot read', 'custom-headers: "ariva/site-headers"'],
	['a configuration-snippet that shortens HSTS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'configuration-snippet names Strict-Transport-Security, X-Content-Type-Options, Content-Security-Policy or more_clear_headers',
		'configuration-snippet: "more_set_headers \\"Strict-Transport-Security: max-age=0\\";"'],
	['a server-snippet that clears nosniff', 'values-k8s-dev.yaml', [], 'server-snippet names Strict-Transport-Security, X-Content-Type-Options, Content-Security-Policy or more_clear_headers',
		'server-snippet: "more_clear_headers X-Content-Type-Options;"'],
	['a configuration-snippet that adds a policy of its own', 'values-k8s-dev.yaml', [], 'configuration-snippet names Strict-Transport-Security, X-Content-Type-Options, Content-Security-Policy or more_clear_headers',
		'configuration-snippet: "add_header Content-Security-Policy \\"frame-ancestors *\\" always;"'],
	['a modsecurity-snippet that turns the rule engine off', 'values-k8s-dev.yaml', [], 'the modsecurity-snippet sets SecRuleEngine, SecAuditEngine', 'modsecurity-snippet: "SecRuleEngine Off"'],
	// ARV-091: no site annotation turns the ModSecurity audit log back on (in the modsecurity-snippet, by loading the controller's
	// modsecurity.conf or rules from elsewhere, or through a modsecurity directive in another snippet, a quote-break included),
	// writes an access_log or error_log of its own, or routes the live hub without tokenInQuery.
	['a modsecurity-snippet that turns the audit log back on', 'values-k8s-dev.yaml', [], 'the modsecurity-snippet sets SecRuleEngine, SecAuditEngine', 'modsecurity-snippet: "SecAuditEngine RelevantOnly"'],
	['a modsecurity-snippet that turns the audit log on in production', 'values-k8s-prd.yaml', [PRD], 'the modsecurity-snippet sets SecRuleEngine, SecAuditEngine', 'modsecurity-snippet: "secauditengine On"'],
	['a modsecurity-snippet that sets the audit log parts', 'values-k8s-dev.yaml', [], 'the modsecurity-snippet sets SecRuleEngine, SecAuditEngine, an audit log setting', 'modsecurity-snippet: "SecAuditLogParts ABIJDEFHZ"'],
	['a modsecurity-snippet that moves the audit log to standard output', 'values-k8s-dev.yaml', [], 'an audit log setting (SecAuditLog...)', 'modsecurity-snippet: "SecAuditLog /dev/stdout"'],
	['a modsecurity-snippet rule that turns the audit engine on per request', 'values-k8s-dev.yaml', [], 'ctl:auditEngine or ctl:auditLogParts',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { annotations: { 'nginx.ingress.kubernetes.io/modsecurity-snippet': 'SecRule REQUEST_URI "@beginsWith /api/auth" "id:100100,phase:1,pass,nolog,ctl:auditEngine=On"' } })] }],
	["a modsecurity-snippet that includes the controller's modsecurity.conf", 'values-k8s-dev.yaml', [], 'or loads rules (Include, SecRemoteRules)', 'modsecurity-snippet: "Include /etc/nginx/modsecurity/modsecurity.conf"'],
	['a modsecurity-snippet that loads remote rules', 'values-k8s-dev.yaml', [], 'or loads rules (Include, SecRemoteRules)', 'modsecurity-snippet: "SecRemoteRules key https://rules.example/ariva.conf"'],
	["a modsecurity-snippet that quotes its way out to the controller's modsecurity.conf", 'values-k8s-dev.yaml', [], 'modsecurity-snippet writes a modsecurity directive',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { annotations: { 'nginx.ingress.kubernetes.io/modsecurity-snippet': "SecRule ARGS \"@rx x\" \"id:100101,phase:2,pass,nolog\"\n';modsecurity_rules_file /etc/nginx/modsecurity/modsecurity.conf;\nmodsecurity_rules '" } })] }],
	["a configuration-snippet that loads the controller's modsecurity.conf", 'values-k8s-dev.yaml', [], 'configuration-snippet writes a modsecurity directive', 'configuration-snippet: "modsecurity_rules_file /etc/nginx/modsecurity/modsecurity.conf;"'],
	['a server-snippet that turns the audit engine on', 'values-k8s-prd.yaml', [PRD], 'server-snippet writes a modsecurity directive', "server-snippet: \"modsecurity_rules 'SecAuditEngine On';\""],
	['a configuration-snippet that turns ModSecurity off', 'values-k8s-dev.yaml', [], 'configuration-snippet writes a modsecurity directive', 'configuration-snippet: "add_header X-Site 1;\\nmodsecurity off;"'],
	["a configuration-snippet that logs the request line with nginx's combined format", 'values-k8s-dev.yaml', [], 'configuration-snippet writes an access_log or error_log directive', 'configuration-snippet: "access_log /var/log/nginx/access.log combined;"'],
	['a server-snippet that logs requests to a file of its own', 'values-k8s-prd.yaml', [PRD], 'server-snippet writes an access_log or error_log directive', 'server-snippet: "client_header_timeout 30s; access_log /tmp/requests.log;"'],
	['a configuration-snippet that logs the live hub below critical', 'values-k8s-dev.yaml', [], 'configuration-snippet writes an access_log or error_log directive',
		{ ingresses: [ingressEntry('web-hubs', 'api-main-service', 'web-test', { plainHttp: 'redirect', tokenInQuery: true, paths: ['/hubs'], annotations: { 'nginx.ingress.kubernetes.io/configuration-snippet': 'error_log /dev/stderr info;' } })] }],
	['a site annotation that turns the live hub\'s access log back on', 'values-k8s-dev.yaml', [], 'nginx.ingress.kubernetes.io/enable-access-log is set by the chart',
		{ ingresses: [ingressEntry('web-hubs', 'api-main-service', 'web-test', { plainHttp: 'redirect', tokenInQuery: true, paths: ['/hubs'], annotations: { 'nginx.ingress.kubernetes.io/enable-access-log': 'true' } })] }],
	['an Ingress that routes /hubs without tokenInQuery', 'values-k8s-dev.yaml', [], 'ingresses api-main: routes /hubs without tokenInQuery',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { paths: ['/hubs'] })] }],
	['an Ingress that routes the hub in capitals without tokenInQuery', 'values-k8s-prd.yaml', [PRD], 'ingresses web-hubs: routes /HUBS/live without tokenInQuery',
		{ ingresses: [ingressEntry('web-hubs', 'api-main-service', 'web-test', { paths: ['/api', '/HUBS/live'] })] }],
	['a tokenInQuery Ingress that routes more than the hub', 'values-k8s-dev.yaml', [], "ingresses api-main: tokenInQuery is the live hub's (no access log, no audit log, critical errors only), so its paths are /hubs or under it, not /",
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { tokenInQuery: true })] }],
	['a tokenInQuery that is not true or false', 'values-k8s-dev.yaml', [], 'ingresses web-hubs: tokenInQuery must be true or false',
		{ ingresses: [ingressEntry('web-hubs', 'api-main-service', 'web-test', { plainHttp: 'redirect', tokenInQuery: 'false', paths: ['/hubs'] })] }],
	// ARV-091, review round 2: no ModSecurity debug log, no rule that copies request values into its msg or logdata, no quoted
	// directive name, and no OpenTelemetry on the live hub's Ingress.
	['a modsecurity-snippet that writes a debug log to standard output', 'values-k8s-dev.yaml', [], 'a debug log (SecDebugLog...)', 'modsecurity-snippet: "SecDebugLog /dev/stdout"'],
	['a modsecurity-snippet that raises the debug log level', 'values-k8s-prd.yaml', [PRD], 'a debug log (SecDebugLog...)', 'modsecurity-snippet: "SecDebugLogLevel 9"'],
	["a site rule whose msg copies the Authorization header", 'values-k8s-dev.yaml', [], 'the modsecurity-snippet expands a macro of the request\'s values',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { annotations: { 'nginx.ingress.kubernetes.io/modsecurity-snippet': 'SecRule REQUEST_HEADERS:Authorization "@rx ." "id:100102,phase:1,pass,log,msg:\\\'%{REQUEST_HEADERS.Authorization}\\\'"' } })] }],
	['a site rule whose logdata copies the matched value', 'values-k8s-dev.yaml', [], 'the modsecurity-snippet expands a macro of the request\'s values',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { annotations: { 'nginx.ingress.kubernetes.io/modsecurity-snippet': 'SecRule REQUEST_COOKIES "@rx ." "id:100103,phase:1,pass,log,msg:\\\'cookie\\\',logdata:\\\'%{matched_var}\\\'"' } })] }],
	['a site rule whose logdata copies a capture', 'values-k8s-prd.yaml', [PRD], 'the modsecurity-snippet expands a macro of the request\'s values',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { annotations: { 'nginx.ingress.kubernetes.io/modsecurity-snippet': 'SecRule REQUEST_HEADERS:Authorization "@rx (Bearer .+)" "id:100104,phase:1,pass,log,capture,msg:\\\'bearer\\\',logdata:\\\'%{TX.1}\\\'"' } })] }],
	['a site rule whose msg copies the query string', 'values-k8s-dev.yaml', [], 'the modsecurity-snippet expands a macro of the request\'s values',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { annotations: { 'nginx.ingress.kubernetes.io/modsecurity-snippet': 'SecRule ARGS_GET:access_token "@rx ." "id:100105,phase:1,pass,log,msg:\\\'%{QUERY_STRING}\\\'"' } })] }],
	['a site rule that saves an argument for a later message', 'values-k8s-dev.yaml', [], 'the modsecurity-snippet expands a macro of the request\'s values',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { annotations: { 'nginx.ingress.kubernetes.io/modsecurity-snippet': 'SecAction "id:100106,phase:2,pass,nolog,setvar:tx.saved=%{ARGS.password}"' } })] }],
	['a configuration-snippet with a quoted access_log', 'values-k8s-dev.yaml', [], 'configuration-snippet writes an access_log or error_log directive', 'configuration-snippet: "\\"access_log\\" /dev/stdout combined;"'],
	['a server-snippet with a quoted error_log', 'values-k8s-prd.yaml', [PRD], 'server-snippet writes an access_log or error_log directive', "server-snippet: \"client_header_timeout 30s; 'error_log' /dev/stderr debug;\""],
	["a server-snippet with a quoted modsecurity directive", 'values-k8s-dev.yaml', [], 'server-snippet writes a modsecurity directive', "server-snippet: \"'modsecurity_rules_file' /etc/nginx/modsecurity/modsecurity.conf;\""],
	['a site annotation that turns OpenTelemetry on for the live hub', 'values-k8s-dev.yaml', [], 'nginx.ingress.kubernetes.io/enable-opentelemetry is set by the chart',
		{ ingresses: [ingressEntry('web-hubs', 'api-main-service', 'web-test', { plainHttp: 'redirect', tokenInQuery: true, paths: ['/hubs'], annotations: { 'nginx.ingress.kubernetes.io/enable-opentelemetry': 'true' } })] }],
	['the Ingresses of one host with different server-snippets', 'values-k8s-dev.yaml', [], 'the Ingresses of host api-integration-test carry different server-snippet annotations',
		{ ingresses: [ingressEntry('api-integration', 'api-integration-service', 'api-integration-test', { annotations: { 'nginx.ingress.kubernetes.io/server-snippet': 'client_header_timeout 30s;' } }),
			ingressEntry('api-integration-feeds', 'api-integration-service', 'api-integration-test', { paths: ['/api/v1/integration/'] })] }],
	// ARV-085: production isolates every pod and names the trusted proxies; no network the chart opens is everyone, a CIDR is a
	// network address, every peer and port is well formed, the hosts' own needs have their peers; the Cronz dashboard has its
	// source range in production; no Ingress routes the probes or sets the source range annotations itself.
	['production with the network policies off', 'values-k8s-prd.yaml', [PRD], 'k8s-prd isolates every pod: set networkPolicies.enabled true', { networkPolicies: { enabled: false } }],
	['production without the network policy values', 'values-k8s-prd.yaml', [PRD, 'networkPolicies=null'], 'networkPolicies must be a map'],
	['a networkPolicies.enabled that is not true or false', 'values-k8s-dev.yaml', ['networkPolicies.enabled=yes'], 'networkPolicies.enabled must be true or false'],
	['an unknown network policy setting', 'values-k8s-dev.yaml', [], 'networkPolicies.monitoring is not a setting', { networkPolicies: { monitoring: { to: [] } } }],
	['production without the trusted proxy networks', 'values-k8s-prd.yaml', [PRD], 'k8s-prd needs ingressController.podNetworks', { ingressController: { podNetworks: [] } }],
	['production trusting a /13 as the proxy network', 'values-k8s-prd.yaml', [PRD], 'ingressController.podNetworks: 10.0.0.0/13 is wider than /14', { ingressController: { podNetworks: ['10.0.0.0/13'] } }],
	['production trusting an IPv6 /47 as the proxy network', 'values-k8s-prd.yaml', [PRD], 'ingressController.podNetworks: fd00::/47 is wider than /48', { ingressController: { podNetworks: ['10.244.0.0/16', 'fd00::/47'] } }],
	['trusting every address as the proxy network', 'values-k8s-dev.yaml', [], 'ingressController.podNetworks: 0.0.0.0/0 is wider than /1', { ingressController: { podNetworks: ['0.0.0.0/0'] } }],
	['trusting every IPv6 address as the proxy network', 'values-k8s-dev.yaml', [], 'ingressController.podNetworks: ::/0 is wider than /1', { ingressController: { podNetworks: ['::/0'] } }],
	['a proxy network with host bits', 'values-k8s-dev.yaml', [], '10.244.0.1/16 is not a network address', { ingressController: { podNetworks: ['10.244.0.1/16'] } }],
	['a proxy network that is not a CIDR', 'values-k8s-dev.yaml', [], 'ingressController.podNetworks: 10.244.0.0 is not a CIDR', { ingressController: { podNetworks: ['10.244.0.0'] } }],
	['a proxy network list that is not a list', 'values-k8s-dev.yaml', ['ingressController.podNetworks=10.244.0.0/16'], 'ingressController.podNetworks must be a list of CIDRs'],
	// The hosts' rules, refused before a host would refuse to start: no address bit after the prefix, no IPv6 network that trusts
	// IPv4 clients wider than a /14 (the IPv4-mapped block, a network holding it, a prefix from /65 to /77 or /97 to /109).
	...[['10.244.1.0/16', 'is not a network address'], ['fd00:10:244:1::/48', 'is not a network address'], ['fd00:10:244::/65', 'trusts IPv4 clients wider than an IPv4 /14'],
		['fd00::/77', 'trusts IPv4 clients wider than an IPv4 /14'], ['fd00::/97', 'trusts IPv4 clients wider than an IPv4 /14'], ['fd00::/109', 'trusts IPv4 clients wider than an IPv4 /14'],
		['fd00::a00:0/104', 'trusts IPv4 clients wider than an IPv4 /14'], ['fd00::/104', 'trusts IPv4 clients wider than an IPv4 /14'], ['::ffff:0:0/96', 'trusts IPv4 clients wider than an IPv4 /14'],
		['::/64', 'trusts IPv4 clients wider than an IPv4 /14'], ['::/80', 'trusts IPv4 clients wider than an IPv4 /14']]
		.map(([network, reason]) => [`production trusting ${network}`, 'values-k8s-prd.yaml', [PRD], `ingressController.podNetworks: ${network} ${reason}`, { ingressController: { podNetworks: ['10.244.0.0/16', network] } }]),
	['an ingress controller selector that selects every pod', 'values-k8s-dev.yaml', [], 'ingressController.podSelector selects everything', { ingressController: { podSelector: { matchLabels: null } } }],
	['an ingress controller selector of another shape', 'values-k8s-dev.yaml', [], 'ingressController.namespaceSelector has names: a label selector has matchLabels and matchExpressions only',
		{ ingressController: { namespaceSelector: { names: ['ingress-nginx'] } } }],
	['production opening an outbound endpoint to every address', 'values-k8s-prd.yaml', [PRD], 'networkPolicies.outbound[0].to[0].ipBlock: 0.0.0.0/0 is wider than /8',
		{ networkPolicies: { outbound: [{ name: 'aodb', to: [{ ipBlock: { cidr: '0.0.0.0/0' } }], ports: [443] }] } }],
	['production opening the collector to every IPv6 address', 'values-k8s-prd.yaml', [PRD], 'networkPolicies.otlp.to[0].ipBlock: ::/0 is wider than /16', { networkPolicies: { otlp: { to: [{ ipBlock: { cidr: '::/0' } }] } } }],
	['production opening half of every address', 'values-k8s-prd.yaml', [PRD], 'networkPolicies.loki.to[0].ipBlock: 0.0.0.0/1 is wider than /8', { networkPolicies: { loki: { to: [{ ipBlock: { cidr: '0.0.0.0/1' } }] } } }],
	['production reaching every pod of every namespace', 'values-k8s-prd.yaml', [PRD], 'networkPolicies.kafka.to[0] selects every pod of every namespace', { networkPolicies: { kafka: { to: [{ namespaceSelector: {} }] } } }],
	['an ipBlock with host bits', 'values-k8s-dev.yaml', [], 'networkPolicies.smtp.to[0].ipBlock: 10.40.1.1/24 is not a network address', { networkPolicies: { smtp: { to: [{ ipBlock: { cidr: '10.40.1.1/24' } }] } } }],
	['an ipBlock except that is not a CIDR', 'values-k8s-dev.yaml', [], 'networkPolicies.smtp.to[0].ipBlock: 10.40.9.0/33 is not a CIDR', { networkPolicies: { smtp: { to: [{ ipBlock: { cidr: '10.40.0.0/16', except: ['10.40.9.0/33'] } }] } } }],
	['an ipBlock with a selector', 'values-k8s-dev.yaml', [], 'networkPolicies.redis.to[0] gives ipBlock with a selector', { networkPolicies: { redis: { to: [{ ipBlock: { cidr: '10.0.0.0/8' }, podSelector: {} }] } } }],
	['a peer of another shape', 'values-k8s-dev.yaml', [], 'networkPolicies.redis.to[0] has cidr: a peer has ipBlock, podSelector and namespaceSelector only', { networkPolicies: { redis: { to: [{ cidr: '10.0.0.0/8' }] } } }],
	['an empty peer', 'values-k8s-dev.yaml', [], 'networkPolicies.redis.to[0] must be a NetworkPolicy peer', { networkPolicies: { redis: { to: [{}] } } }],
	['a port 0', 'values-k8s-dev.yaml', [], 'networkPolicies.database.ports: 0 is not a port', { networkPolicies: { database: { ports: [0] } } }],
	['a port above 65535', 'values-k8s-dev.yaml', [], 'networkPolicies.kafka.ports: 70000 is not a port', { networkPolicies: { kafka: { ports: [70000] } } }],
	['a destination without ports', 'values-k8s-dev.yaml', [], 'networkPolicies.redis.ports must be a list of one or more TCP ports', { networkPolicies: { redis: { ports: [] } } }],
	['DNS on a port of its own', 'values-k8s-dev.yaml', [], 'networkPolicies.dns.ports is not a setting', { networkPolicies: { dns: { ports: [5353] } } }],
	['an outbound endpoint without peers', 'values-k8s-dev.yaml', [], 'networkPolicies.outbound[0].to must be a list of one or more peers', { networkPolicies: { outbound: [{ name: 'aodb', to: [], ports: [443] }] } }],
	['an outbound endpoint without ports', 'values-k8s-dev.yaml', [], 'networkPolicies.outbound[0].ports must be a list of one or more TCP ports', { networkPolicies: { outbound: [{ name: 'aodb', to: [{ ipBlock: { cidr: '10.20.0.0/24' } }] }] } }],
	['an outbound endpoint without a name', 'values-k8s-dev.yaml', [], 'networkPolicies.outbound[0].name must be a DNS label', { networkPolicies: { outbound: [{ to: [{ ipBlock: { cidr: '10.20.0.0/24' } }], ports: [443] }] } }],
	['two outbound endpoints of one name', 'values-k8s-dev.yaml', [], 'networkPolicies.outbound[1].name aodb is given twice',
		{ networkPolicies: { outbound: [{ name: 'aodb', to: [{ ipBlock: { cidr: '10.20.0.0/24' } }], ports: [443] }, { name: 'aodb', to: [{ ipBlock: { cidr: '10.21.0.0/24' } }], ports: [443] }] } }],
	['the network policies without DNS', 'values-k8s-dev.yaml', [], 'networkPolicies.dns.to is empty: with networkPolicies.enabled the pods could not reach kube-dns', { networkPolicies: { enabled: true, dns: { to: [] } } }],
	['production without the database peers', 'values-k8s-prd.yaml', [PRD], 'networkPolicies.database.to is empty', { networkPolicies: { database: { to: [] } } }],
	['production exporting telemetry the policies drop', 'values-k8s-prd.yaml', [PRD, 'otel.endpoint=https://otel.example:4317'], 'otel.endpoint is set but networkPolicies.otlp.to is empty'],
	['production with MQTT and no sensor networks', 'values-k8s-prd.yaml', [PRD, 'mqtt.enabled=true'], 'mqtt.enabled with networkPolicies.enabled needs networkPolicies.mqttSensors.from'],
	['an Ingress to a service of no chart under the network policies', 'values-k8s-dev.yaml', [], 'routes to other-service, which is not a service of the chart',
		{ networkPolicies: { enabled: true }, ingresses: [ingressEntry('other', 'other-service', 'other-test')] }],
	['production with the Cronz dashboard open to everyone', 'values-k8s-prd.yaml', [PRD], 'k8s-prd opens the Cronz dashboard to the management network only: set its sourceRanges',
		{ ingresses: [ingressEntry('api-cronz', 'api-cronz-service', 'api-cronz-test', { plainHttp: 'redirect' })] }],
	['production with the Cronz dashboard behind 0.0.0.0/0', 'values-k8s-prd.yaml', [PRD], 'ingresses api-cronz: sourceRanges: 0.0.0.0/0 is wider than /8',
		{ ingresses: [ingressEntry('api-cronz', 'api-cronz-service', 'api-cronz-test', { plainHttp: 'redirect', sourceRanges: ['0.0.0.0/0'] })] }],
	['production with the Cronz dashboard behind ::/0', 'values-k8s-prd.yaml', [PRD], 'ingresses api-cronz: sourceRanges: ::/0 is wider than /16',
		{ ingresses: [ingressEntry('api-cronz', 'api-cronz-service', 'api-cronz-test', { plainHttp: 'redirect', sourceRanges: ['192.0.2.0/24', '::/0'] })] }],
	['a source range that is not a CIDR', 'values-k8s-dev.yaml', [], 'ingresses api-main: sourceRanges: 10.10.0.0/40 is not a CIDR', { ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { sourceRanges: ['10.10.0.0/40'] })] }],
	['source ranges that are not a list', 'values-k8s-dev.yaml', [], 'ingresses api-main: sourceRanges must be a list of CIDRs', { ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { sourceRanges: '10.10.0.0/24' })] }],
	['a site allowlist-source-range', 'values-k8s-dev.yaml', [], "nginx.ingress.kubernetes.io/allowlist-source-range is the chart's: set the entry's sourceRanges instead", 'allowlist-source-range: "10.10.0.0/24"'],
	['a site whitelist-source-range', 'values-k8s-prd.yaml', [PRD], "nginx.ingress.kubernetes.io/whitelist-source-range is the chart's: set the entry's sourceRanges instead", 'whitelist-source-range: "0.0.0.0/0"'],
	['an Ingress that routes the readiness probe', 'values-k8s-dev.yaml', [], 'ingresses api-main: routes /health/readiness; the health probes are not routed through the ingress',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { paths: ['/health/readiness'] })] }],
	['an Ingress that routes the web probe in capitals', 'values-k8s-dev.yaml', [], 'ingresses web: routes /HEALTHZ', { ingresses: [ingressEntry('web', 'web-service', 'web-test', { plainHttp: 'redirect', paths: ['/HEALTHZ'] })] }],
	// The 404 is judged on the request's own path, before any rewrite: no site rewrite-target (a fixed target, or captures on a
	// regex path), no use-regex, no snippet that writes a rewrite directive, in any environment.
	['a site rewrite-target to the readiness probe', 'values-k8s-prd.yaml', [PRD], 'nginx.ingress.kubernetes.io/rewrite-target is refused: the chart answers the health probes 404', 'rewrite-target: "/health/readiness"'],
	['a site rewrite-target with captures on a regex path', 'values-k8s-dev.yaml', [], 'nginx.ingress.kubernetes.io/rewrite-target is refused',
		{ ingresses: [ingressEntry('api-main', 'api-main-service', 'api-main-test', { paths: ['/status(/|$)(.*)'], annotations: { 'nginx.ingress.kubernetes.io/rewrite-target': '/health/$2' } })] }],
	['a site use-regex', 'values-k8s-dev.yaml', [], 'nginx.ingress.kubernetes.io/use-regex is refused', 'use-regex: "true"'],
	['a configuration-snippet that rewrites to the probe', 'values-k8s-dev.yaml', [], 'configuration-snippet writes a rewrite directive', 'configuration-snippet: "rewrite ^/status$ /health/readiness break;"'],
	['a server-snippet that rewrites after a comment', 'values-k8s-prd.yaml', [PRD], 'server-snippet writes a rewrite directive', 'server-snippet: "# site\\nrewrite ^/status$ /health/readiness last;"'],
	// ARV-091a: production ships the hosts' logs to a collector over TLS and takes the log pseudonym key from a Secret; otlp
	// needs an https endpoint and loki its peers under the NetworkPolicies; no value can hold the key itself, and each case is
	// refused for its own reason.
	['production without the log pseudonym key Secret', 'values-k8s-prd.yaml', [PRD, 'logging.pseudonymKeySecret.name='], 'k8s-prd needs logging.pseudonymKeySecret.name'],
	['production without the log pseudonym key values', 'values-k8s-prd.yaml', [PRD, 'logging.pseudonymKeySecret=null'], 'k8s-prd needs logging.pseudonymKeySecret.name'],
	['production without a log collector', 'values-k8s-prd.yaml', [PRD, 'logging.collector='], "k8s-prd ships the hosts' logs to a collector over TLS"],
	['production without the logging values', 'values-k8s-prd.yaml', [PRD, 'logging=null'], "k8s-prd ships the hosts' logs to a collector over TLS"],
	['production logging to the OTLP collector without an endpoint', 'values-k8s-prd.yaml', [PRD, 'logging.collector=otlp'], 'logging.collector otlp needs otel.endpoint, an https:// URL'],
	['production logging to the OTLP collector over plain HTTP', 'values-k8s-prd.yaml', [PRD, 'logging.collector=otlp', 'otel.endpoint=http://otel.example:4317'], 'otel.endpoint must be an https:// URL'],
	['dev logging to the OTLP collector over plain HTTP', 'values-k8s-dev.yaml', ['logging.collector=otlp', 'otel.endpoint=http://otel.example:4317'], 'logging.collector otlp needs otel.endpoint, an https:// URL'],
	['production logging to an OTLP collector the policies drop', 'values-k8s-prd.yaml', [PRD, 'logging.collector=otlp', 'otel.endpoint=https://otel.example:4317'], 'otel.endpoint is set but networkPolicies.otlp.to is empty'],
	['production logging to Loki without its peers', 'values-k8s-prd.yaml', [PRD], 'logging.collector loki needs networkPolicies.loki.to', { networkPolicies: { loki: { to: [] } } }],
	['dev logging to Loki under the network policies without its peers', 'values-k8s-dev.yaml', ['logging.collector=loki', 'networkPolicies.enabled=true'], 'logging.collector loki needs networkPolicies.loki.to'],
	['a literal log pseudonym key', 'values-k8s-dev.yaml', [`logging.pseudonymKey=${STAND_IN_KEY}`], 'logging.pseudonymKey is not a setting'],
	['a literal log pseudonym key beside its Secret in production', 'values-k8s-prd.yaml', [PRD, `logging.pseudonymKeySecret.value=${STAND_IN_KEY}`], 'logging.pseudonymKeySecret.value is not a setting'],
	['a literal log pseudonym key as the Secret\'s key', 'values-k8s-prd.yaml', [PRD], 'logging.pseudonymKeySecret.key must be a key of a Kubernetes Secret', { logging: { pseudonymKeySecret: { key: STAND_IN_KEY } } }],
	['a literal log pseudonym key without padding as the Secret\'s key', 'values-k8s-dev.yaml', [], 'logging.pseudonymKeySecret.key names the Secret\'s key',
		{ logging: { pseudonymKeySecret: { name: 'ariva-log-pseudonym-key', key: Buffer.alloc(33, 7).toString('base64') } } }],
	['a literal log pseudonym key as the Secret\'s name', 'values-k8s-dev.yaml', [], 'logging.pseudonymKeySecret.name must be a Kubernetes secret name', { logging: { pseudonymKeySecret: { name: STAND_IN_KEY } } }],
	['a log collector other than otlp or loki', 'values-k8s-dev.yaml', ['logging.collector=syslog'], 'logging.collector must be otlp or loki'],
	['a log pseudonym key Secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['logging.pseudonymKeySecret.name=Log_Key'], 'logging.pseudonymKeySecret.name must be a Kubernetes secret name'],
	['logging that is not a map', 'values-k8s-dev.yaml', ['logging=plain'], 'logging must be a map'],
	['a logging.required that is not true or false', 'values-k8s-dev.yaml', ['logging.required=yes'], 'logging.required must be true or false'],
	['logging.required without a collector outside production', 'values-k8s-dev.yaml', ['logging.required=true', 'logging.pseudonymKeySecret.name=ariva-log-pseudonym-key'], "a release with logging.required ships the hosts' logs"],
	['logging.required without the key Secret outside production', 'values-k8s-dev.yaml', ['logging.required=true', 'logging.collector=loki'], 'a release with logging.required needs logging.pseudonymKeySecret.name'],
	['a literal Loki password', 'values-k8s-prd.yaml', [PRD, 'logging.lokiCredentialsSecret.password=stand-in'], 'logging.lokiCredentialsSecret.password is not a setting'],
	['Loki credentials as values', 'values-k8s-dev.yaml', [], 'logging.loki is not a setting', { logging: { loki: { username: 'ariva', password: 'stand-in' } } }],
	['a Loki credentials Secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['logging.lokiCredentialsSecret.name=Loki_Credentials'], 'logging.lokiCredentialsSecret.name must be a Kubernetes secret name'],
	['a Loki password key that is not a Secret key', 'values-k8s-dev.yaml', ['logging.lokiCredentialsSecret.passwordKey=pass word'], 'logging.lokiCredentialsSecret.passwordKey must be a key of a Kubernetes Secret'],
	['Loki credentials that are not a map', 'values-k8s-dev.yaml', ['logging.lokiCredentialsSecret=ariva-loki-credentials'], 'logging.lokiCredentialsSecret must be a map'],
	// ARV-091a: the OTLP collector's sender headers come from a Secret, over https only, never as a value.
	['the collector\'s headers as a value', 'values-k8s-prd.yaml', [PRD, 'otel.headers=x-api-key'], 'otel.headers is not a setting'],
	['the collector\'s headers as a value of their Secret', 'values-k8s-dev.yaml', [], 'otel.headersSecret.value is not a setting', { otel: { endpoint: 'https://otel.example:4317', headersSecret: { name: 'ariva-otlp-headers', value: 'x-api-key=stand-in' } } }],
	['the collector\'s headers without an endpoint', 'values-k8s-dev.yaml', ['otel.headersSecret.name=ariva-otlp-headers'], 'otel.headersSecret needs otel.endpoint, an https:// URL'],
	['the collector\'s headers over plain HTTP in dev', 'values-k8s-dev.yaml', ['otel.headersSecret.name=ariva-otlp-headers', 'otel.endpoint=http://otel.example:4318'], 'otel.headersSecret needs otel.endpoint to be an https:// URL'],
	['the collector\'s headers over plain HTTP in production', 'values-k8s-prd.yaml', [PRD, 'otel.headersSecret.name=ariva-otlp-headers', 'otel.endpoint=http://otel.example:4318'], 'otel.headersSecret needs otel.endpoint to be an https:// URL'],
	['a headers Secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['otel.headersSecret.name=OTLP_Headers', 'otel.endpoint=https://otel.example:4317'], 'otel.headersSecret.name must be a Kubernetes secret name'],
	['a headers key that is not a Secret key', 'values-k8s-dev.yaml', ['otel.headersSecret.key=x-api-key=1'], 'otel.headersSecret.key must be a key of a Kubernetes Secret'],
	['headers Secret values that are not a map', 'values-k8s-dev.yaml', ['otel.headersSecret=ariva-otlp-headers'], 'otel.headersSecret must be a map'],
	['otel values that are not a map', 'values-k8s-dev.yaml', ['otel=plain'], 'otel must be a map'],
	// ARV-200: the collectors' CA Secrets are Kubernetes names, the OTLP collector's over https only, Loki's with the logs going to
	// Loki; the CA itself is never a value.
	['an OTLP collector CA Secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['otel.endpoint=https://otel.example:4317', 'otel.caSecretName=Site_OTLP_CA'], 'otel.caSecretName must be a Kubernetes secret name'],
	['an OTLP collector CA Secret that is a map', 'values-k8s-dev.yaml', [], 'otel.caSecretName must be a Kubernetes secret name', { otel: { endpoint: 'https://otel.example:4317', caSecretName: { name: 'site-otlp-ca' } } }],
	['the OTLP collector\'s CA without an endpoint', 'values-k8s-dev.yaml', ['otel.caSecretName=site-otlp-ca'], 'otel.caSecretName needs otel.endpoint, an https:// URL'],
	['the OTLP collector\'s CA over plain HTTP in dev', 'values-k8s-dev.yaml', ['otel.caSecretName=site-otlp-ca', 'otel.endpoint=http://otel.example:4318'], 'otel.caSecretName needs otel.endpoint to be an https:// URL'],
	['the OTLP collector\'s CA with an endpoint without a scheme', 'values-k8s-demo.yaml', ['otel.caSecretName=site-otlp-ca', 'otel.endpoint=otel.example:4317'], 'otel.caSecretName needs otel.endpoint to be an https:// URL'],
	['the OTLP collector\'s CA as a value', 'values-k8s-dev.yaml', [], 'otel.ca is not a setting', { otel: { endpoint: 'https://otel.example:4317', ca: '-----BEGIN CERTIFICATE-----' } }],
	['a Loki CA Secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['logging.collector=loki', 'logging.lokiCaSecretName=Site_Loki_CA'], 'logging.lokiCaSecretName must be a Kubernetes secret name'],
	['Loki\'s CA without logging to Loki', 'values-k8s-dev.yaml', ['logging.lokiCaSecretName=site-loki-ca'], 'logging.lokiCaSecretName needs logging.collector loki'],
	['Loki\'s CA in production logging to the OTLP collector', 'values-k8s-prd.yaml', [PRD, 'logging.collector=otlp', 'otel.endpoint=https://otel.example:4317', 'logging.lokiCaSecretName=site-loki-ca'],
		'logging.lokiCaSecretName needs logging.collector loki', { networkPolicies: { otlp: { to: [{ ipBlock: { cidr: '10.60.0.0/24' } }] } } }],
	['Loki\'s CA as a value', 'values-k8s-dev.yaml', [], 'logging.lokiCa is not a setting', { logging: { collector: 'loki', lokiCa: '-----BEGIN CERTIFICATE-----' } }]
]) {
	const result = render(file, sets, '', annotation ? [typeof annotation === 'string' ? ingressValues(annotation) : valuesFile(annotation)] : []);
	if (result.status === 0) {
		failed = true;
		console.error(`FAIL release guard: rendering ${label} must fail but succeeded`);
	} else if (message && !result.stderr.includes(message)) {
		failed = true;
		console.error(`FAIL release guard: rendering ${label} failed for another reason than "${message}"\n${result.stderr}`);
	} else {
		console.log(`PASS release guard: ${label} is refused`);
	}
}

// ARV-179 (the ARV-125b re-review's nit 1): an empty namespaces list reads the release's namespace, which helm template takes
// as given (Kubernetes checks it on install and upgrade only), so the chart checks it as a listed one.
{
	const findings = [];
	// An empty list through a values file: --set namespaces={} gives a list of one empty name.
	const empty = valuesFile({ monitoring: { alerts: { disk: { namespaces: [] } } } });
	const bad = render('values-k8s-dev.yaml', [], 'Ariva_NS', [empty]);
	if (bad.status === 0) findings.push('an empty disk namespaces list with a release namespace that is not a Kubernetes name rendered');
	else if (!bad.stderr.includes("monitoring.alerts.disk.namespaces is empty, so the rule reads the release's namespace")) findings.push(`refused for another reason\n${bad.stderr}`);
	const good = render('values-k8s-dev.yaml', [], 'ariva-dev', [empty]);
	if (good.status !== 0) findings.push(`an empty disk namespaces list with a valid release namespace failed\n${good.stderr}`);
	if (findings.length) {
		failed = true;
		for (const f of findings) console.error(`FAIL monitoring namespaces: ${f}`);
	} else {
		console.log('PASS monitoring namespaces: an empty list reads the release namespace, checked as a Kubernetes name');
	}
}

// ARV-082a: an https OTLP endpoint renders in production and reaches every host; the rule is production's, so dev keeps
// http; the servers' CA secret names reach exactly the five API hosts and the two jobs.
{
	const findings = [];
	// ARV-085: with the NetworkPolicies on (production), the collector's peers go with the endpoint.
	const https = render('values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'otel.endpoint=https://otel.example:4317'], '', [valuesFile({ networkPolicies: { otlp: { to: [{ ipBlock: { cidr: '10.60.0.0/24' } }] } } })]);
	if (https.status !== 0) findings.push(`production with an https OTLP endpoint: template failed\n${https.stderr}`);
	else {
		const docs = parse(https.stdout);
		findings.push(...checkManifests(docs, { environment: 'k8s-prd' }));
		const endpoints = docs.filter((doc) => /^api-[a-z]+-deployment$/.test(doc?.metadata?.name ?? '')).map((doc) =>
			(doc.spec.template.spec.containers[0].env ?? []).find((variable) => variable.name === 'OTEL_EXPORTER_OTLP_ENDPOINT')?.value);
		if (endpoints.length !== 5 || endpoints.some((value) => value !== 'https://otel.example:4317')) findings.push(`the https OTLP endpoint does not reach the five hosts (${endpoints.join(', ')})`);
	}
	if (render('values-k8s-dev.yaml', ['otel.endpoint=http://otel.example:4317']).status !== 0) findings.push('dev with an http OTLP endpoint was refused; the rule is production\'s');
	const named = render('values-k8s-dev.yaml', ['databaseCaSecretName=site-db-ca', 'redisCaSecretName=site-redis-ca']);
	if (named.status !== 0) findings.push(`the servers' CA secret names set: template failed\n${named.stderr}`);
	else findings.push(...serverCaReach(parse(named.stdout), { 'database-ca': 'site-db-ca', 'redis-ca': 'site-redis-ca' }));
	if (findings.length) {
		failed = true;
		console.error(`FAIL servers' CAs and telemetry:\n  ${findings.join('\n  ')}`);
	} else {
		console.log('PASS servers\' CAs and telemetry: an https OTLP endpoint in production on every host, http still allowed in dev, the CA secret names reach the five API hosts and the two jobs');
	}
}

// ARV-082b: pod TLS in a site's namespace with the site's secret names, and an Ingress retry list that keeps error and
// timeout, renders and holds every rule; the rules are production's switch, so dev with the simulator and its Ingress off
// renders TLS too.
{
	const findings = [];
	const names = { ...POD_TLS_SECRETS, 'api-main': 'site-main-tls', web: 'site-web-tls' };
	const site = render('values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'podTls.secretNames.api-main=site-main-tls', 'podTls.secretNames.web=site-web-tls',
		'podTls.caSecretName=site-cluster-ca'], 'ariva-site', [ingressValues('proxy-next-upstream: "error timeout http_502"')]);
	if (site.status !== 0) findings.push(`pod TLS with site names: template failed\n${site.stderr}`);
	else {
		findings.push(...checkManifests(parse(site.stdout), { environment: 'k8s-prd' }));
		const docs = parseAsKubernetes(site.stdout);
		// The site's values file holds one Ingress (to api-main): every pod but its Service checks hold as in production.
		findings.push(...podTlsRender(site.stdout, { namespace: 'ariva-site', caSecretName: 'site-cluster-ca', secretNames: names }));
		const ingress = docs.find((doc) => doc?.kind === 'Ingress');
		if (ingress?.metadata?.annotations?.['nginx.ingress.kubernetes.io/proxy-ssl-secret'] !== 'ariva-site/site-cluster-ca') findings.push('the Ingress does not verify against ariva-site/site-cluster-ca');
	}
	// A site snippet that writes neither proxy_ssl nor proxy_pass passes, as the chart's own snippets do in every render above.
	const dev = render('values-k8s-dev.yaml', ['podTls.enabled=true', 'simulationEnabled=false'], '', [ingressValues('configuration-snippet: "add_header X-Ariva-Site 1;"')]);
	if (dev.status !== 0) findings.push(`dev with pod TLS and no simulator: template failed\n${dev.stderr}`);
	else findings.push(...checkManifests(parse(dev.stdout), { environment: 'k8s-dev' }), ...podTlsRender(dev.stdout));
	if (findings.length) {
		failed = true;
		console.error(`FAIL pod TLS with site values:\n  ${findings.join('\n  ')}`);
	} else {
		console.log('PASS pod TLS with site values: a site namespace, cluster CA and certificate names reach every pod, Service and Ingress; a retry list with error and timeout renders; dev renders TLS without the simulator, with a site snippet that writes no proxy_ssl or proxy_pass');
	}
}

// ARV-084: a site's own snippets follow the chart's, on every Ingress of a host alike, and the rules still hold; a machine
// host may redirect outside production (the rule is production's); the chart's snippets are exactly the text the throwaway
// nginx runs proved (the chart's own server-snippet starts every merged one).
{
	const findings = [];
	const siteServer = 'client_header_timeout 30s;\n';
	const siteLocation = 'more_set_headers "X-Ariva-Site: 1";\n';
	// ARV-091: a site's own ModSecurity rule follows the chart's, whose SecAuditEngine Off stays the last word on the audit log.
	// Its logdata names the matched variable (MATCHED_VAR_NAME), never its value, which validate.yaml would refuse.
	const siteModsecurity = 'SecRule REQUEST_HEADERS:User-Agent "@contains sqlmap" "id:100200,phase:1,deny,status:403,log,msg:\\\'scanner\\\',logdata:\\\'%{MATCHED_VAR_NAME}\\\'"\n';
	const chartModsecurity = 'SecRuleEngine On\nSecRule FILES "@rx \\.(exe|dll|bat|cmd|com|msi|sh|ps1|php|jsp|asp|aspx)$" "id:100001,phase:2,deny,status:403,log,msg:\\\'Dangerous file upload blocked\\\'"\nSecAuditEngine Off\n';
	const site = render('values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], '', [valuesFile({ ingresses: [
		ingressEntry('api-integration', 'api-integration-service', 'api-integration-test', { annotations: { 'nginx.ingress.kubernetes.io/server-snippet': siteServer, 'nginx.ingress.kubernetes.io/configuration-snippet': siteLocation,
			'nginx.ingress.kubernetes.io/modsecurity-snippet': siteModsecurity } }),
		ingressEntry('api-integration-feeds', 'api-integration-service', 'api-integration-test', { paths: ['/api/v1/integration/'], annotations: { 'nginx.ingress.kubernetes.io/server-snippet': siteServer } })
	] })]);
	if (site.status !== 0) findings.push(`site snippets: template failed\n${site.stderr}`);
	else {
		const docs = parseAsKubernetes(site.stdout);
		findings.push(...checkManifests(parse(site.stdout), { environment: 'k8s-prd' }), ...ingressHardeningFindings(docs, { environment: 'k8s-prd' }), ...hubRefusalFindings(docs));
		// ARV-085: the probes answered 404 at the server level, after the HSTS value and the 421; ARV-091: then /hubs, as this host has no live hub.
		const chartServer = chartServerSnippet('api-integration-test.dalilhub.tech');
		const chartLocation = 'add_header Content-Security-Policy "frame-ancestors \'none\'" always;\nif ($scheme = http) {\n  return 403;\n}\n';
		for (const ingress of docs.filter((doc) => doc?.kind === 'Ingress')) {
			const annotations = ingress.metadata.annotations;
			if (annotations['nginx.ingress.kubernetes.io/server-snippet'] !== chartServer + siteServer) findings.push(`Ingress/${ingress.metadata.name}: the server-snippet is not the chart's followed by the site's`);
			const expectedLocation = chartLocation + (ingress.metadata.name === 'api-integration-ingress' ? siteLocation : '');
			if (annotations['nginx.ingress.kubernetes.io/configuration-snippet'] !== expectedLocation) findings.push(`Ingress/${ingress.metadata.name}: the configuration-snippet is not the chart's followed by the site's`);
			const expectedModsecurity = chartModsecurity + (ingress.metadata.name === 'api-integration-ingress' ? siteModsecurity : '');
			if (annotations['nginx.ingress.kubernetes.io/modsecurity-snippet'] !== expectedModsecurity) findings.push(`Ingress/${ingress.metadata.name}: the modsecurity-snippet is not the chart's (audit log off) followed by the site's`);
		}
	}
	const dev = render('values-k8s-dev.yaml', [], '', [valuesFile({ ingresses: [ingressEntry('api-ingest', 'api-ingest-service', 'api-ingest-test', { plainHttp: 'redirect' })] })]);
	if (dev.status !== 0) findings.push(`dev with a machine host that redirects: template failed\n${dev.stderr}`);
	else {
		const docs = parseAsKubernetes(dev.stdout);
		findings.push(...ingressHardeningFindings(docs, { environment: 'k8s-dev' }));
		if (modesOf(docs)['api-ingest-ingress'] !== 'redirect') findings.push('dev with a machine host that redirects: the Ingress does not redirect');
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL ingress site snippets and modes:\n  ${findings.join('\n  ')}`);
	} else {
		console.log('PASS ingress site snippets and modes: a site\'s server-snippet and configuration-snippet follow the chart\'s on every Ingress of a host and the rules still hold; a machine host may redirect outside production');
	}
}

// ARV-091: credentials out of the ingress's logs, on real renders. A host with the live hub (the web host: the app, /api and the
// hub's own Ingress) answers 404 to every spelling of /hubs but the lowercase one, which its hub Ingress serves; a host without it
// (api-main's own) answers 404 to /hubs in any case; both server-snippets are exactly the chart's text, and every Ingress keeps
// the ModSecurity audit log off. Then each change below to a shipped render gives exactly its finding: an Ingress without the
// audit log off, the audit log turned back on, a rule turning it on per request, a host without the hub not refusing /hubs or
// refusing it by case only, the hub's host refusing its own hub or leaving another spelling open.
{
	const findings = [];
	const hosts = render('values-k8s-prd.yaml', [PRD], '', [valuesFile({ ingresses: [
		ingressEntry('api-main', 'api-main-service', 'api-main-test'),
		ingressEntry('web', 'web-service', 'web-test', { plainHttp: 'redirect' }),
		ingressEntry('web-api', 'api-main-service', 'web-test', { plainHttp: 'redirect', paths: ['/api'] }),
		ingressEntry('web-hubs', 'api-main-service', 'web-test', { plainHttp: 'redirect', tokenInQuery: true, paths: ['/hubs'] })
	] })]);
	if (hosts.status !== 0) findings.push(`the live hub's host and api-main's: template failed\n${hosts.stderr}`);
	else {
		const docs = parseAsKubernetes(hosts.stdout);
		findings.push(...checkManifests(parse(hosts.stdout), { environment: 'k8s-prd' }), ...ingressHardeningFindings(docs, { environment: 'k8s-prd' }), ...hubRefusalFindings(docs));
		for (const ingress of docs.filter((doc) => doc?.kind === 'Ingress')) {
			const host = ingress.spec.rules[0].host;
			const hub = host.startsWith('web-test.');
			if (ingress.metadata.annotations['nginx.ingress.kubernetes.io/server-snippet'] !== chartServerSnippet(host, hub)) findings.push(`Ingress/${ingress.metadata.name}: the server-snippet is not the chart's for a host ${hub ? 'with' : 'without'} the live hub`);
		}
	}
	const MODSECURITY = 'nginx.ingress.kubernetes.io/modsecurity-snippet';
	const SERVER = 'nginx.ingress.kubernetes.io/server-snippet';
	const annotationsOf = (docs, name) => docs.find((doc) => doc?.kind === 'Ingress' && doc.metadata?.name === name).metadata.annotations;
	const shipped = {};
	const environmentOf = { 'values-k8s-dev.yaml': 'k8s-dev', 'values-k8s-demo.yaml': 'k8s-demo', 'values-localk8s.yaml': 'k8s-dev', 'values-k8s-prd.yaml': 'k8s-prd' };
	const cases = [
		['values-k8s-dev.yaml', 'api-main-ingress without SecAuditEngine Off', 'manifests', (docs) => { const a = annotationsOf(docs, 'api-main-ingress'); a[MODSECURITY] = a[MODSECURITY].replace('SecAuditEngine Off\n', ''); },
			['Ingress/api-main-ingress: the ingress must turn the ModSecurity audit log off (its modsecurity-snippet ends with SecAuditEngine Off, not unset)']],
		['values-k8s-prd.yaml', 'web-hubs-ingress with the audit log turned back on', 'manifests', (docs) => { annotationsOf(docs, 'web-hubs-ingress')[MODSECURITY] += 'SecAuditEngine RelevantOnly\n'; },
			['Ingress/web-hubs-ingress: the ingress must turn the ModSecurity audit log off (its modsecurity-snippet ends with SecAuditEngine Off, not Off then RelevantOnly)']],
		['values-k8s-demo.yaml', 'web-hubs-ingress with OpenTelemetry left to the controller', 'manifests', (docs) => { delete annotationsOf(docs, 'web-hubs-ingress')['nginx.ingress.kubernetes.io/enable-opentelemetry']; },
			['Ingress/web-hubs-ingress: /hubs carries tokens in its URL; the ingress must keep OpenTelemetry off']],
		['values-k8s-dev.yaml', 'api-main-ingress with a ModSecurity debug log', 'manifests', (docs) => { annotationsOf(docs, 'api-main-ingress')[MODSECURITY] += 'SecDebugLog /dev/stdout\nSecDebugLogLevel 9\n'; },
			['Ingress/api-main-ingress: the modsecurity-snippet sets a ModSecurity debug log']],
		['values-k8s-prd.yaml', 'web-api-ingress with a rule that turns the audit engine on', 'manifests',
			(docs) => { annotationsOf(docs, 'web-api-ingress')[MODSECURITY] += 'SecRule REQUEST_URI "@beginsWith /api/auth" "id:100100,phase:1,pass,nolog,ctl:auditEngine=RelevantOnly"\n'; },
			['Ingress/web-api-ingress: the ingress must turn the ModSecurity audit log off (its modsecurity-snippet ends with SecAuditEngine Off, not Off, and no rule sets ctl:auditEngine=RelevantOnly)']],
		['values-k8s-prd.yaml', 'the api-main host not refusing /hubs', 'hubs', (docs) => { const a = annotationsOf(docs, 'api-main-ingress'); a[SERVER] = a[SERVER].replace(HUB_REFUSAL, ''); },
			[`Ingress/api-main-ingress: server-snippet does not answer ${HUB_PATHS.all.join(', ')} with 404 at the server level`]],
		['values-k8s-dev.yaml', 'the api-main host refusing /hubs by case only', 'hubs', (docs) => { const a = annotationsOf(docs, 'api-main-ingress'); a[SERVER] = a[SERVER].replace('$uri ~* "^/hubs', '$uri ~ "^/hubs'); },
			[`Ingress/api-main-ingress: server-snippet does not answer ${HUB_PATHS.variants.join(', ')} with 404 at the server level (if`]],
		['values-k8s-demo.yaml', 'the ingest host not refusing /hubs', 'hubs', (docs) => { const a = annotationsOf(docs, 'api-ingest-ingress'); a[SERVER] = a[SERVER].replace(HUB_REFUSAL, ''); },
			[`Ingress/api-ingest-ingress: server-snippet does not answer ${HUB_PATHS.all.join(', ')} with 404 at the server level`]],
		['values-k8s-dev.yaml', 'the web host refusing its own hub', 'hubs', (docs) => { const a = annotationsOf(docs, 'web-hubs-ingress'); a[SERVER] = a[SERVER].replace(HUB_VARIANT_REFUSAL, HUB_REFUSAL); },
			[`Ingress/web-hubs-ingress: server-snippet refuses ${HUB_PATHS.served.join(', ')}, which the host's live hub serves`]],
		['values-localk8s.yaml', 'the web host leaving /HUBS to the web app', 'hubs', (docs) => { const a = annotationsOf(docs, 'web-ingress'); a[SERVER] = a[SERVER].replace(HUB_VARIANT_REFUSAL, ''); },
			[`Ingress/web-ingress: server-snippet does not answer ${HUB_PATHS.variants.join(', ')} with 404 at the server level: the host's live hub Ingress takes /hubs in lowercase only`]]
	];
	for (const [file, label, kind, change, expected] of cases) {
		if (!shipped[file]) {
			const result = render(file, file === 'values-k8s-prd.yaml' ? [PRD] : []);
			if (result.status !== 0) {
				findings.push(`${file}: template failed\n${result.stderr}`);
				continue;
			}
			shipped[file] = result.stdout;
		}
		const docs = kind === 'manifests' ? parse(shipped[file]) : parseAsKubernetes(shipped[file]);
		const before = kind === 'manifests' ? checkManifests(docs, { environment: environmentOf[file] }) : hubRefusalFindings(docs);
		if (before.length) findings.push(`${file} as shipped: ${before.join(' | ')}`);
		change(docs);
		const found = kind === 'manifests' ? checkManifests(docs, { environment: environmentOf[file] }) : hubRefusalFindings(docs);
		if (!exactly(found, expected)) findings.push(`${file} with ${label} must give exactly ${expected.join(' | ')}; gave ${found.length ? found.join(' | ') : 'nothing'}`);
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL credentials out of the ingress's logs:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS credentials out of the ingress's logs: the web host refuses every spelling of /hubs but its hub's and api-main's host refuses /hubs in any case, both exactly the chart's text, ` +
			`the audit log off on every Ingress; ${cases.length} changes to the shipped renders each give exactly their finding`);
	}
}

// ARV-085: the NetworkPolicies with every flow the firewall matrix allows open (production, tests/fixtures/network-policy-values.yaml);
// dev with them on, where the simulator's flows join; a site's trusted proxy networks reaching the five API hosts in order,
// IPv6 included; and none of it in dev by default (the environment renders above check that too).
{
	const findings = [];
	const counts = [];
	const full = path.join(here, 'fixtures', 'network-policy-values.yaml');
	for (const [label, file, sets, extra, environment] of [
		['production with every flow', 'values-k8s-prd.yaml', [PRD], [full], 'k8s-prd'],
		// ARV-092c: dev's freshclam updates from the ClamAV mirrors, whose peers the policies then need.
		['dev with the network policies and the simulator', 'values-k8s-dev.yaml', [], [valuesFile({ networkPolicies: { enabled: true, clamavMirror: { to: [{ ipBlock: { cidr: '104.16.0.0/13' } }] } } })], 'k8s-dev'],
		['dev with trusted proxies', 'values-k8s-dev.yaml', [], [valuesFile({ ingressController: { podNetworks: ['10.42.0.0/16', 'fd00:42::/56'] } })], 'k8s-dev']
	]) {
		const result = render(file, sets, '', extra);
		if (result.status !== 0) {
			findings.push(`${label}: template failed\n${result.stderr}`);
			continue;
		}
		const values = mergedValues([file, ...extra]);
		const found = [...checkManifests(parse(result.stdout), { environment }), ...networkFindings(result.stdout, values, environment)];
		findings.push(...found.map((finding) => `${label}: ${finding}`));
		counts.push(`${label}: ${describeNetwork(result.stdout, values)}`);
		const policies = parseAsKubernetes(result.stdout).filter((doc) => doc?.kind === 'NetworkPolicy').map((doc) => doc.metadata.name);
		if (label.startsWith('dev with the network') && !['simulation-network', 'ariva-hosts-from-simulation', 'api-integration-to-simulation', 'ariva-clamav-mirror-egress'].every((name) => policies.includes(name))) {
			findings.push(`${label}: the simulator's policies are not rendered (${policies.join(', ')})`);
		}
		if (label.startsWith('production') && !['api-ingest-from-mqtt-sensors', 'ariva-smtp-egress', 'api-integration-outbound-egress', 'ariva-otlp-egress', 'ariva-loki-egress', 'ariva-clamav-mirror-egress'].every((name) => policies.includes(name))) {
			findings.push(`${label}: a flow's policy is not rendered (${policies.join(', ')})`);
		}
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL network policies and trusted proxies:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS network policies and trusted proxies: ${counts.join('; ')}; every Ariva pod denied by default and given exactly the firewall matrix's flows`);
	}
}

// ARV-085: the hosts' rule for IPv6 trusted proxy networks (ForwardedHeadersExtensions.TrustsMappedClients, mirrored by
// trustsMappedClients in checks.mjs) and the chart's (ariva-platform.mappedClientsRefusal in validate.yaml) agree on every prefix
// from /48 to /128 of six base networks, written in full and with ::, and on IPv4 networks; and production renders the IPv6
// prefixes the hosts take at the edges of the refused ranges, giving them to the five API hosts in order.
{
	const findings = [];
	const groupsOf = (bits) => Array.from({ length: 8 }, (_, index) => Number((bits >> BigInt(112 - 16 * index)) & 0xffffn).toString(16));
	const compressed = (bits) => {
		const groups = groupsOf(bits);
		let best = [-1, 0];
		for (let index = 0; index < 8;) {
			if (groups[index] !== '0') { index += 1; continue; }
			let end = index;
			while (end < 8 && groups[end] === '0') end += 1;
			if (end - index > best[1]) best = [index, end - index];
			index = end;
		}
		return best[1] ? `${groups.slice(0, best[0]).join(':')}::${groups.slice(best[0] + best[1]).join(':')}` : groups.join(':');
	};
	const all = (1n << 128n) - 1n;
	const bases = [0n, 0xfd00n << 112n, 0xffffn << 32n, (0xfd00n << 112n) | 0x0a000000n, (0xffffn << 32n) | 0x0a000000n, (0x20010db8n << 96n) | (0x40n << 80n)];
	const cases = new Set(['10.244.0.0/16', '10.0.0.0/8', '192.168.0.0/24']);
	for (const base of bases) {
		for (let prefix = 48; prefix <= 128; prefix += 1) {
			const bits = base & (all ^ ((1n << BigInt(128 - prefix)) - 1n));
			cases.add(`${groupsOf(bits).join(':')}/${prefix}`);
			cases.add(`${compressed(bits)}/${prefix}`);
		}
	}
	const list = [...cases];
	const copy = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-chart-mapped-'));
	try {
		fs.cpSync(chart, path.join(copy, 'platform'), { recursive: true });
		fs.writeFileSync(path.join(copy, 'platform', 'templates', 'zz-mapped-clients-test.yaml'), 'apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: mapped-clients-test\ndata:\n' +
			'{{- range $index, $cidr := .Values.mappedCases }}\n  {{ printf "c%d" $index | quote }}: {{ include "ariva-platform.mappedClientsRefusal" $cidr | quote }}\n{{- end }}\n');
		fs.writeFileSync(path.join(copy, 'cases.json'), JSON.stringify({ mappedCases: list }));
		const result = spawnSync(helm, ['template', 'ariva-platform', path.join(copy, 'platform'), '-f', path.join(chart, 'values-k8s-prd.yaml'), '-f', PRD_SITE, '-f', path.join(copy, 'cases.json'),
			'--set', PRD, '--show-only', 'templates/zz-mapped-clients-test.yaml'], { encoding: 'utf8', shell: false, maxBuffer: 16 * 1024 * 1024 });
		if (result.status !== 0) findings.push(`the mapped client cases: template failed\n${result.stderr}`);
		else {
			const judged = parse(result.stdout)[0]?.data ?? {};
			list.forEach((cidr, index) => {
				const chartRefuses = Boolean(judged[`c${index}`]);
				if (chartRefuses !== trustsMappedClients(cidr)) findings.push(`${cidr}: the chart ${chartRefuses ? 'refuses' : 'takes'} it, the hosts ${chartRefuses ? 'take' : 'refuse'} it`);
			});
		}
	} finally {
		fs.rmSync(copy, { recursive: true, force: true });
	}
	const accepted = render('values-k8s-prd.yaml', [PRD], '', [valuesFile({ ingressController: { podNetworks: ACCEPTED_IPV6 } })]);
	if (accepted.status !== 0) findings.push(`production with the IPv6 prefixes the hosts take: template failed\n${accepted.stderr}`);
	else findings.push(...networkFindings(accepted.stdout, mergedValues(['values-k8s-prd.yaml', { ingressController: { podNetworks: ACCEPTED_IPV6 } }]), 'k8s-prd'));
	if (render('values-k8s-dev.yaml', [], '', [valuesFile({ ingressController: { podNetworks: ['fd00::/104'] } })]).status !== 0) findings.push('dev refused fd00::/104; the rule is production\'s, as the hosts\'');
	if (findings.length) {
		failed = true;
		console.error(`FAIL trusted proxy networks as the hosts judge them:\n  ${findings.slice(0, 40).join('\n  ')}`);
	} else {
		console.log(`PASS trusted proxy networks as the hosts judge them: the chart and the hosts agree on ${list.length} networks (${list.filter(trustsMappedClients).length} refused as trusting IPv4 clients); production renders ${ACCEPTED_IPV6.join(', ')} to the five API hosts; dev takes fd00::/104`);
	}
}

// ARV-082b: the cluster CA guard. validate.yaml fails on ariva-platform.podTlsCaRefusal of the lookup of podTls.caSecretName
// in the release's namespace. helm template has no cluster, so lookup returns nothing there and the guard passes (as it does
// before the secret exists); the refusal itself cannot run here, and no value turns the lookup off. Instead: the guard's
// wiring is read from validate.yaml, and the helper is rendered, in a copy of the chart with one test template, against
// stand-in secrets shaped as the API returns them (data base64).
{
	const findings = [];
	const validate = fs.readFileSync(path.join(chart, 'templates', 'validate.yaml'), 'utf8');
	if (!/\{\{- \$caRefusal := include "ariva-platform\.podTlsCaRefusal" \(lookup "v1" "Secret" \.Release\.Namespace \(toString \(\$podTls\.caSecretName \| default "ariva-cluster-ca"\)\)\) \}\}\n\{\{- if \$caRefusal \}\}\n\{\{- fail \$caRefusal \}\}/.test(validate)) {
		findings.push('validate.yaml does not fail on ariva-platform.podTlsCaRefusal of the lookup of podTls.caSecretName in the release namespace');
	}
	const b64 = (text) => Buffer.from(text).toString('base64');
	const pem = '-----BEGIN CERTIFICATE-----\nMIIBstandin\n-----END CERTIFICATE-----\n';
	const without = 'without ca.crt: the ingress would proxy to the pods without verifying them';
	const cases = {
		'no-secret': [{}, ''],
		'ca-only': [{ type: 'Opaque', data: { 'ca.crt': b64(pem) } }, ''],
		'ca-and-certificate': [{ type: 'Opaque', data: { 'ca.crt': b64(pem), 'tls.crt': b64(pem) } }, ''],
		'tls-without-ca': [{ type: 'kubernetes.io/tls', data: { 'tls.crt': b64(pem), 'tls.key': b64('key') } }, without],
		'no-data': [{ type: 'Opaque' }, without],
		'empty-ca': [{ type: 'Opaque', data: { 'ca.crt': '' } }, without],
		'blank-ca': [{ type: 'Opaque', data: { 'ca.crt': b64(' \n') } }, without],
		'ca-not-pem': [{ type: 'Opaque', data: { 'ca.crt': b64('not a certificate') } }, without],
		'ca-not-base64': [{ type: 'Opaque', data: { 'ca.crt': '%%%' } }, without],
		'tls-with-ca': [{ type: 'kubernetes.io/tls', data: { 'ca.crt': b64(pem), 'tls.crt': b64(pem), 'tls.key': b64('key') } }, 'also holds tls.key']
	};
	const copy = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-chart-ca-'));
	try {
		fs.cpSync(chart, path.join(copy, 'platform'), { recursive: true });
		fs.writeFileSync(path.join(copy, 'platform', 'templates', 'zz-ca-refusal-test.yaml'), 'apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: ca-refusal-test\ndata:\n' +
			'{{- range $name, $secret := .Values.caRefusalCases }}\n  {{ $name | quote }}: {{ include "ariva-platform.podTlsCaRefusal" $secret | quote }}\n{{- end }}\n');
		fs.writeFileSync(path.join(copy, 'cases.json'), JSON.stringify({ caRefusalCases: Object.fromEntries(Object.entries(cases).map(([name, [secret]]) => [name, secret])) }));
		const result = spawnSync(helm, ['template', 'ariva-platform', path.join(copy, 'platform'), '-f', path.join(chart, 'values-k8s-prd.yaml'), '-f', PRD_SITE, '-f', path.join(copy, 'cases.json'),
			'--set', 'buildNumber=main-20261001.1', '--show-only', 'templates/zz-ca-refusal-test.yaml'], { encoding: 'utf8', shell: false });
		if (result.status !== 0) findings.push(`the CA refusal cases: template failed\n${result.stderr}`);
		else {
			const judged = parse(result.stdout)[0]?.data ?? {};
			for (const [name, [, expected]] of Object.entries(cases)) {
				const message = judged[name] ?? '(not rendered)';
				if (expected ? !message.includes(expected) : message !== '') findings.push(`CA secret ${name}: judged "${message}", expected ${expected ? `"${expected}"` : 'no refusal'}`);
			}
		}
	} finally {
		fs.rmSync(copy, { recursive: true, force: true });
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL pod TLS cluster CA guard:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS pod TLS cluster CA guard: validate.yaml fails on the lookup of podTls.caSecretName; ${Object.keys(cases).length} stand-in secrets judged ` +
			'(no ca.crt, an empty, blank, non-PEM or non-base64 ca.crt and a tls.key refused; ca.crt alone or with tls.crt passes; no secret passes, as under helm template, which cannot run lookup)');
	}
}

// ARV-091a: log shipping on real renders. Production as shipped sends the logs to Loki (Loki__Enabled on the five API hosts, its
// placeholder peers opened) with the log pseudonym key from ariva-log-pseudonym-key; production with the OTLP collector and a
// site's Secret and key; dev opting in to Loki with the key; dev as shipped with neither. Each holds every rule.
{
	const findings = [];
	const counts = [];
	const site = { name: 'site-log-key', key: 'master' };
	for (const [label, file, sets, extra, environment, policy] of [
		['production as shipped', 'values-k8s-prd.yaml', [PRD], [], 'k8s-prd', 'ariva-loki-egress'],
		['production logging to the OTLP collector with a site Secret and key', 'values-k8s-prd.yaml', [PRD],
			[{ otel: { endpoint: 'https://otel.example:4317' }, logging: { collector: 'otlp', pseudonymKeySecret: site }, networkPolicies: { otlp: { to: [{ ipBlock: { cidr: '10.60.0.0/24' } }] } } }], 'k8s-prd', 'ariva-otlp-egress'],
		['production logging to Loki with its credentials', 'values-k8s-prd.yaml', [PRD], [{ logging: { lokiCredentialsSecret: { name: 'site-loki-credentials', passwordKey: 'token' } } }], 'k8s-prd', 'ariva-loki-egress'],
		['production exporting to an OTLP collector that authenticates senders', 'values-k8s-prd.yaml', [PRD],
			[{ otel: { endpoint: 'https://otel.example:4317', headersSecret: { name: 'site-otlp-headers', key: 'x-api-key' } }, logging: { collector: 'otlp' }, networkPolicies: { otlp: { to: [{ ipBlock: { cidr: '10.60.0.0/24' } }] } } }], 'k8s-prd', 'ariva-otlp-egress'],
		['dev with the simulator exporting to an OTLP collector that authenticates senders', 'values-k8s-dev.yaml', [], [{ otel: { endpoint: 'https://otel.example:4317', headersSecret: { name: 'site-otlp-headers' } } }], 'k8s-dev', ''],
		['dev logging to Loki with the key', 'values-k8s-dev.yaml', [], [{ logging: { collector: 'loki', pseudonymKeySecret: { name: LOG_SECRET.name } } }], 'k8s-dev', ''],
		['dev as shipped', 'values-k8s-dev.yaml', [], [], 'k8s-dev', '']
	]) {
		const result = render(file, sets, '', extra.map((values) => valuesFile(values)));
		if (result.status !== 0) {
			findings.push(`${label}: template failed\n${result.stderr}`);
			continue;
		}
		const values = mergedValues([file, ...extra]);
		const docs = parseAsKubernetes(result.stdout);
		const found = [...checkManifests(parse(result.stdout), { environment }), ...networkFindings(result.stdout, values, environment), ...logShippingFindings(docs, logShippingOptions(values, environment)),
			...collectorCaFindings(docs, collectorCaOptions(values))];
		if (policy && !docs.some((doc) => doc?.kind === 'NetworkPolicy' && doc.metadata?.name === policy)) found.push(`the collector's NetworkPolicy ${policy} is not rendered`);
		findings.push(...found.map((finding) => `${label}: ${finding}`));
		counts.push(`${label}: ${describeLogShipping(values)}`);
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL log shipping:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS log shipping: ${counts.join('; ')}; nothing reaches another workload, a ConfigMap or a Secret of the chart`);
	}
}

// ARV-091a: the log pseudonym key's Secret guard. validate.yaml fails on ariva-platform.pseudonymKeyRefusal of the lookups of the
// Secret and of the namespace's kube-root-ca.crt ConfigMap (which says the release sees the cluster). helm template has no
// cluster, so both lookups return nothing and the guard passes, as on a first install into a new namespace. As for the cluster CA:
// the wiring is read from validate.yaml, and the helper is rendered in a copy of the chart against stand-in secrets shaped as the
// API returns them (data base64), the hosts' rule for the value (LogProtection.From) included; no message may hold a value.
{
	const findings = [];
	const validate = fs.readFileSync(path.join(chart, 'templates', 'validate.yaml'), 'utf8');
	if (!/\{\{- if \$keySecretName \}\}\n\{\{- \$reachable := lookup "v1" "ConfigMap" \.Release\.Namespace "kube-root-ca\.crt" \}\}\n\{\{- \$keyRefusal := include "ariva-platform\.pseudonymKeyRefusal" \(dict "secret" \(lookup "v1" "Secret" \.Release\.Namespace \$keySecretName\) "name" \$keySecretName "key" \$keySecretKey "reachable" \(not \(empty \$reachable\)\)\) \}\}\n\{\{- if \$keyRefusal \}\}\n\{\{- fail \$keyRefusal \}\}/.test(validate)) {
		findings.push('validate.yaml does not fail on ariva-platform.pseudonymKeyRefusal of the lookups of logging.pseudonymKeySecret and kube-root-ca.crt in the release namespace');
	}
	const b64 = (text) => Buffer.from(text).toString('base64');
	const keyText = (bytes, fill = 7) => Buffer.alloc(bytes, fill).toString('base64');
	const wrapped = keyText(64, 9).replace(/(.{64})/g, '$1\n') + '\n';
	const missing = "which the release's namespace does not hold";
	const noKey = 'has no key pseudonym-key';
	const malformed = 'no base64 of 32 or more random bytes';
	const secret = (data, type = 'Opaque') => ({ type, data });
	const cases = {
		'offline': [{ secret: {}, reachable: false }, ''],
		'missing': [{ secret: {}, reachable: true }, missing],
		'no-data': [{ secret: { type: 'Opaque' }, reachable: true }, noKey],
		'other-key': [{ secret: secret({ password: b64(keyText(32)) }), reachable: true }, noKey],
		'tls-secret': [{ secret: secret({ 'tls.crt': b64('x'), 'tls.key': b64('y') }, 'kubernetes.io/tls'), reachable: true }, noKey],
		'empty': [{ secret: secret({ 'pseudonym-key': '' }), reachable: true }, malformed],
		'blank': [{ secret: secret({ 'pseudonym-key': b64(' \n') }), reachable: true }, malformed],
		'not-base64': [{ secret: secret({ 'pseudonym-key': b64('not a key at all, but long enough to be one') }), reachable: true }, malformed],
		'base64url': [{ secret: secret({ 'pseudonym-key': b64(keyText(32, 0xfb).replaceAll('+', '-').replaceAll('/', '_')) }), reachable: true }, malformed],
		'unpadded': [{ secret: secret({ 'pseudonym-key': b64(keyText(32).replace(/=+$/, '')) }), reachable: true }, malformed],
		'short': [{ secret: secret({ 'pseudonym-key': b64(keyText(31)) }), reachable: true }, malformed],
		'raw-bytes': [{ secret: secret({ 'pseudonym-key': Buffer.alloc(32, 0xfb).toString('base64') }), reachable: true }, malformed],
		'data-not-base64': [{ secret: secret({ 'pseudonym-key': '%%%' }), reachable: true }, malformed],
		'exact': [{ secret: secret({ 'pseudonym-key': b64(keyText(32)) }), reachable: true }, ''],
		'with-newline': [{ secret: secret({ 'pseudonym-key': b64(`${keyText(32)}\n`) }), reachable: true }, ''],
		'wrapped': [{ secret: secret({ 'pseudonym-key': b64(wrapped) }), reachable: true }, ''],
		'site-key': [{ secret: secret({ master: b64(keyText(48)) }), reachable: true, key: 'master' }, ''],
		'site-key-missing': [{ secret: secret({ 'pseudonym-key': b64(keyText(48)) }), reachable: true, key: 'master' }, 'has no key master']
	};
	const copy = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-chart-log-key-'));
	try {
		fs.cpSync(chart, path.join(copy, 'platform'), { recursive: true });
		fs.writeFileSync(path.join(copy, 'platform', 'templates', 'zz-log-key-refusal-test.yaml'), 'apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: log-key-refusal-test\ndata:\n' +
			'{{- range $name, $case := .Values.logKeyCases }}\n  {{ $name | quote }}: {{ include "ariva-platform.pseudonymKeyRefusal" (dict "secret" $case.secret "name" "ariva-log-pseudonym-key" "key" ($case.key | default "pseudonym-key") "reachable" $case.reachable) | quote }}\n{{- end }}\n');
		fs.writeFileSync(path.join(copy, 'cases.json'), JSON.stringify({ logKeyCases: Object.fromEntries(Object.entries(cases).map(([name, [input]]) => [name, input])) }));
		const result = spawnSync(helm, ['template', 'ariva-platform', path.join(copy, 'platform'), '-f', path.join(chart, 'values-k8s-prd.yaml'), '-f', PRD_SITE, '-f', path.join(copy, 'cases.json'),
			'--set', PRD, '--show-only', 'templates/zz-log-key-refusal-test.yaml'], { encoding: 'utf8', shell: false });
		if (result.status !== 0) findings.push(`the log key refusal cases: template failed\n${result.stderr}`);
		else {
			const judged = parse(result.stdout)[0]?.data ?? {};
			const values = [keyText(32), keyText(31), keyText(48), keyText(64, 9).slice(0, 40), keyText(32, 0xfb)];
			for (const [name, [, expected]] of Object.entries(cases)) {
				const message = judged[name] ?? '(not rendered)';
				if (expected ? !message.includes(expected) : message !== '') findings.push(`log key Secret ${name}: judged "${message}", expected ${expected ? `"${expected}"` : 'no refusal'}`);
				if (values.some((value) => message.includes(value.slice(0, 16)))) findings.push(`log key Secret ${name}: the refusal repeats the value`);
			}
		}
	} finally {
		fs.rmSync(copy, { recursive: true, force: true });
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL log pseudonym key Secret guard:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS log pseudonym key Secret guard: validate.yaml fails on the lookups of logging.pseudonymKeySecret and kube-root-ca.crt; ${Object.keys(cases).length} stand-in secrets judged ` +
			'(a reachable namespace without the Secret, a Secret without the key, an empty, blank, non-base64, base64url, unpadded, raw or 31-byte value refused; 32 bytes, a trailing newline, ' +
			"openssl's wrapped lines and a site's own key pass; nothing seen offline passes, as under helm template); no refusal repeats a value");
	}
}

// ARV-200: the collectors' CAs on real renders. dev with both (the simulator gets the OTLP collector's alone), dev with each
// alone (the OTLP collector over http/protobuf), production with both (no simulator there), and production with one site CA
// for both collectors. Each holds every rule, and the CAs reach exactly their workloads; the shipped files (above) mount neither.
{
	const findings = [];
	const counts = [];
	const otlpPeers = { networkPolicies: { otlp: { to: [{ ipBlock: { cidr: '10.60.0.0/24' } }] } } };
	const apiHosts = API_HOSTS.map((host) => `Deployment/${host}-deployment`);
	for (const [label, file, sets, extra, environment, reach] of [
		['dev with both collectors\' CAs', 'values-k8s-dev.yaml', [], [{ otel: { endpoint: 'https://otel.example:4317', caSecretName: OTLP_CA }, logging: { collector: 'loki', lokiCaSecretName: LOKI_CA } }], 'k8s-dev',
			{ 'otlp-ca': [...apiHosts, 'Deployment/simulation-deployment'], 'loki-ca': apiHosts }],
		['dev with the OTLP collector\'s CA alone, over http/protobuf', 'values-k8s-dev.yaml', [], [{ otel: { endpoint: 'https://otel.example:4318', protocol: 'http/protobuf', caSecretName: OTLP_CA } }], 'k8s-dev',
			{ 'otlp-ca': [...apiHosts, 'Deployment/simulation-deployment'], 'loki-ca': [] }],
		['demo with Loki\'s CA alone', 'values-k8s-demo.yaml', [], [{ logging: { collector: 'loki', lokiCaSecretName: LOKI_CA } }], 'k8s-demo', { 'otlp-ca': [], 'loki-ca': apiHosts }],
		['production with both collectors\' CAs', 'values-k8s-prd.yaml', [PRD], [{ otel: { endpoint: 'https://otel.example:4317', caSecretName: OTLP_CA }, logging: { lokiCaSecretName: LOKI_CA }, ...otlpPeers }], 'k8s-prd',
			{ 'otlp-ca': apiHosts, 'loki-ca': apiHosts }],
		['production with one site CA for both collectors', 'values-k8s-prd.yaml', [PRD], [{ otel: { endpoint: 'https://otel.example:4317', caSecretName: 'site-ca' }, logging: { lokiCaSecretName: 'site-ca' }, ...otlpPeers }], 'k8s-prd',
			{ 'otlp-ca': apiHosts, 'loki-ca': apiHosts }]
	]) {
		const result = render(file, sets, '', extra.map((values) => valuesFile(values)));
		if (result.status !== 0) {
			findings.push(`${label}: template failed\n${result.stderr}`);
			continue;
		}
		const values = mergedValues([file, ...extra]);
		const docs = parseAsKubernetes(result.stdout);
		const found = [...checkManifests(parse(result.stdout), { environment }), ...networkFindings(result.stdout, values, environment), ...logShippingFindings(docs, logShippingOptions(values, environment)),
			...collectorCaFindings(docs, collectorCaOptions(values)), ...serverCaReach(docs)];
		const reached = collectorCaReach(docs);
		for (const [volume, expected] of Object.entries(reach)) {
			if (JSON.stringify(reached[volume]) !== JSON.stringify([...expected].sort())) found.push(`${volume} reaches ${reached[volume].join(', ') || 'no workload'}, not ${expected.join(', ') || 'none'}`);
		}
		findings.push(...found.map((finding) => `${label}: ${finding}`));
		counts.push(`${label}: ${describeCollectorCas(values)}`);
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL collectors' CAs:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS collectors' CAs: ${counts.join('; ')}; each CA's ca.crt alone, read-only, mode 0444, not optional, with its variable in exactly those workloads, never in the web pod or the jobs`);
	}
}

// ARV-200: the collectors' CA Secret guard. validate.yaml fails on ariva-platform.collectorCaRefusal of the lookups of each named
// Secret and of the namespace's kube-root-ca.crt ConfigMap (which says the release sees the cluster). helm template has no
// cluster, so both lookups return nothing and the guard passes, as on a first install into a new namespace (the pods then wait
// for the Secret). As for the cluster CA and the log key: the wiring is read from validate.yaml, and the helper is rendered in a
// copy of the chart against stand-in secrets shaped as the API returns them (data base64); no message may hold the file.
{
	const findings = [];
	const validate = fs.readFileSync(path.join(chart, 'templates', 'validate.yaml'), 'utf8');
	if (!validate.includes('{{- $otelCaSecretName := toString ($otel.caSecretName | default "") }}\n') || !validate.includes('{{- $lokiCaSecretName := toString ($logging.lokiCaSecretName | default "") }}\n')) {
		findings.push('validate.yaml does not read otel.caSecretName and logging.lokiCaSecretName into the names the guard judges');
	}
	if (!/\{\{- range \$ca := list \(dict "value" "otel\.caSecretName" "name" \$otelCaSecretName "collector" "the OTLP collector"\) \(dict "value" "logging\.lokiCaSecretName" "name" \$lokiCaSecretName "collector" "Loki"\) \}\}\n\{\{- if \$ca\.name \}\}\n\{\{- \$caReachable := lookup "v1" "ConfigMap" \$\.Release\.Namespace "kube-root-ca\.crt" \}\}\n\{\{- \$collectorCaRefusal := include "ariva-platform\.collectorCaRefusal" \(dict "secret" \(lookup "v1" "Secret" \$\.Release\.Namespace \$ca\.name\) "name" \$ca\.name "value" \$ca\.value "collector" \$ca\.collector "reachable" \(not \(empty \$caReachable\)\)\) \}\}\n\{\{- if \$collectorCaRefusal \}\}\n\{\{- fail \$collectorCaRefusal \}\}/.test(validate)) {
		findings.push('validate.yaml does not fail on ariva-platform.collectorCaRefusal of the lookups of otel.caSecretName, logging.lokiCaSecretName and kube-root-ca.crt in the release namespace');
	}
	const b64 = (text) => Buffer.from(text).toString('base64');
	const certificate = (body) => `-----BEGIN CERTIFICATE-----\n${body}\n-----END CERTIFICATE-----\n`;
	const pem = certificate('MIIBstandinCA');
	const key = (kind) => `-----BEGIN ${kind}-----\nMIIEstandinKEY\n-----END ${kind}-----\n`;
	const missing = "which the release's namespace does not hold";
	const noPem = 'holds no PEM certificate under ca.crt';
	const privateKey = 'holds a private key under ca.crt';
	const others = (keys) => `holds keys other than ca.crt${keys ? ` (${keys})` : ''}; a collector\'s CA Secret holds ca.crt and nothing else`;
	const der = (bytes) => Buffer.from(bytes).toString('base64');
	const secret = (data, type = 'Opaque') => ({ type, data });
	const loki = { name: LOKI_CA, value: 'logging.lokiCaSecretName', collector: 'Loki' };
	const cases = {
		'offline': [{ secret: {}, reachable: false }, ''],
		'missing': [{ secret: {}, reachable: true }, `otel.caSecretName names the Secret ${OTLP_CA}, ${missing}`],
		'missing-loki': [{ ...loki, secret: {}, reachable: true }, `logging.lokiCaSecretName names the Secret ${LOKI_CA}, ${missing}: create it before the release with the key ca.crt, the PEM CA Loki's certificate chains to`],
		'ca-only': [{ secret: secret({ 'ca.crt': b64(pem) }), reachable: true }, ''],
		'bundle': [{ secret: secret({ 'ca.crt': b64(pem + certificate('MIIBstandinSECOND')) }), reachable: true }, ''],
		// The Secret holds ca.crt and nothing else: a certificate, a DER key, a keystore or any other data beside it is refused.
		'ca-and-certificate': [{ secret: secret({ 'ca.crt': b64(pem), 'tls.crt': b64(pem) }), reachable: true }, `otel.caSecretName: the Secret ${OTLP_CA} ${others('tls.crt')}`],
		'ca-and-der-key': [{ secret: secret({ 'ca.crt': b64(pem), 'ca.key': der([0x30, 0x82, 0x04, 0xa4, 0x02, 0x01, 0x00, 0x02, 0x82, 0x01, 0x01]) }), reachable: true }, others('ca.key')],
		'ca-and-keystore': [{ ...loki, secret: secret({ 'ca.crt': b64(pem), 'keystore.p12': der([0x30, 0x82, 0x0a, 0x1d, 0x02, 0x01, 0x03, 0x30, 0x82, 0x09, 0xe3]) }), reachable: true }, `logging.lokiCaSecretName: the Secret ${LOKI_CA} ${others('keystore.p12')}`],
		'ca-and-other-data': [{ secret: secret({ 'ca.crt': b64(pem), notes: '%%%' }), reachable: true }, others('notes')],
		// The collector's own cert-manager Certificate Secret: ca.crt with the collector's tls.crt and tls.key.
		'cert-manager': [{ secret: secret({ 'ca.crt': b64(pem), 'tls.crt': b64(pem), 'tls.key': b64(key('PRIVATE KEY')) }, 'kubernetes.io/tls'), reachable: true }, others('tls.crt, tls.key')],
		'tls-key-empty': [{ secret: secret({ 'ca.crt': b64(pem), 'tls.key': '' }), reachable: true }, others('tls.key')],
		'other-key-no-ca': [{ secret: secret({ 'key.pem': b64(key('PRIVATE KEY')) }), reachable: true }, others('key.pem')],
		// A key that is not a Kubernetes key name (the API refuses one; a stand-in here) is not named.
		'unnamed-key': [{ secret: secret({ 'ca.crt': b64(pem), 'a key': b64('stand-in') }), reachable: true }, others('')],
		'no-data': [{ secret: { type: 'Opaque' }, reachable: true }, noPem],
		'empty-ca': [{ secret: secret({ 'ca.crt': '' }), reachable: true }, noPem],
		'blank-ca': [{ secret: secret({ 'ca.crt': b64(' \n') }), reachable: true }, noPem],
		'ca-not-pem': [{ secret: secret({ 'ca.crt': b64('MIIBstandinDER') }), reachable: true }, noPem],
		'ca-not-base64': [{ secret: secret({ 'ca.crt': '%%%' }), reachable: true }, noPem],
		'loki-not-pem': [{ ...loki, secret: secret({ 'ca.crt': b64('not a certificate') }), reachable: true }, `logging.lokiCaSecretName: the Secret ${LOKI_CA} ${noPem}`],
		'ca-with-key': [{ secret: secret({ 'ca.crt': b64(pem + key('PRIVATE KEY')) }), reachable: true }, privateKey],
		'ca-with-key-and-tls-key': [{ secret: secret({ 'ca.crt': b64(pem + key('PRIVATE KEY')), 'tls.key': b64(key('PRIVATE KEY')) }), reachable: true }, privateKey],
		'ca-with-rsa-key': [{ secret: secret({ 'ca.crt': b64(pem + key('RSA PRIVATE KEY')) }), reachable: true }, privateKey],
		'ca-with-ec-key': [{ ...loki, secret: secret({ 'ca.crt': b64(key('EC PRIVATE KEY') + pem) }), reachable: true }, `logging.lokiCaSecretName: the Secret ${LOKI_CA} ${privateKey}`],
		'ca-with-encrypted-key': [{ secret: secret({ 'ca.crt': b64(pem + key('ENCRYPTED PRIVATE KEY')) }), reachable: true }, privateKey],
		'key-only': [{ secret: secret({ 'ca.crt': b64(key('PRIVATE KEY')) }), reachable: true }, privateKey]
	};
	const copy = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-chart-collector-ca-'));
	try {
		fs.cpSync(chart, path.join(copy, 'platform'), { recursive: true });
		fs.writeFileSync(path.join(copy, 'platform', 'templates', 'zz-collector-ca-refusal-test.yaml'), 'apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: collector-ca-refusal-test\ndata:\n' +
			'{{- range $name, $case := .Values.collectorCaCases }}\n  {{ $name | quote }}: {{ include "ariva-platform.collectorCaRefusal" (dict "secret" $case.secret "name" ($case.name | default "site-otlp-ca") ' +
			'"value" ($case.value | default "otel.caSecretName") "collector" ($case.collector | default "the OTLP collector") "reachable" $case.reachable) | quote }}\n{{- end }}\n');
		fs.writeFileSync(path.join(copy, 'cases.json'), JSON.stringify({ collectorCaCases: Object.fromEntries(Object.entries(cases).map(([name, [input]]) => [name, input])) }));
		const result = spawnSync(helm, ['template', 'ariva-platform', path.join(copy, 'platform'), '-f', path.join(chart, 'values-k8s-prd.yaml'), '-f', PRD_SITE, '-f', path.join(copy, 'cases.json'),
			'--set', PRD, '--show-only', 'templates/zz-collector-ca-refusal-test.yaml'], { encoding: 'utf8', shell: false });
		if (result.status !== 0) findings.push(`the collector CA refusal cases: template failed\n${result.stderr}`);
		else {
			const judged = parse(result.stdout)[0]?.data ?? {};
			for (const [name, [, expected]] of Object.entries(cases)) {
				const message = judged[name] ?? '(not rendered)';
				if (expected ? !message.includes(expected) : message !== '') findings.push(`collector CA Secret ${name}: judged "${message}", expected ${expected ? `"${expected}"` : 'no refusal'}`);
				if (/standin|stand-in|BEGIN|%%%/.test(message)) findings.push(`collector CA Secret ${name}: the refusal repeats a value of the Secret`);
			}
		}
	} finally {
		fs.rmSync(copy, { recursive: true, force: true });
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL collectors' CA Secret guard:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS collectors' CA Secret guard: validate.yaml fails on the lookups of otel.caSecretName, logging.lokiCaSecretName and kube-root-ca.crt; ${Object.keys(cases).length} stand-in secrets judged ` +
			'(a reachable namespace without the Secret, no ca.crt, an empty, blank, non-PEM or non-base64 ca.crt, a ca.crt holding a private key of any kind, and any key beside ca.crt ' +
			"(a certificate, a DER key, a PKCS#12 keystore, the collector's cert-manager Secret, other data) refused, a key that is not a Kubernetes key name left unnamed; " +
			"a CA and a bundle alone pass; nothing seen offline passes, as under helm template); no refusal repeats the Secret's values");
	}
}

// ARV-125b: the AMAN feed rule renders where it is turned on (border deployments), channels reach every threshold, and
// monitoring off renders no monitoring object.
{
	const findings = [];
	const aman = render('values-k8s-dev.yaml', ['monitoring.alerts.amanFeedStale.enabled=true', 'monitoring.channels={ops-pager,site it}']);
	if (aman.status !== 0) findings.push(`the AMAN feed rule turned on: template failed\n${aman.stderr}`);
	else {
		const docs = parse(aman.stdout);
		findings.push(...checkManifests(docs, { environment: 'k8s-dev' }), ...missingMonitoringRules(docs, { amanFeedStale: true }));
		const alerts = docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === 'ariva-monitoring-alerts');
		for (const [key, text] of Object.entries(alerts?.data ?? {})) {
			for (const threshold of JSON.parse(text).condition?.thresholds?.spec ?? []) {
				if (JSON.stringify(threshold.channels) !== JSON.stringify(['ops-pager', 'site it'])) findings.push(`${key}: a threshold does not notify monitoring.channels`);
			}
		}
		// ARV-257: the rule reads each border site's feed age and names the site, so one site's feed stopping is seen while
		// another's flows; it fires at T_stale (2 minutes by default) and when no age is reported at all.
		const text = Object.entries(alerts?.data ?? {}).find(([key]) => key.startsWith('amanFeedStale'))?.[1];
		const rule = text ? JSON.parse(text) : null;
		const spec = rule?.condition?.compositeQuery?.queries?.[0]?.spec;
		const threshold = rule?.condition?.thresholds?.spec?.[0];
		if (!rule) findings.push('the AMAN feed rule is not rendered');
		else {
			if (spec?.aggregations?.[0]?.metricName !== 'ariva.border.feed_age_seconds') findings.push('the AMAN feed rule does not read ariva.border.feed_age_seconds');
			if (JSON.stringify(spec?.groupBy?.map((g) => g.name)) !== JSON.stringify(['site'])) findings.push('the AMAN feed rule is not grouped by site');
			if (JSON.stringify(rule.notificationSettings?.groupBy) !== JSON.stringify(['site'])) findings.push('the AMAN feed rule does not notify per site');
			if (threshold?.op !== 'above' || threshold?.matchType !== 'last' || threshold?.target !== 120) findings.push(`the AMAN feed rule's threshold is not an age above 120 s: ${JSON.stringify(threshold)}`);
			if (rule.condition?.alertOnAbsent !== true) findings.push('the AMAN feed rule does not fire when no feed age is reported');
		}
		// ARV-258: the queue engine's checkpoint age per group, at 2 minutes by default.
		const stale = JSON.parse(monitoringRule(docs, 'streamCheckpointStale.json') || 'null');
		const staleSpec = stale?.condition?.compositeQuery?.queries?.[0]?.spec;
		const staleThreshold = stale?.condition?.thresholds?.spec?.[0];
		if (!stale) findings.push('the stream checkpoint rule is not rendered');
		else {
			if (staleSpec?.aggregations?.[0]?.metricName !== 'ariva.stream.checkpoint_age_seconds' || staleSpec?.aggregations?.[0]?.spaceAggregation !== 'max')
				findings.push('the stream checkpoint rule does not read the largest ariva.stream.checkpoint_age_seconds');
			if (JSON.stringify(staleSpec?.groupBy?.map((g) => g.name)) !== JSON.stringify(['group'])) findings.push('the stream checkpoint rule is not grouped by group');
			if (staleThreshold?.op !== 'above' || staleThreshold?.matchType !== 'last' || staleThreshold?.target !== 120 || staleThreshold?.name !== 'critical')
				findings.push(`the stream checkpoint rule's threshold is not an age above 120 s, critical: ${JSON.stringify(staleThreshold)}`);
			if (stale.evaluation?.spec?.evalWindow !== '2m') findings.push('the stream checkpoint rule does not evaluate over its minutes');
		}
	}
	// The default scopes: the release's environment and namespace.
	const own = render('values-k8s-dev.yaml', [], 'ariva-site');
	if (own.status !== 0) findings.push(`a release in ariva-site: template failed\n${own.stderr}`);
	else {
		const docs = parse(own.stdout);
		findings.push(...monitoringEnvironment(docs, 'k8s-dev'));
		if (!monitoringRule(docs, 'disk.json').includes("k8s.namespace.name IN ['ariva-site']")) findings.push('disk does not read the release namespace by default');
		if (!monitoringRule(docs, 'ingressCertificateExpiry.json').includes('{namespace=~\\"ariva-site\\"}')) findings.push('ingressCertificateExpiry does not read the release namespace by default');
	}
	// The scopes set: another environment name, namespaces and the exported_namespace label.
	const set = render('values-k8s-dev.yaml', ['monitoring.environment=site-a', 'monitoring.alerts.disk.namespaces={ariva-a,ariva-db}',
		'monitoring.alerts.ingressCertificateExpiry.namespaces={ariva-a,ariva-edge}', 'monitoring.alerts.ingressCertificateExpiry.namespaceLabel=exported_namespace']);
	if (set.status !== 0) findings.push(`the scopes set: template failed\n${set.stderr}`);
	else {
		const docs = parse(set.stdout);
		findings.push(...checkManifests(docs, { environment: 'k8s-dev' }), ...monitoringEnvironment(docs, 'site-a'));
		if (!monitoringRule(docs, 'disk.json').includes("k8s.namespace.name IN ['ariva-a', 'ariva-db']")) findings.push('disk does not read monitoring.alerts.disk.namespaces');
		if (!monitoringRule(docs, 'ingressCertificateExpiry.json').includes('{exported_namespace=~\\"ariva-a|ariva-edge\\"}')) findings.push('ingressCertificateExpiry does not read its namespaces through namespaceLabel');
	}
	const off = render('values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'monitoring.enabled=false']);
	if (off.status !== 0) findings.push(`monitoring off: template failed\n${off.stderr}`);
	else if (parse(off.stdout).some((doc) => doc?.metadata?.labels?.['app.kubernetes.io/component'] === 'monitoring')) findings.push('monitoring off still renders a monitoring object');
	if (findings.length) {
		failed = true;
		console.error(`FAIL monitoring switches:\n  ${findings.join('\n  ')}`);
	} else {
		console.log('PASS monitoring switches: the AMAN feed rule where turned on, channels on every threshold, the release\'s environment and namespace by default and the scopes set, nothing when monitoring is off');
	}
}

// ARV-092c (ASVS 5.0 V5.4.3, V12.3.1; CWE-269): the malware scanner beside api-main for each signature source, in dev and in
// production with the NetworkPolicies (the mirror's egress for api-main only, nothing for mounted), and off; then every refusal
// of the release guards for its own reason, none repeating a value that could be secret.
{
	const findings = [];
	const counts = [];
	const MIRROR_CA = ['uploadScan.signatures.source=mirror', 'uploadScan.signatures.mirrorUrl=https://clamav-mirror.site.local/clamav', 'uploadScan.signatures.mirrorCaSecretName=site-clamav-mirror-ca'];
	const NO_SITE = ['uploadScan.signatures.source=', 'uploadScan.signatures.mirrorUrl=', 'uploadScan.signatures.mirrorCaSecretName='];
	const peers = (cidr) => ({ networkPolicies: { clamavMirror: { to: [{ ipBlock: { cidr } }] } } });
	for (const [label, file, sets, extra, environment, helmArgs = []] of [
		['dev from the ClamAV mirrors (the default)', 'values-k8s-dev.yaml', [], [], 'k8s-dev'],
		['dev from a site mirror over http', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mirror', 'uploadScan.signatures.mirrorUrl=http://10.80.0.5:8080/clamav', 'uploadScan.signatures.checksPerDay=24'], [], 'k8s-dev'],
		['dev from a site mirror over https with its CA', 'values-k8s-dev.yaml', MIRROR_CA, [], 'k8s-dev'],
		['dev with mounted signatures', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mounted', 'uploadScan.signatures.claimName=site-clamav-signatures'], [], 'k8s-dev'],
		['dev without the scan', 'values-k8s-dev.yaml', ['uploadScan.enabled=false'], [], 'k8s-dev'],
		// ARV-092c: api-main's HPA reads its own container from Kubernetes 1.27 and the pod's metrics below (the API server
		// refuses a ContainerResource metric there); Helm's own default is newer.
		['dev on Kubernetes 1.26', 'values-k8s-dev.yaml', [], [], 'k8s-dev', ['--kube-version', '1.26.0']],
		['dev on Kubernetes 1.27', 'values-k8s-dev.yaml', [], [], 'k8s-dev', ['--kube-version', '1.27.0']],
		['demo from the ClamAV mirrors', 'values-k8s-demo.yaml', [], [], 'k8s-demo'],
		['production from the ClamAV mirrors', 'values-k8s-prd.yaml', [PRD, ...NO_SITE, 'uploadScan.signatures.source=public'], [valuesFile(peers('104.16.0.0/13'))], 'k8s-prd'],
		['production with mounted signatures', 'values-k8s-prd.yaml', [PRD, ...NO_SITE, 'uploadScan.signatures.source=mounted', 'uploadScan.signatures.claimName=site-clamav-signatures'], [], 'k8s-prd']
	]) {
		const result = render(file, sets, '', extra, helmArgs);
		if (result.status !== 0) {
			findings.push(`${label}: template failed\n${result.stderr}`);
			continue;
		}
		const containerMetrics = !(helmArgs.includes('--kube-version') && helmArgs[helmArgs.indexOf('--kube-version') + 1].startsWith('1.26'));
		const setValues = {};
		for (const set of sets.filter((entry) => entry.startsWith('uploadScan.'))) {
			const [key, value] = set.split(/=(.*)/s);
			const pathParts = key.split('.');
			let node = setValues;
			for (const part of pathParts.slice(0, -1)) node = node[part] ??= {};
			node[pathParts.at(-1)] = value === 'false' ? false : /^\d+$/.test(value) ? Number(value) : value;
		}
		const values = mergedValues([file, ...extra, setValues]);
		const docs = parseAsKubernetes(result.stdout);
		const found = [...checkManifests(parse(result.stdout), { environment }), ...uploadScanFindings(docs, { ...uploadScanOptions(values), containerMetrics }), ...networkFindings(result.stdout, values, environment)];
		const hpaTypes = [...new Set((docs.find((doc) => doc?.kind === 'HorizontalPodAutoscaler' && doc.metadata?.name === UPLOAD_SCAN.hpa)?.spec?.metrics ?? []).map((metric) => metric.type))].join(', ');
		if (helmArgs.includes('--kube-version')) counts.push(`${label}: api-main's HPA on ${hpaTypes} metrics`);
		const mirrorEgress = docs.find((doc) => doc?.kind === 'NetworkPolicy' && doc.metadata?.name === 'ariva-clamav-mirror-egress');
		const source = uploadScanOptions(values).source;
		if (environment === 'k8s-prd' && (source === 'mounted') !== !mirrorEgress) found.push(`the mirror's egress is ${mirrorEgress ? 'rendered' : 'missing'} with source ${source}`);
		findings.push(...found.map((finding) => `${label}: ${finding}`));
		counts.push(`${label}: ${describeUploadScan(values)}`);
	}
	if (findings.length) {
		failed = true;
		console.error(`FAIL malware scan beside api-main:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS malware scan beside api-main: ${counts.join('; ')}; the scanner in api-main's pod only, pinned, as uid 100, read-only, ` +
			"with its own mounts and resources, no readiness or startup probe (liveness only, after the load and longer than a reload), clamd.conf with the repository's limits " +
			'and official databases only, MaxThreads and MaxQueue bounding clamd\'s /tmp and the signature volume sized for an update, ' +
			"the HPA on api-main's own container from Kubernetes 1.27 (the pod's below)");
	}
	// The refusals: each for its own reason, and no message repeats a planted value (a mirror URL may carry a credential).
	const PLANTED = 'Stand-In-Secret-7';
	for (const [label, file, sets, message, extraValues] of [
		['production without the scan', 'values-k8s-prd.yaml', [PRD, 'uploadScan.enabled=false'], 'k8s-prd scans every upload for malware before it is stored or imported: set uploadScan.enabled true'],
		['a release with logging.required without the scan', 'values-k8s-dev.yaml', ['logging.required=true', 'logging.collector=loki', 'logging.pseudonymKeySecret.name=ariva-log-pseudonym-key', 'uploadScan.enabled=false'],
			'a release with logging.required scans every upload for malware'],
		['production without a signature source', 'values-k8s-prd.yaml', [PRD, ...NO_SITE], "k8s-prd chooses how clamd's signatures arrive"],
		['production without the uploadScan values', 'values-k8s-prd.yaml', [PRD, 'uploadScan=null'], 'uploadScan must be a map'],
		['a signature source that is not one of the three', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=internet'], 'uploadScan.signatures.source must be public, mirror or mounted'],
		['an empty signature source in dev', 'values-k8s-dev.yaml', ['uploadScan.signatures.source='], 'uploadScan.signatures.source must be public, mirror or mounted'],
		['a site mirror without its URL', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mirror'], 'uploadScan.signatures.source mirror needs uploadScan.signatures.mirrorUrl'],
		['a mirror URL with a credential', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mirror', `uploadScan.signatures.mirrorUrl=https://ariva:${PLANTED}@clamav-mirror.site.local`],
			'uploadScan.signatures.mirrorUrl must be the http:// or https:// URL of the site\'s signature mirror'],
		['a mirror URL with a token in its query', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mirror', `uploadScan.signatures.mirrorUrl=https://clamav-mirror.site.local/?token=${PLANTED}`],
			'uploadScan.signatures.mirrorUrl must be the http:// or https:// URL'],
		['a mirror URL that writes a second freshclam directive', 'values-k8s-dev.yaml', [], 'uploadScan.signatures.mirrorUrl must be the http:// or https:// URL',
			{ uploadScan: { signatures: { source: 'mirror', mirrorUrl: `https://clamav-mirror.site.local\nDatabaseMirror ${PLANTED}.example` } } }],
		['production\'s mirror over plain http', 'values-k8s-prd.yaml', [PRD, 'uploadScan.signatures.mirrorUrl=http://clamav-mirror.site.local', 'uploadScan.signatures.mirrorCaSecretName='],
			'k8s-prd takes signature updates over TLS only: uploadScan.signatures.mirrorUrl must be an https:// URL'],
		['a mirror over plain http with logging.required', 'values-k8s-dev.yaml', ['logging.required=true', 'logging.collector=loki', 'logging.pseudonymKeySecret.name=ariva-log-pseudonym-key',
			'uploadScan.signatures.source=mirror', 'uploadScan.signatures.mirrorUrl=http://clamav-mirror.site.local'], 'a release with logging.required takes signature updates over TLS only'],
		['a mirror URL over ftp', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mirror', 'uploadScan.signatures.mirrorUrl=ftp://clamav-mirror.site.local'], 'uploadScan.signatures.mirrorUrl must be the http:// or https:// URL'],
		['a mirror URL with source public', 'values-k8s-dev.yaml', ['uploadScan.signatures.mirrorUrl=https://clamav-mirror.site.local'], 'uploadScan.signatures.mirrorUrl is read with source mirror only'],
		['the mirror\'s CA with source public', 'values-k8s-dev.yaml', ['uploadScan.signatures.mirrorCaSecretName=site-clamav-mirror-ca'], 'uploadScan.signatures.mirrorCaSecretName is read with source mirror only'],
		['the mirror\'s CA over http', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mirror', 'uploadScan.signatures.mirrorUrl=http://clamav-mirror.site.local', 'uploadScan.signatures.mirrorCaSecretName=site-clamav-mirror-ca'],
			'uploadScan.signatures.mirrorCaSecretName needs an https:// mirrorUrl'],
		['a mirror CA Secret name that is not a Kubernetes name', 'values-k8s-dev.yaml', [...MIRROR_CA.slice(0, 2), 'uploadScan.signatures.mirrorCaSecretName=Site_CA'], 'uploadScan.signatures.mirrorCaSecretName must be a Kubernetes secret name'],
		['mounted signatures without their claim', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mounted'], 'uploadScan.signatures.source mounted needs uploadScan.signatures.claimName'],
		['a claim with source public', 'values-k8s-dev.yaml', ['uploadScan.signatures.claimName=site-clamav-signatures'], 'uploadScan.signatures.claimName is read with source mounted only'],
		['a claim name that is not a Kubernetes name', 'values-k8s-dev.yaml', ['uploadScan.signatures.source=mounted', 'uploadScan.signatures.claimName=Site_Signatures'], 'uploadScan.signatures.claimName must be a Kubernetes name'],
		['production\'s mirror without its peers', 'values-k8s-prd.yaml', [PRD], 'uploadScan.signatures.source public or mirror needs networkPolicies.clamavMirror.to', { networkPolicies: { clamavMirror: { to: [] } } }],
		['production from the ClamAV mirrors without their peers', 'values-k8s-prd.yaml', [PRD, ...NO_SITE, 'uploadScan.signatures.source=public'], 'needs networkPolicies.clamavMirror.to', { networkPolicies: { clamavMirror: { to: [] } } }],
		['dev under the network policies without the mirrors\' peers', 'values-k8s-dev.yaml', ['networkPolicies.enabled=true'], 'uploadScan.signatures.source public or mirror needs networkPolicies.clamavMirror.to'],
		['production\'s mirror open to every address', 'values-k8s-prd.yaml', [PRD], 'networkPolicies.clamavMirror.to[0].ipBlock: 0.0.0.0/0 is wider than /8', peers('0.0.0.0/0')],
		['the mirror\'s egress on a port that is not one', 'values-k8s-dev.yaml', [], 'networkPolicies.clamavMirror.ports: 70000 is not a port', { networkPolicies: { clamavMirror: { ports: [70000] } } }],
		['an unknown uploadScan setting', 'values-k8s-dev.yaml', ['uploadScan.mode=sidecar'], 'uploadScan.mode is not a setting'],
		['uploadScan that is not a map', 'values-k8s-dev.yaml', ['uploadScan=on'], 'uploadScan must be a map'],
		['an uploadScan.enabled that is not true or false', 'values-k8s-dev.yaml', ['uploadScan.enabled=yes'], 'uploadScan.enabled must be true or false'],
		['the scanner image without a digest', 'values-k8s-dev.yaml', ['uploadScan.image.digest='], 'uploadScan.image.digest must be a sha256 digest'],
		['the latest scanner image', 'values-k8s-dev.yaml', ['uploadScan.image.tag=latest'], 'uploadScan.image.tag must be an explicit ClamAV tag'],
		['a scanner image with a pull policy of its own', 'values-k8s-dev.yaml', ['uploadScan.image.pullPolicy=Always'], 'uploadScan.image must be a map of repository, tag and digest'],
		['a scanner repository that is not one', 'values-k8s-dev.yaml', ['uploadScan.image.repository=Clam AV'], 'uploadScan.image.repository must be an image repository'],
		['no signature check at all', 'values-k8s-dev.yaml', ['uploadScan.signatures.checksPerDay=0'], 'uploadScan.signatures.checksPerDay must be a whole number from 1 to 24'],
		['more than one signature check an hour', 'values-k8s-dev.yaml', ['uploadScan.signatures.checksPerDay=25'], 'uploadScan.signatures.checksPerDay must be a whole number from 1 to 24'],
		['a signature setting that is not one', 'values-k8s-dev.yaml', [`uploadScan.signatures.password=${PLANTED}`], 'uploadScan.signatures.password is not a setting'],
		['clamd without a memory limit', 'values-k8s-dev.yaml', [], 'uploadScan.clamd must be a map of resources', { uploadScan: { clamd: { resources: { limits: { memory: null } } } } }],
		['freshclam without its resources', 'values-k8s-dev.yaml', ['uploadScan.freshclam=small'], 'uploadScan.freshclam must be a map of resources']
	]) {
		const result = render(file, sets, '', extraValues ? [valuesFile(extraValues)] : []);
		if (result.status === 0) {
			failed = true;
			console.error(`FAIL release guard: rendering ${label} must fail but succeeded`);
		} else if (!result.stderr.includes(message)) {
			failed = true;
			console.error(`FAIL release guard: rendering ${label} failed for another reason than "${message}"\n${result.stderr}`);
		} else if (result.stderr.includes(PLANTED)) {
			failed = true;
			console.error(`FAIL release guard: rendering ${label} repeats a value of the release in its message`);
		} else {
			console.log(`PASS release guard: ${label} is refused`);
		}
	}
}

// ARV-062: the Helmfile the release pipelines apply renders for every environment, installs the database before the
// platform, and refuses production without a build number. Needs Helmfile 1.x (HELMFILE or on PATH); required in GitHub
// Actions, skipped elsewhere.
if (spawnSync(helmfile, ['--version'], { encoding: 'utf8', shell: false }).status !== 0) {
	if (process.env.GITHUB_ACTIONS) {
		console.error('FAIL helmfile not found; failing because this is GitHub Actions');
		failed = true;
	} else {
		console.warn('helmfile not found (set HELMFILE); Helmfile environments SKIPPED locally');
	}
} else {
	// Helmfile calls "helm": put the Helm this test uses first on its PATH.
	const env = helm.includes(path.sep) ? { ...process.env, PATH: `${path.dirname(path.resolve(helm))}${path.delimiter}${process.env.PATH}` } : process.env;
	// ARV-092c: prd takes the site's signature source (PRD_SITE), as a site's own values file would.
	const template = (environment, sets = []) => spawnSync(helmfile, ['-f', helmfileState, '-e', environment, ...sets, 'template', '--skip-deps', '--set', 'imageCredentials.password=',
		...(environment === 'prd' ? ['--values', PRD_SITE] : [])], { encoding: 'utf8', shell: false, env, maxBuffer: 64 * 1024 * 1024 });
	for (const [environment, sets, contextEnvironment] of [
		['dev', [], 'k8s-dev'], ['demo', [], 'k8s-demo'], ['localk8s', [], 'k8s-dev'], ['prd', ['--state-values-set', 'buildNumber=main-20261001.1'], 'k8s-prd']
	]) {
		const result = template(environment, sets);
		if (result.status !== 0) {
			failed = true;
			console.error(`FAIL helmfile -e ${environment}: template failed\n${result.stderr}`);
			continue;
		}
		const docs = parse(result.stdout);
		const findings = checkManifests(docs, { environment: contextEnvironment });
		if (!docs.some((doc) => doc?.kind === 'StatefulSet' && doc.metadata?.name === 'timescaledb')) findings.push('the timescaledb release is not installed');
		if (!docs.some((doc) => doc?.kind === 'Job' && doc.metadata?.name === 'kafka-topics')) findings.push('the kafka-topics job is not rendered');
		findings.push(...missingMonitoringRules(docs), ...monitoringEnvironment(docs, contextEnvironment));
		// ARV-082a: TimescaleDB with TLS in prd only (state value timescaledbTls), from the chart's default secret.
		const tls = environment === 'prd';
		findings.push(...databaseTlsFindings(parseAsKubernetes(result.stdout), { tls }), ...serverCaReach(docs));
		// ARV-082b: the pods over TLS in prd only (state value podTls), with the chart's default secrets.
		findings.push(...(tls ? podTlsRender(result.stdout) : podTlsFindings(parseAsKubernetes(result.stdout), { tls: false })));
		// ARV-084: the Ingresses' headers, plain HTTP and cookie, as the release applies them.
		findings.push(...ingressHardeningFindings(parseAsKubernetes(result.stdout), { environment: contextEnvironment }));
		// ARV-085: the NetworkPolicies in prd only (state value networkPolicies, the release sets them on), beside the database's own.
		const values = mergedValues([{ dev: 'values-k8s-dev.yaml', demo: 'values-k8s-demo.yaml', localk8s: 'values-localk8s.yaml', prd: 'values-k8s-prd.yaml' }[environment]]);
		findings.push(...networkFindings(result.stdout, values, contextEnvironment, { enabled: tls }));
		// ARV-091a: the log pseudonym key and the collector, required in prd by the release (state value logShipping).
		findings.push(...logShippingFindings(parseAsKubernetes(result.stdout), logShippingOptions(values, contextEnvironment, tls)));
		// ARV-092c: the malware scanner beside api-main (prd: the site's mirror, PRD_SITE).
		findings.push(...uploadScanFindings(parseAsKubernetes(result.stdout), uploadScanOptions(values)));
		// ARV-232: the server runs at the state value databaseMaxConnections (200, and 400 in prd), the limit the platform release
		// checks its replicas against (the refusal below proves the platform release reads the same value).
		const serverArgs = docs.find((doc) => doc?.kind === 'StatefulSet' && doc.metadata?.name === 'timescaledb')?.spec?.template?.spec?.containers?.[0]?.args ?? [];
		const maxConnections = `max_connections=${environment === 'prd' ? 400 : 200}`;
		const flag = serverArgs.indexOf(maxConnections);
		if (flag < 1 || serverArgs[flag - 1] !== '-c') findings.push(`the database's arguments do not set ${maxConnections}: ${JSON.stringify(serverArgs)}`);
		if (findings.length) {
			failed = true;
			console.error(`FAIL helmfile -e ${environment}:\n  ${findings.join('\n  ')}`);
		} else {
			console.log(`PASS helmfile -e ${environment}: ${docs.length} objects, database and topics job included, TimescaleDB ${tls ? 'with TLS' : 'without TLS'}, ` +
				`${tls ? 'the pods over TLS' : 'plain HTTP to the pods'}, the Ingresses' headers held and plain HTTP ${describeModes(docs)}, ${describeNetwork(result.stdout, values)}, ${describeLogShipping(values)}, ${describeUploadScan(values)}`);
		}
	}
	// ARV-232: the platform release checks its replicas against the state value databaseMaxConnections, over a site's own
	// databaseConnections.serverMaxConnections (the prd site fixture's 400): at 300 prd's replicas need more than the server allows.
	{
		const attempt = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--state-values-set', 'databaseMaxConnections=300']);
		if (attempt.status === 0 || !attempt.stderr.includes('pass what the server allows')) {
			failed = true;
			console.error(`FAIL release guard: helmfile -e prd with databaseMaxConnections 300 must fail on the connection budget but ${attempt.status === 0 ? 'succeeded' : `failed otherwise\n${attempt.stderr}`}`);
		} else {
			console.log('PASS release guard: helmfile -e prd with databaseMaxConnections 300 is refused (the platform release reads the state value)');
		}
	}
	// ARV-082a: prd refuses TimescaleDB without TLS.
	{
		const attempt = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--state-values-set', 'timescaledbTls=false']);
		if (attempt.status === 0 || !attempt.stderr.includes('prd runs TimescaleDB with TLS')) {
			failed = true;
			console.error(`FAIL release guard: helmfile -e prd with timescaledbTls false must fail but ${attempt.status === 0 ? 'succeeded' : `failed otherwise\n${attempt.stderr}`}`);
		} else {
			console.log('PASS release guard: helmfile -e prd with timescaledbTls false is refused');
		}
	}
	// ARV-085: prd refuses the pods without their NetworkPolicies, in the Helmfile and, under it, in the chart.
	{
		const attempt = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--state-values-set', 'networkPolicies=false']);
		const overridden = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--set', 'networkPolicies.enabled=false']);
		if (attempt.status === 0 || !attempt.stderr.includes('prd isolates every pod') || overridden.status === 0 || !overridden.stderr.includes('k8s-prd isolates every pod')) {
			failed = true;
			console.error(`FAIL release guard: helmfile -e prd with networkPolicies false must fail\n${attempt.stderr}${overridden.stderr}`);
		} else {
			console.log('PASS release guard: helmfile -e prd with networkPolicies false is refused, and so is the chart under it');
		}
	}
	// ARV-091a: prd refuses to render without log shipping, in the Helmfile and, under it, in the chart; the release's
	// logging.required holds in any environment that sets the state value.
	{
		const attempt = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--state-values-set', 'logShipping=false']);
		const noCollector = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--set', 'logging.collector=']);
		const noKey = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--set', 'logging.pseudonymKeySecret.name=']);
		const dev = template('dev', ['--state-values-set', 'logShipping=true']);
		const refused = (result, message) => result.status !== 0 && result.stderr.includes(message);
		if (!refused(attempt, "prd ships the hosts' logs to a collector over TLS") || !refused(noCollector, "k8s-prd ships the hosts' logs to a collector over TLS") ||
			!refused(noKey, 'k8s-prd needs logging.pseudonymKeySecret.name') || !refused(dev, "a release with logging.required ships the hosts' logs")) {
			failed = true;
			console.error(`FAIL release guard: helmfile -e prd with logShipping false, without a collector or without the key's Secret, and -e dev with logShipping true must each fail for its own reason\n${attempt.stderr}${noCollector.stderr}${noKey.stderr}${dev.stderr}`);
		} else {
			console.log("PASS release guard: helmfile -e prd with logShipping false is refused, and so is the chart under it without a collector or without the key's Secret, and -e dev with logShipping true and neither");
		}
	}
	// ARV-092c: prd refuses to render without the malware scan, in the Helmfile and, under it, in the chart; and without the site's
	// signature source, which no file of the repository gives it.
	{
		const attempt = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--state-values-set', 'uploadScan=false']);
		const overridden = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--set', 'uploadScan.enabled=false']);
		const noSource = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--set', 'uploadScan.signatures.source=', '--set', 'uploadScan.signatures.mirrorUrl=',
			'--set', 'uploadScan.signatures.mirrorCaSecretName=']);
		const refused = (result, message) => result.status !== 0 && result.stderr.includes(message);
		if (!refused(attempt, 'prd scans every upload for malware with clamd beside api-main') || !refused(overridden, 'k8s-prd scans every upload for malware') ||
			!refused(noSource, "k8s-prd chooses how clamd's signatures arrive")) {
			failed = true;
			console.error(`FAIL release guard: helmfile -e prd with uploadScan false, with the chart's scan off or without a signature source must each fail for its own reason\n${attempt.stderr}${overridden.stderr}${noSource.stderr}`);
		} else {
			console.log('PASS release guard: helmfile -e prd with uploadScan false is refused, and so is the chart under it with the scan off or without the site\'s signature source');
		}
	}
	// ARV-082b: prd refuses the pods without TLS.
	{
		const attempt = template('prd', ['--state-values-set', 'buildNumber=main-20261001.1', '--state-values-set', 'podTls=false']);
		if (attempt.status === 0 || !attempt.stderr.includes('prd serves the pods over TLS')) {
			failed = true;
			console.error(`FAIL release guard: helmfile -e prd with podTls false must fail but ${attempt.status === 0 ? 'succeeded' : `failed otherwise\n${attempt.stderr}`}`);
		} else {
			console.log('PASS release guard: helmfile -e prd with podTls false is refused');
		}
	}
	// ARV-002: the GitHub dev release (.github/workflows/release-dev.yml) deploys from GHCR, pins every image by the digest
	// it verified and owns the pull secret itself. Same --set values as the workflow.
	{
		const repository = 'ghcr.io/example/ariva';
		const services = ['api-main', 'api-ingest', 'api-stream', 'api-cronz', 'api-integration', 'simulation', 'web'];
		const digestOf = (service) => 'sha256:' + createHash('sha256').update(service).digest('hex');
		const result = template('dev', ['--state-values-set', 'buildNumber=main-41', '--set', `imageRepository=${repository}`, '--set', 'releaseVersion=main-41',
			...services.flatMap((service) => ['--set', `imageDigests.${service}=${digestOf(service)}`])]);
		const findings = [];
		if (result.status !== 0) findings.push(`template failed\n${result.stderr}`);
		else {
			const docs = parse(result.stdout);
			findings.push(...checkManifests(docs, { environment: 'k8s-dev' }));
			const images = docs.flatMap((doc) => {
				const spec = doc?.spec?.template?.spec ?? doc?.spec?.jobTemplate?.spec?.template?.spec;
				return spec ? [...(spec.initContainers ?? []), ...(spec.containers ?? [])].map((container) => ({ name: `${doc.kind}/${doc.metadata?.name}`, image: String(container.image) })) : [];
			}).filter(({ name }) => !/timescaledb/.test(name));
			// ARV-092c: the scanner beside api-main is the chart's pinned ClamAV image (init, clamd and freshclam), never an Ariva one.
			const scannerImage = `${CHART_SCANNER.repository}:${CHART_SCANNER.tag}@${CHART_SCANNER.digest}`;
			const scanners = images.filter(({ image }) => image.split('@')[0].split(':')[0].endsWith('/clamav') || image.startsWith('clamav/'));
			if (scanners.length !== 3 || scanners.some(({ name, image }) => name !== 'Deployment/api-main-deployment' || image !== scannerImage)) {
				findings.push(`the scanner's images are ${scanners.map(({ name, image }) => `${name} ${image}`).join(', ') || 'none'}, not ${scannerImage} three times in api-main's pod`);
			}
			const arivaImages = images.filter((entry) => !scanners.includes(entry));
			if (arivaImages.length < 9) findings.push(`expected the platform's 9 workloads, found ${arivaImages.length} images`);
			for (const { name, image } of arivaImages) {
				const match = image.match(/^ghcr\.io\/example\/ariva\/([a-z-]+):main-41@(sha256:[0-9a-f]{64})$/);
				if (!match) findings.push(`${name}: ${image} is not ${repository}/<service>:main-41@sha256:<digest>`);
				else if (match[2] !== digestOf(match[1])) findings.push(`${name}: ${image} carries another service's digest`);
			}
			if (docs.some((doc) => doc?.kind === 'Secret' && doc.metadata?.name === 'dalilacr-secret')) findings.push('the chart renders dalilacr-secret although the workflow owns it');
		}
		const refusedWith = (sets, message) => {
			const attempt = template('dev', ['--state-values-set', 'buildNumber=main-41', ...sets]);
			return attempt.status !== 0 && attempt.stderr.includes(message);
		};
		const refused = refusedWith(['--set', 'imageDigests.api-main=latest'], 'imageDigests.api-main must be a sha256 digest')
			&& refusedWith(['--set', `imageDigests.other=${digestOf('other')}`], 'imageDigests.other is not an Ariva service');
		if (!refused) findings.push('a malformed digest or an unknown service in imageDigests was not refused');
		if (findings.length) {
			failed = true;
			console.error(`FAIL helmfile -e dev as release-dev:\n  ${findings.join('\n  ')}`);
		} else {
			console.log('PASS helmfile -e dev as release-dev: every Ariva image from GHCR pinned by its digest, the scanner beside api-main the chart\'s pinned ClamAV, no chart-owned pull secret, bad digests refused');
		}
	}
	if (template('prd').status === 0) {
		failed = true;
		console.error('FAIL release guard: helmfile -e prd without a build number must fail but succeeded');
	} else {
		console.log('PASS release guard: helmfile -e prd without a build number is refused');
	}
	// ARV-127: the offline bundle's Helmfile, staged as scripts/offline/bundle.mjs stages it (packaged charts, values files,
	// images.yaml), renders the same objects as this Helmfile for every environment given the same images; every image comes
	// from the site's mirror, an Ariva image by the bundle's digest; it refuses to render without a mirror; and a site's own
	// values files reach their release without moving an image.
	{
		const { stageHelm } = await import(pathToFileURL(path.resolve(here, '..', '..', '..', '..', 'scripts', 'offline', 'bundle.mjs')).href);
		const services = ['api-main', 'api-ingest', 'api-stream', 'api-cronz', 'api-integration', 'simulation', 'web'];
		const digestOf = (service) => 'sha256:' + createHash('sha256').update(service).digest('hex');
		const mirror = 'registry.example.local:5000/site';
		const staged = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-offline-helm-'));
		const findings = [];
		try {
			stageHelm(staged, { version: '1.2.0', build: 'v1.2.0-57', digests: Object.fromEntries(services.map((service) => [service, digestOf(service)])) });
			const offline = (environment, sets = [], site = environment === 'prd') => spawnSync(helmfile, ['-f', path.join(staged, 'helm', 'helmfile.yaml.gotmpl'), '-e', environment, ...sets, 'template', '--skip-deps',
				'--set', 'imageCredentials.password=', ...(site ? ['--values', PRD_SITE] : [])], { encoding: 'utf8', shell: false, env, maxBuffer: 64 * 1024 * 1024 });
			// The chart gives every Deployment a random rollme annotation (AMAN pattern); everything else must be equal.
			const objects = (text) => new Map(parse(text).map((doc) => [`${doc.kind}/${doc.metadata?.name}`, JSON.stringify(doc).replace(/"rollme":"[^"]*"/g, '"rollme":"*"')]));
			const same = ['--state-values-set', 'buildNumber=v1.2.0-57', '--set', `imageRepository=${mirror}/ariva`, '--set', 'releaseVersion=1.2.0',
				'--set', `image.repository=${mirror}/timescale/timescaledb-ha`, '--set', `uploadScan.image.repository=${mirror}/clamav/clamav`,
				...services.flatMap((service) => ['--set', `imageDigests.${service}=${digestOf(service)}`])];
			for (const environment of ['dev', 'demo', 'localk8s', 'prd']) {
				const fromBundle = offline(environment, ['--state-values-set', `mirror=${mirror}`]);
				const fromRepository = template(environment, same);
				if (fromBundle.status !== 0 || fromRepository.status !== 0) {
					findings.push(`-e ${environment}: template failed\n${fromBundle.stderr}${fromRepository.stderr}`);
					continue;
				}
				// ARV-084: the bundle's Ingresses as the site installs them.
				const bundleEnvironment = { prd: 'k8s-prd', demo: 'k8s-demo' }[environment] ?? 'k8s-dev';
				findings.push(...ingressHardeningFindings(parseAsKubernetes(fromBundle.stdout), { environment: bundleEnvironment }).map((finding) => `-e ${environment}: ${finding}`));
				const a = objects(fromBundle.stdout);
				const b = objects(fromRepository.stdout);
				const differing = [...new Set([...a.keys(), ...b.keys()])].filter((key) => a.get(key) !== b.get(key));
				if (differing.length) findings.push(`-e ${environment}: the bundle renders ${differing.join(', ')} differently`);
				const images = parse(fromBundle.stdout).flatMap((doc) => {
					const spec = doc?.spec?.template?.spec ?? doc?.spec?.jobTemplate?.spec?.template?.spec;
					return spec ? [...(spec.initContainers ?? []), ...(spec.containers ?? [])].map((container) => String(container.image)) : [];
				});
				const mirroredScanner = `${mirror}/clamav/clamav:${CHART_SCANNER.tag}@${CHART_SCANNER.digest}`;
				for (const image of images) {
					const ariva = image.match(/^registry\.example\.local:5000\/site\/ariva\/([a-z-]+):v1\.2\.0-57@(sha256:[0-9a-f]{64})$/);
					if (ariva ? ariva[2] !== digestOf(ariva[1]) : !/^registry\.example\.local:5000\/site\/timescale\/timescaledb-ha:[^@]+@sha256:[0-9a-f]{64}$/.test(image) && image !== mirroredScanner) {
						findings.push(`-e ${environment}: ${image} is not a mirror image pinned by the bundle's or the chart's digest`);
					}
				}
				// ARV-092c: the scanner from the mirror under the chart's tag and digest, as the site installs it.
				const bundleValues = mergedValues([{ dev: 'values-k8s-dev.yaml', demo: 'values-k8s-demo.yaml', localk8s: 'values-localk8s.yaml', prd: 'values-k8s-prd.yaml' }[environment]]);
				findings.push(...uploadScanFindings(parseAsKubernetes(fromBundle.stdout), uploadScanOptions(bundleValues, { image: { repository: `${mirror}/clamav/clamav` } })).map((finding) => `-e ${environment}: ${finding}`));
			}
			const unset = offline('prd');
			if (unset.status === 0 || !unset.stderr.includes("set the state value mirror")) findings.push('-e prd without a mirror rendered');
			if (offline('prd', ['--state-values-set', 'mirror=https://registry.example.local']).status === 0) findings.push('a mirror with a scheme rendered');
			// ARV-082a: prd refuses TimescaleDB without TLS here too.
			const withoutTls = offline('prd', ['--state-values-set', `mirror=${mirror}`, '--state-values-set', 'timescaledbTls=false']);
			if (withoutTls.status === 0 || !withoutTls.stderr.includes('prd runs TimescaleDB with TLS')) findings.push('-e prd with timescaledbTls false rendered');
			// ARV-082b: and the pods without TLS.
			const withoutPodTls = offline('prd', ['--state-values-set', `mirror=${mirror}`, '--state-values-set', 'podTls=false']);
			if (withoutPodTls.status === 0 || !withoutPodTls.stderr.includes('prd serves the pods over TLS')) findings.push('-e prd with podTls false rendered');
			// ARV-085: and the pods without their NetworkPolicies.
			const withoutPolicies = offline('prd', ['--state-values-set', `mirror=${mirror}`, '--state-values-set', 'networkPolicies=false']);
			if (withoutPolicies.status === 0 || !withoutPolicies.stderr.includes('prd isolates every pod')) findings.push('-e prd with networkPolicies false rendered');
			// ARV-091a: and without log shipping.
			const withoutLogs = offline('prd', ['--state-values-set', `mirror=${mirror}`, '--state-values-set', 'logShipping=false']);
			if (withoutLogs.status === 0 || !withoutLogs.stderr.includes("prd ships the hosts' logs")) findings.push('-e prd with logShipping false rendered');
			// ARV-092c: and without the malware scan, or without the site's signature source.
			const withoutScan = offline('prd', ['--state-values-set', `mirror=${mirror}`, '--state-values-set', 'uploadScan=false']);
			if (withoutScan.status === 0 || !withoutScan.stderr.includes('prd scans every upload for malware')) findings.push('-e prd with uploadScan false rendered');
			const withoutSource = offline('prd', ['--state-values-set', `mirror=${mirror}`], false);
			if (withoutSource.status === 0 || !withoutSource.stderr.includes("k8s-prd chooses how clamd's signatures arrive")) findings.push('-e prd without the site\'s signature source rendered');
			const site = path.join(staged, 'site.yaml');
			// ARV-082a: production sends telemetry over TLS only, so the site's endpoint is https.
			// ARV-082b: a site file that names the web pod's certificate and tries to turn pod TLS off.
			fs.writeFileSync(site, `otel:\n  endpoint: "https://otel.offline-site.local:4317"\nimageRepository: other.example/ariva\nimageDigests:\n  api-main: "${digestOf('planted')}"\nbuildNumber: planted\n` +
				'podTls:\n  enabled: false\n  secretNames:\n    web: site-web-tls\n' +
				// ARV-085: the collector's peers with its endpoint, and an attempt to turn the NetworkPolicies off.
				'networkPolicies:\n  enabled: false\n  otlp:\n    to:\n      - ipBlock:\n          cidr: 10.60.0.0/24\n' +
				// ARV-091a: the site's collector (the OTLP one above) and the key's Secret, and an attempt to drop the requirement.
				'logging:\n  collector: otlp\n  required: false\n  pseudonymKeySecret:\n    name: site-log-pseudonym-key\n' +
				// ARV-092c: the site's signatures (a claim it keeps current, as a site without internet may), an attempt to turn the
				// scan off and another scanner image.
				`uploadScan:\n  enabled: false\n  image:\n    repository: other.example/clamav\n    tag: planted\n    digest: "${digestOf('planted')}"\n  signatures:\n    source: mounted\n    claimName: site-clamav-signatures\n`);
			const database = path.join(staged, 'database.yaml');
			// ARV-082a: a site file that tries to turn the database's TLS off in prd.
			fs.writeFileSync(database, `persistence:\n  size: 77Gi\nimage:\n  tag: planted\n  digest: "${digestOf('planted')}"\ntls:\n  enabled: false\n`);
			const withSite = offline('prd', ['--state-values-set', `mirror=${mirror}`, '--state-values-set', `siteValues=${site}`, '--state-values-set', `databaseValues=${database}`], false);
			if (withSite.status !== 0) findings.push(`-e prd with site values: template failed\n${withSite.stderr}`);
			else {
				if (!withSite.stdout.includes('https://otel.offline-site.local:4317')) findings.push('siteValues did not reach the platform release');
				if (!withSite.stdout.includes('77Gi')) findings.push('databaseValues did not reach the TimescaleDB release');
				const tlsFindings = databaseTlsFindings(parseAsKubernetes(withSite.stdout), { tls: true });
				if (tlsFindings.length) findings.push(`a site databaseValues file turned the TimescaleDB TLS off in prd:\n    ${tlsFindings.join('\n    ')}`);
				findings.push(...ingressHardeningFindings(parseAsKubernetes(withSite.stdout), { environment: 'k8s-prd' }).map((finding) => `-e prd with site values: ${finding}`));
				// ARV-085: the NetworkPolicies stay on whatever the site file says, the collector's peers its own.
				findings.push(...networkFindings(withSite.stdout, mergedValues(['values-k8s-prd.yaml', site, { podTls: { enabled: true }, uploadScan: { enabled: true } }], { prdSite: false }), 'k8s-prd', { enabled: true }).map((finding) => `-e prd with site values: ${finding}`));
				// ARV-092c: the scan stays on whatever the site file says, the claim its own, the scanner the bundle's.
				findings.push(...uploadScanFindings(parseAsKubernetes(withSite.stdout), uploadScanOptions(mergedValues(['values-k8s-prd.yaml', site, { uploadScan: { enabled: true } }], { prdSite: false }),
					{ image: { repository: `${mirror}/clamav/clamav`, tag: CHART_SCANNER.tag, digest: CHART_SCANNER.digest } })).map((finding) => `-e prd with site values: ${finding}`));
				if (!parseAsKubernetes(withSite.stdout).some((doc) => doc?.kind === 'NetworkPolicy' && doc.metadata?.name === 'ariva-otlp-egress')) findings.push('a site values file did not open its collector');
				// ARV-091a: the site's collector and Secret reach the five API hosts, required whatever the site file says.
				findings.push(...logShippingFindings(parseAsKubernetes(withSite.stdout), logShippingOptions(mergedValues(['values-k8s-prd.yaml', site], { prdSite: false }), 'k8s-prd', true)).map((finding) => `-e prd with site values: ${finding}`));
				if (!withSite.stdout.includes('name: "site-log-pseudonym-key"')) findings.push("a site values file did not name the log pseudonym key's Secret");
				const podTlsSite = podTlsRender(withSite.stdout, { secretNames: { ...POD_TLS_SECRETS, web: 'site-web-tls' } });
				if (podTlsSite.length) findings.push(`a site values file turned pod TLS off in prd or did not name the web certificate:\n    ${podTlsSite.join('\n    ')}`);
				if (/other\.example|planted/.test(withSite.stdout) || withSite.stdout.includes(digestOf('planted').slice(7)) || !withSite.stdout.includes(`${mirror}/ariva/api-main:v1.2.0-57@${digestOf('api-main')}`)) {
					findings.push('a site values file moved an image away from the bundle\'s repository, build or digest');
				}
				const pinnedDatabase = fs.readFileSync(path.join(databaseChart, 'values.yaml'), 'utf8').match(/^\s+tag:\s*(\S+)\r?\n\s+digest:\s*(\S+)/m);
				if (!pinnedDatabase || !withSite.stdout.includes(`${mirror}/timescale/timescaledb-ha:${pinnedDatabase[1]}@${pinnedDatabase[2]}`)) {
					findings.push('a site databaseValues file moved the TimescaleDB image away from the chart\'s tag and digest');
				}
			}
			// A dry run's bundle pins no digest: its Helmfile must refuse to render, whatever the site's files say.
			const dry = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-offline-dry-'));
			try {
				stageHelm(dry, { version: '0.0.0-ci.1', build: 'ci-1', digests: {} });
				const unpinned = spawnSync(helmfile, ['-f', path.join(dry, 'helm', 'helmfile.yaml.gotmpl'), '-e', 'prd', '--state-values-set', `mirror=${mirror}`,
					'--state-values-set', `siteValues=${site}`, 'template', '--skip-deps', '--set', 'imageCredentials.password='], { encoding: 'utf8', shell: false, env, maxBuffer: 64 * 1024 * 1024 });
				if (unpinned.status === 0 || !unpinned.stderr.includes('a dry run bundle cannot be installed')) findings.push('a bundle without image digests (a dry run) rendered');
			} finally {
				fs.rmSync(dry, { recursive: true, force: true });
			}
		} finally {
			fs.rmSync(staged, { recursive: true, force: true });
		}
		if (findings.length) {
			failed = true;
			console.error(`FAIL offline bundle Helmfile:\n  ${findings.join('\n  ')}`);
		} else {
			console.log('PASS offline bundle Helmfile: the same objects as helmfile-k8s in every environment (the NetworkPolicies included), the Ingresses\' headers and plain HTTP held, every image from the mirror by digest (ClamAV under the chart\'s pin), no render without a mirror or with a dry run\'s unpinned images, site values reach their release without moving an image or turning the database TLS, pod TLS, the NetworkPolicies, log shipping or the malware scan off in prd (but name the certificates, the collector, the log key\'s Secret and the signature source), prd refused without either TLS, the NetworkPolicies, log shipping, the malware scan or a signature source');
		}
	}
}

process.exit(failed ? 1 : 0);
