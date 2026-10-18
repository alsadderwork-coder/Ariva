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
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs              needs helm 3 on PATH (or HELM=path)
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs --self-test  checks the rules against fixtures, no helm
// Without helm the test is skipped locally and fails in CI (CI=true).
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseAllDocuments } from 'yaml';
import { checkManifests, DATABASE_TLS12_CIPHERS, databaseTlsFindings, missingMonitoringRules, POD_TLS, podTlsFindings, podTlsSecrets, serverCaSecrets } from './checks.mjs';

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
	if (!/^COPY --from=builder \/app\/build\/nginx\/listen\.d\/plain\.conf \/etc\/nginx\/listen\.d\/plain\.conf\s*$/m.test(fs.readFileSync(path.join(web, 'Dockerfile'), 'utf8'))) {
		findings.push(`the web Dockerfile must copy build/nginx/listen.d/plain.conf to ${POD_TLS.webListen}/plain.conf`);
	}
	return findings;
}

function selfTest() {
	const good = checkManifests(parse(fs.readFileSync(path.join(here, 'fixtures', 'good.yaml'), 'utf8')), { environment: 'k8s-prd' });
	const bad = checkManifests(parse(fs.readFileSync(path.join(here, 'fixtures', 'bad.yaml'), 'utf8')), { environment: 'k8s-prd' });
	const expected = [
		'runAsNonRoot', 'runAsUser', 'seccompProfile', 'hostNetwork', 'allowPrivilegeEscalation', 'privileged',
		'capabilities.drop', 'readOnlyRootFilesystem', '/tmp', 'latest', 'needs a tls section', 'not covered by tls', 'production must pin',
		'token signing key', 'integration token signing key', 'integration token key ring', 'needs securityContext.fsGroup', 'token public keys', 'disable the access log', 'audit log off', 'only critical errors', 'its own ingress',
		'pinned by digest', 'carries a literal value', 'needs a NetworkPolicy', 'automountServiceAccountToken', 'Kafka__ProvisionTopics',
		'POSTGRES_INITDB_ARGS', 'POSTGRES_HOST_AUTH_METHOD', 'without NOSUPERUSER', 'must set DOTNET_ENVIRONMENT',
		'Deployment/no-environment', 'Job/argument-overrides', 'Deployment/later-config-map-overrides', 'Deployment/wrong-environment', 'Job/bare-argument-overrides',
		'may get the validation reader login', 'comes from its secret (secretKeyRef)', 'must get the validation reader login',
		'must not mark the validation reader secret optional',
		'terminationGracePeriodSeconds of at least 60', 'must set Hosting__GracePeriodSeconds',
		'needs a startupProbe', 'must set Hosting__StartupAllowanceSeconds',
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
	if (good.length || missing.length || tls.failures.length || podTls.failures.length || webImage.length) {
		console.error('chart-security self-test FAILED');
		if (good.length) console.error('  good fixture produced findings:\n   ' + good.join('\n   '));
		if (missing.length) console.error('  rules that did not fire on the bad fixture: ' + missing.join(', '));
		for (const failure of [...tls.failures, ...podTls.failures, ...webImage]) console.error(`  ${failure}`);
		process.exit(1);
	}
	console.log(`chart-security self-test: ${expected.length} rules fire on the bad fixture, ${tls.count} TimescaleDB TLS rules on the TLS fixtures and ${podTls.count} pod TLS rules ` +
		`on the pod TLS fixtures (each bad case trips exactly its rule); the good fixtures are clean; the web image takes its listener from ${POD_TLS.webListen}.`);
}

function helmAvailable() {
	return spawnSync(helm, ['version', '--short'], { encoding: 'utf8', shell: false }).status === 0;
}

function render(valuesFile, sets = [], namespace = '', extraFiles = []) {
	const args = ['template', 'ariva-platform', chart, '-f', path.join(chart, valuesFile), ...extraFiles.flatMap((file) => ['-f', file]), '--set', 'imageCredentials.password='];
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
	['the database tls values removed', ['tls=null']]
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

for (const [label, file, sets, message, annotation] of [
	['production without a build number', 'values-k8s-prd.yaml', []],
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
	['a devices offline share above 1', 'values-k8s-dev.yaml', ['monitoring.alerts.devicesOffline.share=2']],
	['a rule window of 0 minutes', 'values-k8s-dev.yaml', ['monitoring.alerts.hostDown.minutes=0']],
	['a rule window that is not a whole number', 'values-k8s-dev.yaml', ['monitoring.alerts.streamConsumerLag.minutes=2.5']],
	['a severity that is not warning or critical', 'values-k8s-dev.yaml', ['monitoring.alerts.busDegraded.severity=page']],
	['a values entry for a rule that does not exist', 'values-k8s-dev.yaml', ['monitoring.alerts.unknownRule.minutes=5']],
	['a channel name with a quote', 'values-k8s-dev.yaml', ['monitoring.channels={bad"name}']],
	['a disk rule without its claims', 'values-k8s-dev.yaml', ['monitoring.alerts.disk.claims=null']],
	['a claim that is not a Kubernetes name', 'values-k8s-dev.yaml', ["monitoring.alerts.disk.claims={data') OR (1}"]],
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
	['a server-snippet with a Lua block under pod TLS', 'values-k8s-prd.yaml', ['buildNumber=main-20261001.1'], 'server-snippet writes a proxy_ssl, grpc_ssl, _pass or _by_lua directive itself', 'server-snippet: "access_by_lua_block { ngx.exit(200) }"']
]) {
	const result = render(file, sets, '', annotation ? [ingressValues(annotation)] : []);
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

// ARV-082a: an https OTLP endpoint renders in production and reaches every host; the rule is production's, so dev keeps
// http; the servers' CA secret names reach exactly the five API hosts and the two jobs.
{
	const findings = [];
	const https = render('values-k8s-prd.yaml', ['buildNumber=main-20261001.1', 'otel.endpoint=https://otel.example:4317']);
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
		const result = spawnSync(helm, ['template', 'ariva-platform', path.join(copy, 'platform'), '-f', path.join(chart, 'values-k8s-prd.yaml'), '-f', path.join(copy, 'cases.json'),
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
	const template = (environment, sets = []) => spawnSync(helmfile, ['-f', helmfileState, '-e', environment, ...sets, 'template', '--skip-deps', '--set', 'imageCredentials.password='],
		{ encoding: 'utf8', shell: false, env, maxBuffer: 64 * 1024 * 1024 });
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
		if (findings.length) {
			failed = true;
			console.error(`FAIL helmfile -e ${environment}:\n  ${findings.join('\n  ')}`);
		} else {
			console.log(`PASS helmfile -e ${environment}: ${docs.length} objects, database and topics job included, TimescaleDB ${tls ? 'with TLS' : 'without TLS'}, ` +
				`${tls ? 'the pods over TLS' : 'plain HTTP to the pods'}`);
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
			if (images.length < 9) findings.push(`expected the platform's 9 workloads, found ${images.length} images`);
			for (const { name, image } of images) {
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
			console.log('PASS helmfile -e dev as release-dev: every image from GHCR pinned by its digest, no chart-owned pull secret, bad digests refused');
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
			const offline = (environment, sets = []) => spawnSync(helmfile, ['-f', path.join(staged, 'helm', 'helmfile.yaml.gotmpl'), '-e', environment, ...sets, 'template', '--skip-deps', '--set', 'imageCredentials.password='],
				{ encoding: 'utf8', shell: false, env, maxBuffer: 64 * 1024 * 1024 });
			// The chart gives every Deployment a random rollme annotation (AMAN pattern); everything else must be equal.
			const objects = (text) => new Map(parse(text).map((doc) => [`${doc.kind}/${doc.metadata?.name}`, JSON.stringify(doc).replace(/"rollme":"[^"]*"/g, '"rollme":"*"')]));
			const same = ['--state-values-set', 'buildNumber=v1.2.0-57', '--set', `imageRepository=${mirror}/ariva`, '--set', 'releaseVersion=1.2.0',
				'--set', `image.repository=${mirror}/timescale/timescaledb-ha`, ...services.flatMap((service) => ['--set', `imageDigests.${service}=${digestOf(service)}`])];
			for (const environment of ['dev', 'demo', 'localk8s', 'prd']) {
				const fromBundle = offline(environment, ['--state-values-set', `mirror=${mirror}`]);
				const fromRepository = template(environment, same);
				if (fromBundle.status !== 0 || fromRepository.status !== 0) {
					findings.push(`-e ${environment}: template failed\n${fromBundle.stderr}${fromRepository.stderr}`);
					continue;
				}
				const a = objects(fromBundle.stdout);
				const b = objects(fromRepository.stdout);
				const differing = [...new Set([...a.keys(), ...b.keys()])].filter((key) => a.get(key) !== b.get(key));
				if (differing.length) findings.push(`-e ${environment}: the bundle renders ${differing.join(', ')} differently`);
				const images = parse(fromBundle.stdout).flatMap((doc) => {
					const spec = doc?.spec?.template?.spec ?? doc?.spec?.jobTemplate?.spec?.template?.spec;
					return spec ? [...(spec.initContainers ?? []), ...(spec.containers ?? [])].map((container) => String(container.image)) : [];
				});
				for (const image of images) {
					const ariva = image.match(/^registry\.example\.local:5000\/site\/ariva\/([a-z-]+):v1\.2\.0-57@(sha256:[0-9a-f]{64})$/);
					if (ariva ? ariva[2] !== digestOf(ariva[1]) : !/^registry\.example\.local:5000\/site\/timescale\/timescaledb-ha:[^@]+@sha256:[0-9a-f]{64}$/.test(image)) {
						findings.push(`-e ${environment}: ${image} is not a mirror image pinned by the bundle's or the chart's digest`);
					}
				}
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
			const site = path.join(staged, 'site.yaml');
			// ARV-082a: production sends telemetry over TLS only, so the site's endpoint is https.
			// ARV-082b: a site file that names the web pod's certificate and tries to turn pod TLS off.
			fs.writeFileSync(site, `otel:\n  endpoint: "https://otel.offline-site.local:4317"\nimageRepository: other.example/ariva\nimageDigests:\n  api-main: "${digestOf('planted')}"\nbuildNumber: planted\n` +
				'podTls:\n  enabled: false\n  secretNames:\n    web: site-web-tls\n');
			const database = path.join(staged, 'database.yaml');
			// ARV-082a: a site file that tries to turn the database's TLS off in prd.
			fs.writeFileSync(database, `persistence:\n  size: 77Gi\nimage:\n  tag: planted\n  digest: "${digestOf('planted')}"\ntls:\n  enabled: false\n`);
			const withSite = offline('prd', ['--state-values-set', `mirror=${mirror}`, '--state-values-set', `siteValues=${site}`, '--state-values-set', `databaseValues=${database}`]);
			if (withSite.status !== 0) findings.push(`-e prd with site values: template failed\n${withSite.stderr}`);
			else {
				if (!withSite.stdout.includes('https://otel.offline-site.local:4317')) findings.push('siteValues did not reach the platform release');
				if (!withSite.stdout.includes('77Gi')) findings.push('databaseValues did not reach the TimescaleDB release');
				const tlsFindings = databaseTlsFindings(parseAsKubernetes(withSite.stdout), { tls: true });
				if (tlsFindings.length) findings.push(`a site databaseValues file turned the TimescaleDB TLS off in prd:\n    ${tlsFindings.join('\n    ')}`);
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
			console.log('PASS offline bundle Helmfile: the same objects as helmfile-k8s in every environment, every image from the mirror by digest, no render without a mirror or with a dry run\'s unpinned images, site values reach their release without moving an image or turning the database TLS or pod TLS off in prd (but name the certificates), prd refused without either TLS');
		}
	}
}

process.exit(failed ? 1 : 0);
