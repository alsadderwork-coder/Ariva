// Chart security test (ARV-002, CWE-269). Renders Charts/platform with helm for every environment and fails when a
// workload lacks the pod or container security context, a writable /tmp, a pinned image tag, or an ingress lacks TLS.
// It also proves the release guards: production without a build number and the "latest" tag must fail to render.
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs              needs helm 3 on PATH (or HELM=path)
//   node Platform/Cloud/Ariva.K8s/tests/chart-security.mjs --self-test  checks the rules against fixtures, no helm
// Without helm the test is skipped locally and fails in CI (CI=true).
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseAllDocuments } from 'yaml';
import { checkManifests } from './checks.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const chart = path.resolve(here, '..', 'Helm', 'Charts', 'platform');
const helm = process.env.HELM || 'helm';

const parse = (text) => parseAllDocuments(text).map((doc) => doc.toJS()).filter(Boolean);

function selfTest() {
	const good = checkManifests(parse(fs.readFileSync(path.join(here, 'fixtures', 'good.yaml'), 'utf8')), { environment: 'k8s-prd' });
	const bad = checkManifests(parse(fs.readFileSync(path.join(here, 'fixtures', 'bad.yaml'), 'utf8')), { environment: 'k8s-prd' });
	const expected = [
		'runAsNonRoot', 'runAsUser', 'seccompProfile', 'hostNetwork', 'allowPrivilegeEscalation', 'privileged',
		'capabilities.drop', 'readOnlyRootFilesystem', '/tmp', 'latest', 'needs a tls section', 'not covered by tls', 'production must pin',
		'token signing key', 'token public keys'
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

for (const [label, file, sets] of [
	['production without a build number', 'values-k8s-prd.yaml', []],
	['production with trunk', 'values-k8s-prd.yaml', ['buildNumber=trunk']],
	['the latest tag', 'values-k8s-dev.yaml', ['buildNumber=latest']]
]) {
	const result = render(file, sets);
	if (result.status === 0) {
		failed = true;
		console.error(`FAIL release guard: rendering ${label} must fail but succeeded`);
	} else {
		console.log(`PASS release guard: ${label} is refused`);
	}
}

process.exit(failed ? 1 : 0);
