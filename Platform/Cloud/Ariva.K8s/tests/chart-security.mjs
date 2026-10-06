// Chart security test (ARV-002, ARV-062, CWE-269). Renders Charts/platform for every environment and Charts/timescaledb,
// and fails when a workload lacks the pod or container security context, a writable /tmp, a pinned image tag (a digest
// for third-party images), carries a credential in its manifest, a StatefulSet has no NetworkPolicy, or an ingress
// lacks TLS. It also proves the release guards: production without a build number, the "latest" tag, the demo seed in
// production (ARV-019), the MQTT transport without its TLS secret (ARV-024), and a database image without a digest,
// passwords without a secret or the superuser as the migration login (ARV-062) must fail to render.
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs              needs helm 3 on PATH (or HELM=path)
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs --self-test  checks the rules against fixtures, no helm
// Without helm the test is skipped locally and fails in CI (CI=true).
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseAllDocuments } from 'yaml';
import { checkManifests } from './checks.mjs';

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
		'POSTGRES_INITDB_ARGS', 'POSTGRES_HOST_AUTH_METHOD', 'without NOSUPERUSER', 'must set DOTNET_ENVIRONMENT'
	];
	const missing = expected.filter((rule) => !bad.some((finding) => finding.includes(rule)));
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

function render(valuesFile, sets = []) {
	const args = ['template', 'ariva-platform', chart, '-f', path.join(chart, valuesFile), '--set', 'imageCredentials.password='];
	for (const set of sets) args.push('--set', set);
	return spawnSync(helm, args, { encoding: 'utf8', shell: false, maxBuffer: 32 * 1024 * 1024 });
}

function renderDatabase(sets = []) {
	const args = ['template', 'timescaledb', databaseChart];
	for (const set of sets) args.push('--set', set);
	return spawnSync(helm, args, { encoding: 'utf8', shell: false, maxBuffer: 8 * 1024 * 1024 });
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
	const workloads = docs.filter((doc) => doc?.kind === 'Deployment').length;
	const ingresses = docs.filter((doc) => doc?.kind === 'Ingress').length;
	if (findings.length) {
		failed = true;
		console.error(`FAIL ${env.file}:\n  ${findings.join('\n  ')}`);
	} else {
		console.log(`PASS ${env.file}: ${workloads} deployments, ${ingresses} ingresses`);
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
	['MQTT without its TLS secret', 'values-k8s-dev.yaml', ['mqtt.enabled=true', 'mqtt.tlsSecretName=']]
]) {
	const result = render(file, sets);
	if (result.status === 0) {
		failed = true;
		console.error(`FAIL release guard: rendering ${label} must fail but succeeded`);
	} else {
		console.log(`PASS release guard: ${label} is refused`);
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
