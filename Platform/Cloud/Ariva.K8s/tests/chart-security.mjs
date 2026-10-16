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
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs              needs helm 3 on PATH (or HELM=path)
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs --self-test  checks the rules against fixtures, no helm
// Without helm the test is skipped locally and fails in CI (CI=true).
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseAllDocuments } from 'yaml';
import { checkManifests, missingMonitoringRules } from './checks.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const chart = path.resolve(here, '..', 'Helm', 'Charts', 'platform');
const databaseChart = path.resolve(here, '..', 'Helm', 'Charts', 'timescaledb');
const helm = process.env.HELM || 'helm';
const helmfile = process.env.HELMFILE || 'helmfile';
const helmfileState = path.resolve(here, '..', 'Helm', 'helmfile-k8s.yaml.gotmpl');

const parse = (text) => parseAllDocuments(text).map((doc) => doc.toJS()).filter(Boolean);

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
	if (good.length || missing.length) {
		console.error('chart-security self-test FAILED');
		if (good.length) console.error('  good fixture produced findings:\n   ' + good.join('\n   '));
		if (missing.length) console.error('  rules that did not fire on the bad fixture: ' + missing.join(', '));
		process.exit(1);
	}
	console.log(`chart-security self-test: ${expected.length} rules fire on the bad fixture, the good fixture is clean.`);
}

function helmAvailable() {
	return spawnSync(helm, ['version', '--short'], { encoding: 'utf8', shell: false }).status === 0;
}

function render(valuesFile, sets = [], namespace = '') {
	const args = ['template', 'ariva-platform', chart, '-f', path.join(chart, valuesFile), '--set', 'imageCredentials.password='];
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
	for (const key of ['rawSensingDays', 'deskZoneReadingDays', 'laneStaffingDays', 'alertEmailDays', 'integrationCallDays', 'reportDeliveryDays']) {
		const name = `Database__Retention__${key[0].toUpperCase()}${key.slice(1)}`;
		if (expected[key] === undefined) findings.push(`${valuesFile}: migration.retention.${key} has no value`);
		else if (env[name] !== String(expected[key])) findings.push(`Job/database-migration: ${name} is ${env[name]}, not migration.retention.${key} (${expected[key]})`);
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
	const workloads = docs.filter((doc) => doc?.kind === 'Deployment').length;
	const ingresses = docs.filter((doc) => doc?.kind === 'Ingress').length;
	const monitoring = (name) => Object.keys(docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === name)?.data ?? {}).length;
	if (findings.length) {
		failed = true;
		console.error(`FAIL ${env.file}:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS ${env.file}: ${workloads} deployments, ${ingresses} ingresses, validation reader login in api-main and the migration job only, ` +
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
		if (findings.length) {
			failed = true;
			console.error(`FAIL timescaledb:\n  ${findings.join('\n  ')}`);
		} else {
			console.log(`PASS timescaledb: ${docs.filter((doc) => doc?.kind === 'StatefulSet').length} statefulset, ${docs.filter((doc) => doc?.kind === 'NetworkPolicy').length} network policy`);
		}
	}
}

for (const [label, sets] of [
	['a database image without a digest', ['image.digest=']],
	['the latest database image', ['image.tag=latest']],
	['database passwords without a secret', ['existingSecret=']],
	['the superuser as the migration login', ['migrationLogin=postgres']],
	['a migration login that is not an identifier', ['migrationLogin=ariva; drop']]
]) {
	if (renderDatabase(sets).status === 0) {
		failed = true;
		console.error(`FAIL release guard: rendering ${label} must fail but succeeded`);
	} else {
		console.log(`PASS release guard: ${label} is refused`);
	}
}

for (const [label, file, sets] of [
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
	['a certificate namespace label other than namespace or exported_namespace', 'values-k8s-dev.yaml', ['monitoring.alerts.ingressCertificateExpiry.namespaceLabel=job']]
]) {
	const result = render(file, sets);
	if (result.status === 0) {
		failed = true;
		console.error(`FAIL release guard: rendering ${label} must fail but succeeded`);
	} else {
		console.log(`PASS release guard: ${label} is refused`);
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
		if (findings.length) {
			failed = true;
			console.error(`FAIL helmfile -e ${environment}:\n  ${findings.join('\n  ')}`);
		} else {
			console.log(`PASS helmfile -e ${environment}: ${docs.length} objects, database and topics job included`);
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
}

process.exit(failed ? 1 : 0);
