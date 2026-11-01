// Base image pinning for every Dockerfile (ARV-002, supply chain), the dev container's included (ARV-076), and the
// third-party images run outside the chart (ARV-096: the local stack, the CI service and job containers, the Azure DevOps E2E
// database, the dynamic scan, the visual baselines' browser and the integration tests' Testcontainers images).
//   node scripts/base-images.mjs --check      every FROM uses a full version tag plus @sha256 digest, every image run
//                                             outside the chart is pinned by tag and digest with one digest per tag, every
//                                             other copy of a third-party pin carries its digest, the third-party watch
//                                             covers every pin file, every .NET restore and npm install in a Dockerfile
//                                             is locked (ARV-214 reads the exec form, a tool named by a path, env and
//                                             VAR=value prefixes, shell wrappers, here-documents, ONBUILD, COPY scripts and
//                                             a guarded tool anywhere in a command; not a script file a RUN runs), the
//                                             pipelines that run the integration tests pin Testcontainers' Ryuk image, and
//                                             CI's SDK steps install and run the images' SDK band (offline; CI gate)
//   node scripts/base-images.mjs --resolve    finds the newest patch of each pinned line and its digest, and the current
//                                             digest of each image run outside the chart (needs registry access and
//                                             docker buildx; runs in .github/workflows/base-images.yml)
//   node scripts/base-images.mjs --resolve --write   also rewrites the FROM lines and every copy of the other pins
//   node scripts/base-images.mjs --resolve --outside [--write]   the images run outside the chart only (a third-party bump;
//                                             moves the tests' copies too, writes nothing while --check finds a problem)
//   node scripts/base-images.mjs --list [--json]   every third-party image pinned outside the Dockerfiles, each with every
//                                             place it is pinned (offline; --json is the matrix of third-party-images.yml)
//   node scripts/base-images.mjs --patches    each pinned third-party line against its registry: a newer patch release, its
//                                             tag moved to a new digest, or a newer TimescaleDB line (a newer minor line
//                                             elsewhere is a note); exits 1 when one is behind (ARV-227, weekly in
//                                             .github/workflows/third-party-images.yml)
// Dependabot (docker ecosystem) keeps the Dockerfile digests current once they are pinned.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
// .private holds client material that nothing lists or reads (owner decision 2026-10-09, ARV-139c).
const SKIP = ['node_modules', '.git', 'bin', 'obj', 'fixtures', 'build', '.svelte-kit', '.private'];

/**
 * The version line each base image follows, and how to recognise a full version tag of it. A line may have several
 * variants (patterns): --resolve keeps each FROM on the variant it uses. The Playwright line follows the version of
 * @playwright/test in Ariva.E2E's package-lock.json rather than the newest tag, since its browsers must match it.
 */
const LINES = {
	'mcr.microsoft.com/dotnet/runtime-deps': { patterns: [/^10\.0\.\d+$/], list: 'mcr' },
	'mcr.microsoft.com/dotnet/aspnet': { patterns: [/^10\.0\.\d+$/], list: 'mcr' },
	// Any feature band (ARV-184): MCR publishes patches of the newest band only (the 1xx tags stopped at 10.0.103, which bundles
	// runtime 10.0.3), and a self-contained publish ships the SDK's own runtime, so the band moves with the runtime.
	'mcr.microsoft.com/dotnet/sdk': { patterns: [/^10\.0\.\d{3}$/], list: 'mcr' },
	nginx: { patterns: [/^1\.30\.\d+-alpine\d+\.\d+$/], list: 'hub', hubName: 'library/nginx', hubFilter: '1.30.' },
	// Alpine for the web image; Debian slim for the dev container (ARV-076), whose glibc base runs its node binary.
	node: { patterns: [/^22\.\d+\.\d+-alpine\d+\.\d+$/, /^22\.\d+\.\d+-bookworm-slim$/], list: 'hub', hubName: 'library/node', hubFilter: '22.' },
	// Dev container (ARV-076): the Docker CLI with Compose, uv, and the Playwright image (also the visual baselines' browser).
	docker: { patterns: [/^29\.\d+\.\d+-cli$/], list: 'hub', hubName: 'library/docker', hubFilter: '29.' },
	'ghcr.io/astral-sh/uv': { patterns: [/^0\.\d+\.\d+$/], list: 'ghcr', ghcrName: 'astral-sh/uv' },
	// Helm for the chart gate in the dev container; keep it at CI's version (ci.yml, azure/setup-helm).
	'alpine/helm': { patterns: [/^3\.\d+\.\d+$/], list: 'hub', hubName: 'alpine/helm', hubFilter: '3.' },
	'mcr.microsoft.com/playwright': { patterns: [/^v1\.\d+\.\d+-noble$/], list: 'mcr', follow: 'playwright' }
};

/** The Playwright image the visual baselines render in must be the dev container's (one digest to bump). */
const VISUAL_BROWSER = path.join(ROOT, 'Platform', 'Testing', 'Ariva.E2E', 'scripts', 'visual-browser.mjs');

/**
 * ARV-096: the third-party images run outside the chart and the Dockerfiles, and the version line each follows. Names are
 * compared without docker.io/ and library/ (redis is docker.io/library/redis). Each pin carries a tag of its line and a digest;
 * an image and tag pinned in several places has one digest everywhere, so a bump moves every copy. TimescaleDB is the chart's
 * tag and digest (Charts/timescaledb/values.yaml) everywhere, the AppHost and the integration tests included; ClamAV is the
 * platform chart's (Charts/platform/values.yaml uploadScan.image, ARV-092c: the scanner beside api-main) everywhere; and smtp4dev
 * the version of the E2E local tool (.config/dotnet-tools.json).
 * The ZAP image is pinned by digest only (the scan's digest predates every current tag) and follows ZAP's stable tag when
 * resolved; tests/zap/zap.spec.ts holds the same digest (checked in check()).
 */
const OUTSIDE_LINES = {
	// retires (ARV-227): the ha image stops moving an older pgN-tsA.B tag once a newer line is out, so --patches reports the newer line.
	'timescale/timescaledb-ha': { patterns: [/^pg17-ts2\.\d+$/], hubName: 'timescale/timescaledb-ha', chart: true, retires: true },
	// redis:8.2-alpine in compose and CI; Aspire's own Redis (8.6) in the AppHost
	redis: { patterns: [/^8\.\d+-alpine$/, /^8\.\d+$/], hubName: 'library/redis' },
	'apache/kafka': { patterns: [/^4\.\d+\.\d+$/], hubName: 'apache/kafka' },
	// Aspire's own Kafka image in the AppHost
	'confluentinc/confluent-local': { patterns: [/^8\.\d+\.\d+$/], hubName: 'confluentinc/confluent-local' },
	'rnwood/smtp4dev': { patterns: [/^3\.\d+\.\d+$/], hubName: 'rnwood/smtp4dev', tool: 'rnwood.smtp4dev' },
	'semgrep/semgrep': { patterns: [/^1\.\d+\.\d+$/], hubName: 'semgrep/semgrep' },
	// The malware scan's clamd (ARV-092b): compose, the AppHost, the E2E steps of ci.yml, security-zap.yml and Azure DevOps,
	// and the integration tests, all on the platform chart's tag and digest (ARV-092c: the chart runs it beside api-main).
	'clamav/clamav': { patterns: [/^1\.\d+\.\d+$/], hubName: 'clamav/clamav', scannerChart: true },
	'ghcr.io/zaproxy/zaproxy': { patterns: [/^$/], ghcrName: 'zaproxy/zaproxy', resolveTag: 'stable' },
	// The integration tests' broker and fault proxy (INTEGRATION_IMAGES); their TimescaleDB and Redis are the lines above.
	'confluentinc/cp-kafka': { patterns: [/^7\.\d+\.\d+$/], hubName: 'confluentinc/cp-kafka' },
	'ghcr.io/shopify/toxiproxy': { patterns: [/^2\.\d+\.\d+$/], ghcrName: 'shopify/toxiproxy' },
	// ARV-214: Testcontainers' resource reaper, which mounts the Docker socket; the pipelines that run the integration tests name it
	// by tag and digest (TESTCONTAINERS_RYUK_CONTAINER_IMAGE, RYUK_RUNNERS) instead of the package's built-in reference.
	'testcontainers/ryuk': { patterns: [/^0\.\d+\.\d+$/], hubName: 'testcontainers/ryuk' }
};

/**
 * ARV-214: the pipeline steps that run the integration tests, each found by a pattern of its file, and the variable each must
 * set to Testcontainers' Ryuk image (read as a pin by outsideImagePins, so it is held to a tag and a digest like the others).
 */
const RYUK_VARIABLE = 'TESTCONTAINERS_RYUK_CONTAINER_IMAGE';
/** A line that runs the integration tests: dotnet on their assembly or project, or the project alone in a task's list. */
const RYUK_RUNNER_LINES = [/\bdotnet\s+(?:test\s+)?\S*Ariva\.IntegrationTests\.(?:dll|csproj)\b/, /^\s*(?:projects:\s*)?['"]?\S*Ariva\.IntegrationTests\.csproj['"]?\s*$/];
/** The files that must hold such a step (each found in every CI file; these two must not lose theirs). */
const RYUK_RUNNERS = ['.github/workflows/ci.yml', 'Platform/Cloud/Ariva.Cicd/AzureDevOps/Common/Analyze-solution.yaml'];

/**
 * AppHost resources allowed on a tag, each with the reason (resource name: reason). None today: TimescaleDB carries the chart's
 * digest since AppHostModelTests reads the digest (Aspire drops the tag once a digest is set and runs image@sha256).
 */
const APPHOST_TAG_ONLY = {};

/**
 * ARV-227: where --check and --resolve --outside look for further copies of a third-party pin (test expectations, the chart tests'
 * fixtures, the scan's ZAP image), with the files they read; the fixtures are read here although the FROM line search skips them.
 */
const COPY_ROOTS = ['Platform', 'scripts', '.github', '.devcontainer'];
const COPY_SKIP = [...SKIP.filter((name) => name !== 'fixtures'), 'test-results', 'playwright-report', 'blob-report'];
const COPY_NAMES = /\.(cs|ya?ml|ts|mjs|js|json|ps1|sh)$|^Dockerfile$/;

/** The integration tests' project, whose container images are C# constants (INTEGRATION_IMAGES). */
const INTEGRATION_TESTS = path.join('Platform', 'Backplane', 'Ariva.IntegrationTests');

/**
 * ARV-096: the integration tests' Testcontainers images, each a C# string constant named by its file and constant, checked as pins
 * like the others (tag and digest, one digest per tag across the repository, the chart's for TimescaleDB). A constant that is
 * renamed or moved is reported, and so is an image the integration tests name by a literal instead (integrationImageLiterals).
 */
const INTEGRATION_IMAGES = [
	[path.join(INTEGRATION_TESTS, 'Setup', 'PostgresFixture.cs'), ['Image']],
	[path.join(INTEGRATION_TESTS, 'Setup', 'KafkaContainers.cs'), ['Image']],
	[path.join(INTEGRATION_TESTS, 'Caching', 'FusionCacheBackplaneTests.cs'), ['Image']],
	[path.join(INTEGRATION_TESTS, 'Faults', 'FaultsFixture.cs'), ['ToxiproxyImage']],
	[path.join(INTEGRATION_TESTS, 'Setup', 'ClamdFixture.cs'), ['Image']]
];

/**
 * Files that pin images run outside the chart, as [repository-relative path, kind, constant names (csharp only)]. The integration
 * tests' files are listed even when missing, so a moved fixture is reported rather than skipped.
 */
function outsideFiles() {
	const files = [['docker-compose.dev.yml', 'yaml'], [path.join('Platform', 'Cloud', 'Ariva.AppHost', 'AppHost.cs'), 'apphost']];
	for (const dir of [path.join('.github', 'workflows'), path.join('Platform', 'Cloud', 'Ariva.Cicd', 'AzureDevOps')]) {
		const walk = (relative) => {
			const full = path.join(ROOT, relative);
			if (!fs.existsSync(full)) return;
			for (const entry of fs.readdirSync(full, { withFileTypes: true })) {
				if (entry.isDirectory()) walk(path.join(relative, entry.name));
				else if (/\.ya?ml$/.test(entry.name)) files.push([path.join(relative, entry.name), 'yaml']);
			}
		};
		walk(dir);
	}
	return [...files.filter(([file]) => fs.existsSync(path.join(ROOT, file))), ...INTEGRATION_IMAGES.map(([file, names]) => [file, 'csharp', names])];
}

/** The pins of every outside file (a missing integration test file reads as empty, so its constants are reported). */
function outsidePins() {
	return outsideFiles().flatMap(([file, kind, names]) => {
		const full = path.join(ROOT, file);
		return outsideImagePins(file.split(path.sep).join('/'), fs.existsSync(full) ? fs.readFileSync(full, 'utf8') : '', kind, names);
	});
}

/** An image reference split into its normalised name, tag and digest (sha256:...). */
function parseImageRef(ref) {
	const [nameTag, digest] = ref.split('@');
	const lastSlash = nameTag.lastIndexOf('/');
	const colon = nameTag.indexOf(':', lastSlash + 1);
	const name = (colon === -1 ? nameTag : nameTag.slice(0, colon)).replace(/^docker\.io\//, '').replace(/^library\//, '');
	return { image: name, tag: colon === -1 ? '' : nameTag.slice(colon + 1), digest: digest ?? '' };
}

/** The image of a docker run command line: the first argument that is neither an option nor an option's value. */
function dockerRunImage(command) {
	const valued = new Set(['--name', '-p', '--publish', '-e', '--env', '--env-file', '-v', '--volume', '--mount', '--network', '--net', '-w', '--workdir', '-u', '--user', '--platform', '--entrypoint', '-h', '--hostname', '-l', '--label', '--add-host', '--health-cmd', '--health-interval', '--health-timeout', '--health-retries', '--memory', '-m', '--cpus', '--shm-size', '--tmpfs', '--ulimit', '--pull', '--restart', '--cap-add', '--cap-drop', '--security-opt', '--device', '--log-driver', '--log-opt', '--expose', '--ip']);
	const tokens = command.match(/"[^"]*"|'[^']*'|\S+/g) ?? [];
	for (let i = 0; i < tokens.length; i++) {
		if (tokens[i].startsWith('-')) {
			if (valued.has(tokens[i])) i++;
			continue;
		}
		return tokens[i].replace(/^["']|["']$/g, '');
	}
	return null;
}

/**
 * The image pins in one file, as { where, ref, image, tag, digest, resource }. YAML: image: and container: values (a matrix
 * value without a registry, tag or digest is a name, not an image) and the image of each docker run line; values that are
 * variables or expressions are skipped (ZAP_IMAGE is read as a pin itself). AppHost: each container resource, its image and
 * tag from AddContainer or WithImage and its digest from WithImageSHA256; an integration left on its package's default image
 * is reported with an empty image. C# (the integration tests): the string constant of each given name (const string Name = "...");
 * a name with no such constant is reported as missing.
 */
export function outsideImagePins(file, text, kind, names = []) {
	const pins = [];
	const push = (line, ref, resource) => pins.push({ where: `${file}:${line}`, ref, resource, ...parseImageRef(ref) });
	if (kind === 'csharp') {
		for (const name of names) {
			const constant = new RegExp(`\\bconst\\s+string\\s+${name}\\s*=\\s*"([^"]*)"\\s*;`).exec(text);
			if (constant) push(text.slice(0, constant.index).split('\n').length, constant[1]);
			else pins.push({ where: file, ref: name, missing: name, image: '', tag: '', digest: '' });
		}
		return pins;
	}
	if (kind === 'yaml') {
		text.split(/\r?\n/).forEach((raw, index) => {
			const line = raw.replace(/\s#.*$/, '');
			const value = line.match(/^\s*(?:-\s+)?(?:image|container|[A-Z][A-Z0-9_]*_IMAGE):\s*["']?([^\s"']+)["']?\s*$/)?.[1];
			if (value && !/[$}{]/.test(value) && /[/:@]/.test(value)) push(index + 1, value);
			const run = line.match(/\bdocker\s+run\s+(.*)$/)?.[1];
			const image = run ? dockerRunImage(run) : null;
			if (image && !/[$}{]/.test(image)) push(index + 1, image);
		});
		return pins;
	}
	const notContainers = /^(Project|Parameter|ViteApp|JavaScriptApp|NpmApp|NodeApp|CSharpApp|Executable|ConnectionString|Database)\b/;
	let offset = 0;
	for (const statement of text.split(';')) {
		const line = text.slice(0, offset).split('\n').length;
		offset += statement.length + 1;
		for (const add of statement.matchAll(/\bbuilder\.Add(\w+)(?:<[^>]*>)?\(\s*"([^"]+)"/g)) {
			if (notContainers.test(add[1])) continue;
			const container = statement.match(/\bAddContainer\(\s*"[^"]+"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"/);
			const withImage = [...statement.matchAll(/\.WithImage\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\)/g)].at(-1);
			const sha = [...statement.matchAll(/\.WithImageSHA256\(\s*"([^"]+)"\s*\)/g)].at(-1)?.[1];
			const [image, tag] = withImage ? [withImage[1], withImage[2]] : container ? [container[1], container[2]] : ['', ''];
			const where = line + statement.slice(0, add.index).split('\n').length - 1;
			pins.push({ where: `${file}:${where}`, ref: image ? `${image}:${tag}${sha ? `@sha256:${sha}` : ''}` : `Add${add[1]}`, resource: add[2], ...parseImageRef(image ? `${image}:${tag}` : ''), digest: sha ? `sha256:${sha}` : '' });
		}
	}
	return pins;
}

/**
 * Problems for the pins of every outside file. chart: { tag, digest } of the TimescaleDB chart; scanner: { tag, digest } of the
 * platform chart's ClamAV (ARV-092c); tools: the local tool manifest's versions by package id; tagOnly: AppHost resources allowed
 * on a tag (APPHOST_TAG_ONLY).
 */
export function outsideImageProblems(pins, { chart, scanner, tools, tagOnly = {} }) {
	const problems = [];
	const digests = new Map();
	const tagsOfDigest = new Map();
	for (const pin of pins) {
		if (pin.missing) {
			problems.push(`${pin.where}: no constant ${pin.missing} names an image here; INTEGRATION_IMAGES in scripts/base-images.mjs lists the integration tests' image constants`);
			continue;
		}
		if (!pin.image) {
			problems.push(`${pin.where}: ${pin.resource} runs its integration's default image; name it with WithImage and pin it with WithImageSHA256`);
			continue;
		}
		const line = OUTSIDE_LINES[pin.image] ?? LINES[pin.image];
		if (!line) {
			problems.push(`${pin.where}: ${pin.image} is not a known image line (add it to OUTSIDE_LINES in scripts/base-images.mjs)`);
			continue;
		}
		if (!line.patterns.some((pattern) => pattern.test(pin.tag))) problems.push(`${pin.where}: ${pin.ref} must use a tag matching ${line.patterns.join(' or ')}`);
		const allowed = pin.resource && tagOnly[pin.resource];
		if (!/^sha256:[0-9a-f]{64}$/.test(pin.digest) && !allowed) problems.push(`${pin.where}: ${pin.ref} must be pinned by digest (@sha256:..., or WithImageSHA256 in the AppHost)`);
		if (line.chart && chart) {
			if (pin.tag !== chart.tag) problems.push(`${pin.where}: ${pin.ref} must use the chart's tag ${chart.tag} (Charts/timescaledb/values.yaml)`);
			if (pin.digest !== chart.digest && !allowed) problems.push(`${pin.where}: ${pin.ref} must use the chart's digest ${chart.digest}`);
		}
		if (line.scannerChart && scanner) {
			if (pin.tag !== scanner.tag) problems.push(`${pin.where}: ${pin.ref} must use the chart's tag ${scanner.tag} (Charts/platform/values.yaml uploadScan.image)`);
			if (pin.digest !== scanner.digest && !allowed) problems.push(`${pin.where}: ${pin.ref} must use the chart's digest ${scanner.digest} (Charts/platform/values.yaml uploadScan.image)`);
		}
		if (line.tool && tools?.[line.tool] !== undefined && pin.tag !== tools[line.tool]) problems.push(`${pin.where}: ${pin.ref} must be ${tools[line.tool]}, the version of the local tool ${line.tool} (.config/dotnet-tools.json)`);
		if (pin.digest) {
			const key = `${pin.image}:${pin.tag}`;
			if (!digests.has(key)) digests.set(key, new Map());
			digests.get(key).set(pin.digest, [...(digests.get(key).get(pin.digest) ?? []), pin.where]);
			// ARV-227: a tag changed in some copies only keeps the old digest under the new tag; --resolve --outside would then move
			// the copies still on the old tag to the new tag's digest.
			const image = `${pin.image}@${pin.digest}`;
			if (!tagsOfDigest.has(image)) tagsOfDigest.set(image, new Set());
			tagsOfDigest.get(image).add(pin.tag || 'untagged');
		}
	}
	for (const [key, byDigest] of digests) {
		if (byDigest.size > 1) problems.push(`${key} is pinned to ${byDigest.size} digests; pin one everywhere (${[...byDigest].map(([digest, where]) => `${digest.slice(7, 19)} in ${where.join(', ')}`).join('; ')})`);
	}
	for (const [key, tags] of tagsOfDigest) {
		if (tags.size > 1) problems.push(`${key.slice(0, key.indexOf('@'))} has one digest (${key.slice(key.indexOf('@') + 8, key.indexOf('@') + 20)}) under ${tags.size} tags (${[...tags].join(', ')}); change the tag in every place --list names`);
	}
	return problems;
}

/**
 * ARV-096: problems for one C# file of the integration tests that names a container image by a string literal (WithImage("...")
 * or new XBuilder("...") of a Testcontainers builder) rather than through a constant of INTEGRATION_IMAGES, which would pin it
 * a second time out of the check's sight. An interpolated reference (the chart's own image, read from its values) is not a literal.
 */
export function integrationImageLiterals(file, text) {
	const problems = [];
	for (const literal of text.matchAll(/(?:\.WithImage|\bnew\s+(?:Container|PostgreSql|Redis|Kafka|Toxiproxy)Builder)\(\s*"([^"]*)"/g)) {
		problems.push(`${file}:${text.slice(0, literal.index).split('\n').length}: the image "${literal[1]}" is a literal; name it by one of the pinned constants (INTEGRATION_IMAGES in scripts/base-images.mjs)`);
	}
	return problems;
}

/**
 * ARV-227: every third-party image pinned outside Ariva's Dockerfiles, one entry per image, tag and digest, with each place it
 * is pinned: the outside files' pins (compose, the AppHost, the workflows, Azure DevOps, the integration tests' constants) and the
 * charts' TimescaleDB and ClamAV. A pin without a digest is left out (the check reports it). name is image:tag, ref what a
 * scanner pulls (image:tag@digest), where the places, comma separated.
 */
export function pinnedImages(pins, { chart, scanner } = {}) {
	const all = [...pins, ...chartPins(chart, scanner)];
	const images = new Map();
	for (const pin of all.filter((p) => p.image && /^sha256:[0-9a-f]{64}$/.test(p.digest ?? ''))) {
		const name = pin.tag ? `${pin.image}:${pin.tag}` : pin.image;
		const key = `${name}@${pin.digest}`;
		if (!images.has(key)) images.set(key, { image: pin.image, tag: pin.tag, digest: pin.digest, name, ref: key, places: [] });
		images.get(key).places.push(pin.where);
	}
	return [...images.values()].sort((a, b) => a.ref.localeCompare(b.ref)).map(({ places, ...image }) => ({ ...image, where: places.join(', ') }));
}

/** ARV-227: the charts' TimescaleDB (chart: tag, digest) and ClamAV (scanner: repository, tag, digest) as pins. */
function chartPins(chart, scanner) {
	return [
		...(chart?.tag && chart?.digest ? [{ where: 'Platform/Cloud/Ariva.K8s/Helm/Charts/timescaledb/values.yaml', image: 'timescale/timescaledb-ha', tag: chart.tag, digest: chart.digest }] : []),
		...(scanner?.repository && scanner?.digest ? [{ where: 'Platform/Cloud/Ariva.K8s/Helm/Charts/platform/values.yaml', image: scanner.repository, tag: scanner.tag, digest: scanner.digest }] : [])
	];
}

/** ARV-227: the newest tag on the patch line of a version tag x.y.z (same x.y, a higher z), or null; other tags have no patch line. */
export function newerPatch(tag, tags) {
	const version = (value) => /^(\d+)\.(\d+)\.(\d+)$/.exec(value ?? '')?.slice(1).map(Number);
	const pinned = version(tag);
	if (!pinned) return null;
	let best = null;
	for (const candidate of tags) {
		const found = version(candidate);
		if (!found || found[0] !== pinned[0] || found[1] !== pinned[1] || found[2] <= pinned[2]) continue;
		if (!best || found[2] > best[1]) best = [candidate, found[2]];
	}
	return best?.[0] ?? null;
}

/**
 * ARV-227: a tag's line: x.y.z, x.y and x.y-variant on major x, pgN-tsA.B on PostgreSQL N and TimescaleDB A. prefix is what the
 * registry is asked for, family the line's kin (same major and variant), minor its place among them; null for other tags (ZAP's
 * stable).
 */
function tagLine(tag) {
	let m = /^(\d+)\.(\d+)\.(\d+)$/.exec(tag ?? '');
	if (m) return { prefix: `${m[1]}.`, family: `${m[1]}.x.z`, minor: Number(m[2]), patch: Number(m[3]) };
	m = /^(\d+)\.(\d+)(-[a-z]+)?$/.exec(tag ?? '');
	if (m) return { prefix: `${m[1]}.`, family: `${m[1]}.x${m[3] ?? ''}`, minor: Number(m[2]), patch: 0 };
	m = /^pg(\d+)-ts(\d+)\.(\d+)$/.exec(tag ?? '');
	if (m) return { prefix: `pg${m[1]}-ts${m[2]}.`, family: `pg${m[1]}-ts${m[2]}.x`, minor: Number(m[3]), patch: 0 };
	return null;
}

/** ARV-227: the newest tag of a newer line of the same family (1.4.6 to 1.5.2, 8.2-alpine to 8.6-alpine, pg17-ts2.30 to pg17-ts2.31), or null. */
export function newerLine(tag, tags) {
	const pinned = tagLine(tag);
	if (!pinned) return null;
	let best = null;
	for (const candidate of tags) {
		const found = tagLine(candidate);
		if (!found || found.family !== pinned.family || found.minor <= pinned.minor) continue;
		if (!best || found.minor > best.minor || (found.minor === best.minor && found.patch > best.patch)) best = { ...found, tag: candidate };
	}
	return best?.tag ?? null;
}

/**
 * ARV-227: one pinned line's status from what its registry says: tags (the tags on its major) and digest (what its tag, ZAP's
 * stable, names now). BEHIND for a newer patch release, a tag that names another digest, or a newer line where lines retire
 * (retires); a newer line elsewhere is a note on an ok line; ERROR when the registry names no sha256 digest.
 */
export function patchStatus(image, line, { tags, digest }) {
	const tag = line.resolveTag ?? image.tag;
	if (!/^sha256:[0-9a-f]{64}$/.test(digest ?? '')) return { status: 'ERROR', notes: [`the registry named no sha256 digest for ${tag}`] };
	const behind = [];
	const notes = [];
	const patch = newerPatch(image.tag, tags);
	if (patch) behind.push(`newer patch release ${patch}`);
	if (digest !== image.digest) behind.push(`${tag} now names ${digest}`);
	const newer = newerLine(image.tag, tags);
	if (newer && line.retires) behind.push(`newer line ${newer} (${image.tag} stops moving once a newer line is out)`);
	else if (newer) notes.push(`note: newer line ${newer}`);
	return { status: behind.length ? 'BEHIND' : 'ok', notes: [...behind, ...notes] };
}

/**
 * ARV-227: the copies of third-party pins in one file: image references with a digest (image[:tag]@sha256:...) whose normalised
 * image is one of images (a Set). Test expectations, the chart tests' fixtures and the scan's ZAP image hold such copies besides
 * the pins themselves.
 */
export function imageCopies(file, text, images) {
	const copies = [];
	for (const m of text.matchAll(/(?<![\w./-])((?:[a-z0-9][a-z0-9.-]*\/)*[a-z0-9][a-z0-9._-]*)(?::(\w[\w.-]{0,127}))?@(sha256:[0-9a-f]{64})/g)) {
		const { image } = parseImageRef(m[1]);
		if (images.has(image)) copies.push({ where: `${file}:${text.slice(0, m.index).split('\n').length}`, image, tag: m[2] ?? '', digest: m[3] });
	}
	return copies;
}

/**
 * ARV-227: problems for copies of third-party pins: a tagged copy carries the digest pinned for its image and tag, an untagged one
 * a digest pinned for its image. A bump that misses a copy fails --check, and --resolve --outside refuses to write.
 */
export function copyProblems(copies, pins) {
	const problems = [];
	for (const copy of copies) {
		const ofImage = pins.filter((pin) => pin.image === copy.image && pin.digest);
		if (copy.tag) {
			const same = ofImage.filter((pin) => pin.tag === copy.tag);
			if (!same.length) problems.push(`${copy.where}: ${copy.image}:${copy.tag} is a copy of no pin (${copy.image} is pinned on ${[...new Set(ofImage.map((pin) => pin.tag || 'no tag'))].join(', ')}); change its tag with the pin's`);
			else if (!same.some((pin) => pin.digest === copy.digest)) problems.push(`${copy.where}: ${copy.image}:${copy.tag}@${copy.digest.slice(7, 19)} must carry the pin's digest ${same[0].digest} (node scripts/base-images.mjs --resolve --outside --write moves every copy)`);
		} else if (!ofImage.some((pin) => pin.digest === copy.digest)) {
			problems.push(`${copy.where}: ${copy.image}@${copy.digest.slice(7, 19)} carries no digest pinned for ${copy.image} (node scripts/base-images.mjs --resolve --outside --write moves every copy)`);
		}
	}
	return problems;
}

/** ARV-227: a GitHub Actions paths filter as a regular expression (** any characters, * and ? any but a slash). */
export function pathFilter(glob) {
	const source = glob.split('**').map((part) => part.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '[^/]*').replace(/\?/g, '[^/]')).join('.*');
	return new RegExp(`^${source}$`);
}

/** ARV-227: the globs of each paths: block list in a workflow, in order. */
export function workflowPaths(text) {
	const lines = text.split(/\r?\n/);
	const filters = [];
	lines.forEach((line, index) => {
		const head = /^(\s*)paths:\s*$/.exec(line);
		if (!head) return;
		const globs = [];
		for (let i = index + 1; i < lines.length; i++) {
			const item = /^(\s*)-\s*['"]?([^'"#\s]+)['"]?\s*(?:#.*)?$/.exec(lines[i]);
			if (!item || item[1].length <= head[1].length) break;
			globs.push(item[2]);
		}
		filters.push(globs);
	});
	return filters;
}

/**
 * ARV-227: problems when the watch's triggers do not cover the files that pin third-party images: its pull_request and push
 * filters (two at least) must each match every such file, so a change to a pin is scanned before it lands and when it reaches main.
 */
export function watchPathProblems(files, filters) {
	const problems = [];
	if (filters.length < 2) problems.push('third-party-images.yml: its pull_request and push triggers each need a paths filter over the files that pin third-party images');
	for (const globs of filters) {
		const regexes = globs.map(pathFilter);
		for (const file of files) if (!regexes.some((regex) => regex.test(file))) problems.push(`third-party-images.yml: a paths filter misses ${file}, which pins a third-party image; add it so a change to the pin is scanned`);
	}
	return [...new Set(problems)];
}

function selfTestThirdPartyImages() {
	const d = (c) => `sha256:${c.repeat(64)}`;
	const failures = [];
	const tags = ['1.4.3', '1.4.6', '1.4.5', '1.4.6_base', '1.5.0', '1.4.10-rc1', 'stable', 'latest', '1.3.9'];
	for (const [tag, expected] of [['1.4.3', '1.4.6'], ['1.4.6', null], ['1.5.0', null], ['8.2-alpine', null], ['pg17-ts2.30', null], ['', null], ['1.3.2', '1.3.9']]) {
		const found = newerPatch(tag, tags);
		if (found !== expected) failures.push(`newerPatch(${tag || 'no tag'}): expected ${expected}, got ${found}`);
	}
	if (newerPatch('2.9.0', ['2.10.0', '2.9.10', '2.9.9']) !== '2.9.10') failures.push('newerPatch compares patches as numbers, not text');
	const pins = [
		{ where: 'docker-compose.dev.yml:4', image: 'redis', tag: '8.2-alpine', digest: d('b') },
		{ where: '.github/workflows/ci.yml:30', image: 'redis', tag: '8.2-alpine', digest: d('b') },
		{ where: 'ci.yml:40', image: 'ghcr.io/zaproxy/zaproxy', tag: '', digest: d('e') },
		{ where: 'x.yml:1', image: 'redis', tag: '8.2-alpine', digest: '' },
		{ where: 'AppHost.cs:9', resource: 'kafka', image: '', tag: '', digest: '' }
	];
	const listed = pinnedImages(pins, { chart: { tag: 'pg17-ts2.30', digest: d('a') }, scanner: { repository: 'clamav/clamav', tag: '1.4.6', digest: d('5') } });
	const expected = [
		{ name: 'clamav/clamav:1.4.6', ref: `clamav/clamav:1.4.6@${d('5')}`, where: 'Platform/Cloud/Ariva.K8s/Helm/Charts/platform/values.yaml' },
		{ name: 'ghcr.io/zaproxy/zaproxy', ref: `ghcr.io/zaproxy/zaproxy@${d('e')}`, where: 'ci.yml:40' },
		{ name: 'redis:8.2-alpine', ref: `redis:8.2-alpine@${d('b')}`, where: 'docker-compose.dev.yml:4, .github/workflows/ci.yml:30' },
		{ name: 'timescale/timescaledb-ha:pg17-ts2.30', ref: `timescale/timescaledb-ha:pg17-ts2.30@${d('a')}`, where: 'Platform/Cloud/Ariva.K8s/Helm/Charts/timescaledb/values.yaml' }
	];
	const shown = JSON.stringify(listed.map(({ name, ref, where }) => ({ name, ref, where })));
	if (shown !== JSON.stringify(expected)) failures.push(`pinnedImages: expected one entry per image, tag and digest with every place, got ${shown}`);
	// Newer lines: the same major and variant, a higher minor; moving and other tags.
	const lineTags = ['1.4.7', '1.5.0', '1.5.2', '1.5.10-rc1', '2.0.0', '8.2-alpine', '8.4-alpine', '8.6-alpine', '8.6', '8.6-alpine3.22', '8.8.1-alpine', 'pg17-ts2.30', 'pg17-ts2.31', 'pg18-ts2.31', 'pg17-ts2.31-all'];
	for (const [tag, expected] of [['1.4.6', '1.5.2'], ['1.5.2', null], ['8.2-alpine', '8.6-alpine'], ['8.4', '8.6'], ['pg17-ts2.30', 'pg17-ts2.31'], ['pg17-ts2.31', null], ['', null]]) {
		const found = newerLine(tag, lineTags);
		if (found !== expected) failures.push(`newerLine(${tag || 'no tag'}): expected ${expected}, got ${found}`);
	}
	// A line's status: a moved digest and a newer patch are BEHIND, a newer line a note unless the line retires, no digest an ERROR.
	const status = (image, line, answer) => { const r = patchStatus(image, line, answer); return `${r.status} ${r.notes.join('; ')}`; };
	const clam = { image: 'clamav/clamav', tag: '1.4.6', digest: d('5') };
	const cases = [
		[clam, {}, { tags: ['1.4.6', '1.5.2'], digest: d('5') }, `ok note: newer line 1.5.2`],
		[clam, {}, { tags: ['1.4.6'], digest: d('5') }, 'ok '],
		[clam, {}, { tags: ['1.4.7'], digest: d('5') }, 'BEHIND newer patch release 1.4.7'],
		[{ image: 'redis', tag: '8.2-alpine', digest: d('b') }, {}, { tags: [], digest: d('c') }, `BEHIND 8.2-alpine now names ${d('c')}`],
		[{ image: 'timescale/timescaledb-ha', tag: 'pg17-ts2.30', digest: d('a') }, { retires: true }, { tags: ['pg17-ts2.31'], digest: d('a') }, 'BEHIND newer line pg17-ts2.31 (pg17-ts2.30 stops moving once a newer line is out)'],
		[{ image: 'ghcr.io/zaproxy/zaproxy', tag: '', digest: d('e') }, { resolveTag: 'stable' }, { tags: [], digest: d('e') }, 'ok '],
		[clam, {}, { tags: [], digest: undefined }, 'ERROR the registry named no sha256 digest for 1.4.6'],
		[clam, {}, { tags: [], digest: 'sha256:<b>x</b>' }, 'ERROR the registry named no sha256 digest for 1.4.6']
	];
	for (const [image, line, answer, expected] of cases) {
		const found = status(image, line, answer);
		if (found !== expected) failures.push(`patchStatus(${image.image}:${image.tag}): expected "${expected}", got "${found}"`);
	}
	// Copies: found by image (docker.io/ and library/ dropped, another registry's image left alone) and held to the pins.
	const known = new Set(['clamav/clamav', 'redis', 'ghcr.io/zaproxy/zaproxy']);
	const text = `a: "docker.io/clamav/clamav@${d('5')}"\nb: clamav/clamav:1.4.6@${d('5')}\nc: myregistry.example/clamav/clamav@${d('6')}\nd: docker.io/library/redis:8.2-alpine@${d('b')}\ne: ghcr.io/zaproxy/zaproxy@${d('e')}\nf: postgres:17@${d('9')}\n`;
	const copies = imageCopies('t.yaml', text, known);
	const copied = copies.map((c) => `${c.where} ${c.image}:${c.tag}@${c.digest.slice(7, 8)}`).join(', ');
	if (copied !== 't.yaml:1 clamav/clamav:@5, t.yaml:2 clamav/clamav:1.4.6@5, t.yaml:4 redis:8.2-alpine@b, t.yaml:5 ghcr.io/zaproxy/zaproxy:@e') failures.push(`imageCopies: got ${copied}`);
	const pinsNow = [{ image: 'clamav/clamav', tag: '1.4.6', digest: d('5') }, { image: 'redis', tag: '8.2-alpine', digest: d('b') }, { image: 'ghcr.io/zaproxy/zaproxy', tag: '', digest: d('e') }];
	if (copyProblems(copies, pinsNow).length) failures.push(`copyProblems: copies of the pins reported: ${copyProblems(copies, pinsNow).join('; ')}`);
	const bumped = [{ image: 'clamav/clamav', tag: '1.4.7', digest: d('7') }, ...pinsNow.slice(1)];
	const stale = copyProblems(copies, bumped);
	if (stale.length !== 2 || !stale[0].startsWith('t.yaml:1:') || !stale[1].startsWith('t.yaml:2: clamav/clamav:1.4.6 is a copy of no pin')) failures.push(`copyProblems: a missed copy of a bumped pin not reported as expected: ${stale.join('; ')}`);
	const moved = copyProblems(copies, [{ image: 'clamav/clamav', tag: '1.4.6', digest: d('7') }, ...pinsNow.slice(1)]);
	if (moved.length !== 2 || !moved[1].includes("must carry the pin's digest")) failures.push(`copyProblems: copies left on an old digest not reported: ${moved.join('; ')}`);
	// One digest under two tags of one image (a tag changed in some places only) is refused.
	const twoTags = outsideImageProblems([
		{ where: 'a.yml:1', image: 'apache/kafka', tag: '4.1.2', digest: d('1'), ref: 'x' },
		{ where: 'b.yml:1', image: 'apache/kafka', tag: '4.1.0', digest: d('1'), ref: 'x' }
	], {});
	if (!twoTags.some((problem) => problem.startsWith('apache/kafka has one digest (111111111111) under 2 tags (4.1.2, 4.1.0)'))) failures.push(`outsideImageProblems: one digest under two tags not reported: ${twoTags.join('; ')}`);
	// The watch's paths filters: GitHub's globs, every pin file covered by each trigger.
	for (const [glob, file, expected] of [['.github/workflows/**', '.github/workflows/ci.yml', true], ['Platform/Cloud/Ariva.K8s/Helm/Charts/*/values.yaml', 'Platform/Cloud/Ariva.K8s/Helm/Charts/platform/values.yaml', true], ['Platform/Cloud/Ariva.K8s/Helm/Charts/*/values.yaml', 'Platform/Cloud/Ariva.K8s/Helm/Charts/platform/sub/values.yaml', false], ['docker-compose.dev.yml', 'docker-compose_dev.yml', false]]) {
		if (pathFilter(glob).test(file) !== expected) failures.push(`pathFilter(${glob}) on ${file}: expected ${expected}`);
	}
	const watch = `on:\n  pull_request:\n    paths:\n      - 'a/**'\n      - "b.yml" # pins\n  push:\n    branches: [main]\n    paths:\n      - 'a/**'\njobs:\n`;
	if (JSON.stringify(workflowPaths(watch)) !== JSON.stringify([['a/**', 'b.yml'], ['a/**']])) failures.push(`workflowPaths: got ${JSON.stringify(workflowPaths(watch))}`);
	const missed = watchPathProblems(['a/x.yml', 'b.yml'], workflowPaths(watch));
	if (missed.length !== 1 || !missed[0].includes('misses b.yml')) failures.push(`watchPathProblems: expected b.yml missed by the push filter, got ${missed.join('; ')}`);
	if (!watchPathProblems(['a/x.yml'], [['a/**']]).some((problem) => problem.includes('each need a paths filter'))) failures.push('watchPathProblems: a single filter accepted');
	// Docker Hub's next page is followed on hub.docker.com only.
	if (hubNextPage(null) !== null || hubNextPage(undefined) !== null) failures.push('hubNextPage: no next page must end the listing');
	if (hubNextPage('https://hub.docker.com/v2/repositories/library/redis/tags?page=2') !== 'https://hub.docker.com/v2/repositories/library/redis/tags?page=2') failures.push('hubNextPage: a Docker Hub page refused');
	for (const next of ['http://hub.docker.com/v2/x', 'https://hub.docker.com.example/v2/x', 'https://169.254.169.254/latest', 'https://user@evil.example/hub.docker.com']) {
		try {
			hubNextPage(next);
			failures.push(`hubNextPage: ${next} followed`);
		} catch {
			// Refused, as it should be.
		}
	}
	if (failures.length) {
		console.error(`base images: third-party images self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

/** The integration tests' C# files (bin and obj skipped), repository-relative. */
function integrationSources(relative = INTEGRATION_TESTS, out = []) {
	const full = path.join(ROOT, relative);
	if (!fs.existsSync(full)) return out;
	for (const entry of fs.readdirSync(full, { withFileTypes: true })) {
		if (SKIP.includes(entry.name)) continue;
		if (entry.isDirectory()) integrationSources(path.join(relative, entry.name), out);
		else if (entry.name.endsWith('.cs')) out.push(path.join(relative, entry.name));
	}
	return out;
}

function selfTestOutsideImages() {
	const d = (c) => `sha256:${c.repeat(64)}`;
	const chart = { tag: 'pg17-ts2.30', digest: d('a') };
	const scanner = { tag: '1.4.3', digest: d('5') };
	const tools = { 'rnwood.smtp4dev': '3.15.0' };
	const compose = `services:\n  db:\n    image: timescale/timescaledb-ha:pg17-ts2.30@${d('a')}\n  redis:\n    image: redis:8.2-alpine@${d('b')} # comment\n  mail:\n    image: rnwood/smtp4dev:3.15.0@${d('c')}\n  clamav:\n    image: clamav/clamav:1.4.3@${d('5')}\n`;
	const workflow = `jobs:\n  build:\n    strategy:\n      matrix:\n        include:\n          - image: api-main\n    container:\n      image: semgrep/semgrep:1.178.0@${d('d')}\n    services:\n      redis:\n        image: redis:8.2-alpine@${d('b')}\n    env:\n      ZAP_IMAGE: ghcr.io/zaproxy/zaproxy@${d('e')}\n    steps:\n      - run: docker run --rm -v "$PWD/x:/y" "$ZAP_IMAGE" zap-baseline.py\n      - run: docker run -d --name db -p 5433:5432 -e P="x-$(id)" timescale/timescaledb-ha:pg17-ts2.30@${d('a')}\n`;
	const apphost = `var db = builder.AddPostgres("timescaledb", p)\n    .WithImage("timescale/timescaledb-ha", "pg17-ts2.30")\n    .WithImageSHA256("${'a'.repeat(64)}");\nvar kafka = builder.AddKafka("kafka", 1)\n    .WithImage("confluentinc/confluent-local", "8.2.0")\n    .WithImageSHA256("${'f'.repeat(64)}");\nvar smtp = builder.AddContainer("smtp4dev", "rnwood/smtp4dev", "3.15.0")\n    .WithImageSHA256("${'c'.repeat(64)}");\nvar p = builder.AddParameter("x", "y");\nbuilder.AddProject<Projects.X>("x");\n`;
	// The integration tests' constants (INTEGRATION_IMAGES): TimescaleDB on the chart's digest, Redis on compose's, two images of their own.
	const postgres = `public sealed class PostgresFixture\n{\n    /// <summary>The chart's image.</summary>\n    public const string Image = "timescale/timescaledb-ha:pg17-ts2.30@${d('a')}";\n}\n`;
	const kafkaFixture = `public static class KafkaContainers\n{\n    public const string Image = "confluentinc/cp-kafka:7.6.1@${d('3')}";\n}\n`;
	const redisFixture = `public sealed class RedisFixture\n{\n    public const string Image = "redis:8.2-alpine@${d('b')}";\n}\n`;
	const faults = `public sealed class FaultsFixture\n{\n    public const string ToxiproxyImage = "ghcr.io/shopify/toxiproxy:2.12.0@${d('4')}";\n\n    private void Build() => new RedisBuilder().WithImage(RedisFixture.Image).Build();\n}\n`;
	const csharp = [['PostgresFixture.cs', postgres, 'csharp', ['Image']], ['KafkaContainers.cs', kafkaFixture, 'csharp', ['Image']], ['Redis.cs', redisFixture, 'csharp', ['Image']],
		['FaultsFixture.cs', faults, 'csharp', ['ToxiproxyImage']]];
	const good = [...outsideImagePins('compose', compose, 'yaml'), ...outsideImagePins('ci', workflow, 'yaml'), ...outsideImagePins('apphost', apphost, 'apphost'),
		...csharp.flatMap(([file, text, kind, names]) => outsideImagePins(file, text, kind, names))];
	const failures = [];
	const goodProblems = outsideImageProblems(good, { chart, scanner, tools, tagOnly: APPHOST_TAG_ONLY });
	if (goodProblems.length) failures.push(`good: expected clean, got ${goodProblems.join('; ')}`);
	if (good.length !== 15) failures.push(`good: expected 15 pins, got ${good.length} (${good.map((pin) => pin.ref).join(', ')})`);
	for (const [file, text] of csharp) {
		const literals = integrationImageLiterals(file, text);
		if (literals.length) failures.push(`good: expected no image literal in ${file}, got ${literals.join('; ')}`);
	}
	const bad = (name, files, expected) => {
		const pins = files.flatMap(([file, text, kind, names]) => outsideImagePins(file, text, kind, names));
		const found = outsideImageProblems(pins, { chart, scanner, tools, tagOnly: APPHOST_TAG_ONLY }).join('\n');
		for (const fragment of expected) if (!found.includes(fragment)) failures.push(`${name}: did not report "${fragment}" (got: ${found || 'nothing'})`);
	};
	bad('no digest in compose', [['compose', compose.replace(`redis:8.2-alpine@${d('b')}`, 'redis:8.2-alpine'), 'yaml']], ['redis:8.2-alpine must be pinned by digest']);
	bad('a floating tag', [['compose', compose.replace('redis:8.2-alpine', 'redis:latest'), 'yaml']], ['redis:latest@', 'must use a tag matching']);
	bad('two digests for one tag', [['compose', compose, 'yaml'], ['ci', workflow.replace(`redis:8.2-alpine@${d('b')}`, `redis:8.2-alpine@${d('9')}`), 'yaml']], ['redis:8.2-alpine is pinned to 2 digests']);
	bad('an unknown image', [['ci', `jobs:\n  x:\n    container:\n      image: someone/tool:1.0@${d('1')}\n`, 'yaml']], ['someone/tool is not a known image line']);
	bad('TimescaleDB off the chart', [['compose', compose.replace(`pg17-ts2.30@${d('a')}`, `pg17-ts2.31@${d('2')}`), 'yaml']], ["must use the chart's tag pg17-ts2.30", "must use the chart's digest"]);
	// ARV-092c: every clamd outside the chart (compose, the AppHost, CI, the integration tests) runs the chart's ClamAV.
	bad('ClamAV off the chart', [['compose', compose.replace(`clamav/clamav:1.4.3@${d('5')}`, `clamav/clamav:1.4.4@${d('6')}`), 'yaml']],
		["clamav/clamav:1.4.4@sha256:6666666666666666666666666666666666666666666666666666666666666666 must use the chart's tag 1.4.3 (Charts/platform/values.yaml uploadScan.image)", "must use the chart's digest sha256:5555"]);
	bad('ClamAV on the chart\'s tag with another digest', [['ci', `jobs:\n  e2e:\n    steps:\n      - run: docker run -d --name e2e-clamd clamav/clamav:1.4.3@${d('7')}\n`, 'yaml']],
		["must use the chart's digest sha256:5555"]);
	bad('docker run without a digest', [['ado', '      - script: |\n          docker run -d --name db -p 5433:5432 -e P="x" timescale/timescaledb-ha:pg17-ts2.30\n', 'yaml']], ['timescale/timescaledb-ha:pg17-ts2.30 must be pinned by digest']);
	bad('smtp4dev off the local tool', [['compose', compose.replace('rnwood/smtp4dev:3.15.0', 'rnwood/smtp4dev:3.16.0'), 'yaml']], ['must be 3.15.0, the version of the local tool rnwood.smtp4dev']);
	bad('AppHost integration on its default image', [['apphost', 'var redis = builder.AddRedis("redis", 1, password);\n', 'apphost']], ['redis runs its integration\'s default image']);
	bad('AppHost container without a digest', [['apphost', 'var smtp = builder.AddContainer("smtp4dev", "rnwood/smtp4dev", "3.15.0")\n    .WithHttpEndpoint(port: 1);\n', 'apphost']], ['rnwood/smtp4dev:3.15.0 must be pinned by digest']);
	bad('AppHost TimescaleDB on the tag only', [['apphost', apphost.replace(`\n    .WithImageSHA256("${'a'.repeat(64)}");\nvar kafka`, ';\nvar kafka'), 'apphost']],
		['timescale/timescaledb-ha:pg17-ts2.30 must be pinned by digest', "timescale/timescaledb-ha:pg17-ts2.30 must use the chart's digest"]);
	bad('AppHost TimescaleDB off the chart', [['apphost', apphost.replace(`.WithImageSHA256("${'a'.repeat(64)}")`, `.WithImageSHA256("${'2'.repeat(64)}")`), 'apphost']], ["must use the chart's digest"]);
	bad('an integration image on a tag only', [['Redis.cs', redisFixture.replace(`@${d('b')}`, ''), 'csharp', ['Image']]], ['Redis.cs:3: redis:8.2-alpine must be pinned by digest']);
	bad('an integration image on another digest than compose', [['compose', compose, 'yaml'], ['Redis.cs', redisFixture.replace(d('b'), d('9')), 'csharp', ['Image']]], ['redis:8.2-alpine is pinned to 2 digests']);
	bad('the integration TimescaleDB off the chart', [['PostgresFixture.cs', postgres.replace(d('a'), d('2')), 'csharp', ['Image']]], ["must use the chart's digest"]);
	bad('an integration constant renamed', [['KafkaContainers.cs', kafkaFixture.replace('string Image', 'string BrokerImage'), 'csharp', ['Image']]], ['KafkaContainers.cs: no constant Image names an image here']);
	bad('an integration fixture missing', [['Gone.cs', '', 'csharp', ['ToxiproxyImage']]], ['Gone.cs: no constant ToxiproxyImage names an image here']);
	bad('an unknown integration image', [['KafkaContainers.cs', kafkaFixture.replace('confluentinc/cp-kafka:7.6.1', 'bitnami/kafka:3.9.0'), 'csharp', ['Image']]], ['bitnami/kafka is not a known image line']);
	for (const [name, text, expected] of [
		['an image literal in WithImage', faults.replace('.WithImage(RedisFixture.Image)', '.WithImage("redis:8.2-alpine")'), 'FaultsFixture.cs:5: the image "redis:8.2-alpine" is a literal'],
		['an image literal in a builder', faults.replace('new RedisBuilder()', 'new ContainerBuilder("nginx:1.30.0-alpine3.24")'), 'the image "nginx:1.30.0-alpine3.24" is a literal']
	]) {
		const found = integrationImageLiterals('FaultsFixture.cs', text).join('\n');
		if (!found.includes(expected)) failures.push(`${name}: did not report "${expected}" (got: ${found || 'nothing'})`);
	}
	if (integrationImageLiterals('ChartDatabase.cs', 'var container = new ContainerBuilder($"{repository}@{digest}");').length) failures.push('an interpolated chart image: reported as a literal');
	if (failures.length) {
		console.error(`base images: outside images self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

/**
 * ARV-214: the YAML step (a list item and the lines indented under it) that holds line index, as [first, end) line indexes.
 * A step starts at the nearest list item above at an indent no deeper than the line's and ends at the next line indented no
 * deeper than that item.
 */
function yamlStep(lines, index) {
	let start = index;
	while (start >= 0 && !(/^\s*-\s/.test(lines[start]) && lines[start].search(/\S/) <= lines[index].search(/\S/))) start--;
	if (start < 0) return [index, index + 1];
	const indent = lines[start].search(/\S/);
	let end = start + 1;
	while (end < lines.length && (!lines[end].trim() || lines[end].search(/\S/) > indent)) end++;
	return [start, end];
}

/**
 * ARV-214: the value of a variable set directly in a YAML step's own env mapping (the key at the step's key column, the variable
 * one level under it), or undefined when the step sets none there (a key under inputs: or with:, or a job-level env, does
 * not count: only the step's env reaches its process).
 */
function stepEnvValue(step, name) {
	const keyColumn = step[0].match(/^\s*-\s+/)?.[0].length;
	if (keyColumn === undefined) return undefined;
	const at = step.findIndex((line, index) => (index === 0 ? /^\s*-\s+env:\s*(#.*)?$/ : /^\s*env:\s*(#.*)?$/).test(line) && (index === 0 || line.search(/\S/) === keyColumn));
	if (at < 0) return undefined;
	const children = [];
	for (let i = at + 1; i < step.length; i++) {
		if (!step[i].trim() || /^\s*#/.test(step[i])) continue;
		if (step[i].search(/\S/) <= keyColumn) break;
		children.push(step[i]);
	}
	const childColumn = children[0]?.search(/\S/);
	const line = children.find((child) => child.search(/\S/) === childColumn && child.trim().startsWith(`${name}:`));
	return line?.replace(/\s#.*$/, '').trim().slice(name.length + 1).trim().replace(/^(["'])(.*)\1$/, '$2');
}

/**
 * ARV-214: whether a value is a literal image reference by tag and digest (name/path:tag@sha256:64 hex), with nothing a CI
 * system would expand ($, braces, parentheses) or white space; read in linear time by splitting it, not by one pattern.
 */
function literalImageReference(value) {
	if (/[\s${}()]/.test(value)) return false;
	const at = value.indexOf('@');
	if (at < 0 || !/^sha256:[0-9a-f]{64}$/.test(value.slice(at + 1))) return false;
	const name = value.slice(0, at);
	const slash = name.lastIndexOf('/');
	const colon = name.lastIndexOf(':');
	return slash > 0 && colon > slash + 1 && colon < name.length - 1;
}

/**
 * ARV-214: problems for the CI files ([file, text]) that run the integration tests: every step that runs them (a line of
 * RYUK_RUNNER_LINES) sets TESTCONTAINERS_RYUK_CONTAINER_IMAGE in its own env to a literal image reference by tag and digest
 * (no GitHub expression, no Azure DevOps macro), and each file of RYUK_RUNNERS has such a step (a step renamed or moved is
 * reported, not passed). The value's line, tag and digest are checked with the other pins.
 */
export function ryukProblems(files) {
	const problems = [];
	for (const required of RYUK_RUNNERS) {
		const entry = files.find(([file]) => file === required);
		if (!entry || entry[1] === null) problems.push(`${required}: missing; it runs the integration tests, whose Ryuk image it must pin (${RYUK_VARIABLE})`);
	}
	for (const [file, text] of files) {
		if (text === null) continue;
		const lines = text.split(/\r?\n/);
		const runs = lines.map((line, index) => (RYUK_RUNNER_LINES.some((pattern) => pattern.test(line.replace(/\s#.*$/, ''))) ? index : -1)).filter((index) => index >= 0);
		if (!runs.length && RYUK_RUNNERS.includes(file)) problems.push(`${file}: no step runs the integration tests; RYUK_RUNNER_LINES in scripts/base-images.mjs finds the step that must pin Testcontainers' Ryuk image`);
		for (const index of runs) {
			const [start, end] = yamlStep(lines, index);
			const value = stepEnvValue(lines.slice(start, end), RYUK_VARIABLE);
			if (value === undefined) problems.push(`${file}:${index + 1}: the step that runs the integration tests must set ${RYUK_VARIABLE} in its own env (not under inputs:, with: or the job) to Testcontainers' Ryuk image by tag and digest`);
			else if (!literalImageReference(value)) problems.push(`${file}:${index + 1}: ${RYUK_VARIABLE} must be a literal image reference by tag and digest (no expression or macro), got ${value}`);
		}
	}
	return problems;
}

/** ARV-214: the feature band of an SDK version (10.0.402 is 10.0.4xx), or null when it is not one. */
export function sdkBand(version) {
	const match = /^(\d+)\.(\d+)\.(\d)\d\d$/.exec(version ?? '');
	return match ? `${match[1]}.${match[2]}.${match[3]}xx` : null;
}

/**
 * ARV-214 (no global.json, decision recorded in docs/product/decisions.md): CI builds and tests with an SDK of the feature band
 * the images build with. Problems for the SDK versions of every Dockerfile (sdkTags) and the CI files ([file, text]): the
 * Dockerfiles use one band; each actions/setup-dotnet step names that band (10.0.4xx) or one of those versions in dotnet-version,
 * and reads no global-json-file; each Azure DevOps UseDotNet task, which takes no band, names the SDK's major (10.x) or major
 * and minor (10.0.x), or one of those versions.
 */
export function sdkPinProblems(sdkTags, files) {
	const problems = [];
	const bands = [...new Set(sdkTags.map(sdkBand))];
	if (!sdkTags.length || bands.includes(null) || bands.length !== 1) {
		problems.push(`the Dockerfiles must build with SDKs of one feature band (found ${sdkTags.join(', ') || 'none'})`);
		return problems;
	}
	const band = bands[0];
	const [major, minor] = band.split('.');
	const bandPattern = band.replace('xx', '[0-9][0-9]');
	for (const [file, text] of files) {
		const lines = text.split(/\r?\n/);
		lines.forEach((line, index) => {
			const setup = /\buses:\s*["']?actions\/setup-dotnet@/.test(line.replace(/\s#.*$/, ''));
			const useDotNet = /^\s*(?:-\s*)?task:\s*UseDotNet@/.test(line);
			if (!setup && !useDotNet) return;
			const [start, end] = yamlStep(lines, index);
			const step = lines.slice(start, end);
			const field = (name) => {
				const at = step.findIndex((entry) => new RegExp(`^\\s*(?:-\\s+)?${name}:`).test(entry));
				if (at < 0) return undefined;
				const value = step[at].replace(/\s#.*$/, '').replace(new RegExp(`^\\s*(?:-\\s+)?${name}:\\s*`), '').replace(/^["']|["']$/g, '').trim();
				return value === '|' || value === '>' || value === '' ? null : value;
			};
			const where = `${file}:${index + 1}`;
			if (setup) {
				if (field('global-json-file') !== undefined) problems.push(`${where}: setup-dotnet must not read a global-json-file (the repository has none: ARV-214); name dotnet-version: '${band}'`);
				const version = field('dotnet-version');
				if (version === undefined || version === null) problems.push(`${where}: setup-dotnet must name one dotnet-version, the images' SDK band '${band}'`);
				else if (version !== band && !sdkTags.includes(version)) problems.push(`${where}: setup-dotnet's dotnet-version ${version} must be the images' SDK band '${band}' (or ${[...new Set(sdkTags)].join(', ')})`);
				// The band pin holds only if the job then runs that SDK: the very next step of the same job (so before any step
				// that runs dotnet) writes a temporary global.json from the version setup-dotnet installed, after checking it is in
				// the band, and proves dotnet --version is it; it runs whenever the setup does (the same if:, no continue-on-error).
				const id = field('id');
				const dash = step[0].search(/\S/);
				let next = end;
				while (next < lines.length && (!lines[next].trim() || /^\s*#/.test(lines[next]))) next++;
				const holdStep = next < lines.length && /^\s*-\s/.test(lines[next]) && lines[next].search(/\S/) === dash ? lines.slice(...yamlStep(lines, next)) : null;
				// Only the step's own lines count, comments left out; the band check, the write and the proof must be in its run
				// script. The file is written at the repository root and the proof runs there, whatever working directory the job
				// or the step sets, so every dotnet in the job (one under the root included) finds it.
				const holdLines = (holdStep ?? []).filter((entry) => !/^\s*#/.test(entry));
				const runAt = holdLines.findIndex((entry) => /^\s*(?:-\s+)?run:/.test(entry));
				// A trailing shell comment holds nothing that runs: it is left out too (fail closed: a # inside quotes cuts the line).
				const holdRun = runAt < 0 ? '' : holdLines.slice(runAt).map((entry) => entry.replace(/\s#.*$/, '')).join('\n');
				const ifOf = (entries) => entries.map((entry) => entry.match(/^\s*(?:-\s+)?if:\s*(.*?)\s*$/)?.[1]).find((value) => value !== undefined) ?? null;
				const proofAt = holdRun.indexOf('cd "$GITHUB_WORKSPACE"');
				const hold = id && holdStep && holdLines.some((entry) => entry.includes(`steps.${id}.outputs.dotnet-version`)) && holdRun.includes(bandPattern) && holdRun.includes('> "$GITHUB_WORKSPACE/global.json"') && proofAt >= 0 && holdRun.indexOf('dotnet --version', proofAt) > proofAt;
				if (!hold) problems.push(`${where}: setup-dotnet needs an id, and its next step must check steps.<id>.outputs.dotnet-version is in ${bandPattern}, write it to "$GITHUB_WORKSPACE/global.json" and prove dotnet --version runs it after cd "$GITHUB_WORKSPACE", in its run script (otherwise the newest SDK on the runner runs)`);
				else if (holdStep.some((entry) => /^\s*(?:-\s+)?continue-on-error:/.test(entry))) problems.push(`${where}: the step that holds the job to the SDK must not continue on error`);
				else if (ifOf(holdStep) !== ifOf(step)) problems.push(`${where}: the step that holds the job to the SDK must run whenever setup-dotnet does (the same if:)`);
			} else {
				const version = field('version');
				const allowed = [`${major}.x`, `${major}.${minor}.x`, ...sdkTags];
				if (!allowed.includes(version)) problems.push(`${where}: UseDotNet's version ${version ?? 'missing'} must be ${allowed.join(', ')} (UseDotNet@2 takes no feature band)`);
			}
		});
		// A GitHub job that runs dotnet without setup-dotnet runs whatever SDK the runner has.
		if (file.startsWith('.github/workflows/')) {
			for (const job of workflowJobs(lines)) {
				const runsDotnet = job.lines.some((entry) => !/^\s*#/.test(entry) && /(^|[\s;&|(])dotnet\s+(?:build|test|restore|run|publish|tool|pack|format|msbuild|list|\S+\.dll)\b/.test(entry));
				const setsUp = job.lines.some((entry) => /\buses:\s*["']?actions\/setup-dotnet@/.test(entry));
				if (runsDotnet && !setsUp) problems.push(`${file}: job ${job.name} runs dotnet without an actions/setup-dotnet step (the runner's own SDK would run)`);
			}
		}
	}
	return problems;
}

/** The jobs of a GitHub workflow, as { name, lines }: each key two spaces under jobs:, with the lines indented under it. */
function workflowJobs(lines) {
	const jobs = [];
	const start = lines.findIndex((line) => /^jobs:\s*(#.*)?$/.test(line));
	if (start < 0) return jobs;
	for (let i = start + 1; i < lines.length; i++) {
		if (/^\S/.test(lines[i]) && lines[i].trim() && !/^#/.test(lines[i])) break;
		const name = lines[i].match(/^ {2}([A-Za-z0-9_-]+):\s*(#.*)?$/)?.[1];
		if (name) jobs.push({ name, lines: [] });
		else if (jobs.length) jobs.at(-1).lines.push(lines[i]);
	}
	return jobs;
}

/**
 * ARV-214: the local .NET tool manifests (each pins its tools by exact version; these two must stay, and any other
 * dotnet-tools.json in the repository is checked too, toolManifests) and the AppHost project with the Aspire SDK.
 */
const TOOL_MANIFESTS = ['.config/dotnet-tools.json', 'Platform/Backplane/Ariva.UnitTests/.config/dotnet-tools.json'];
export function toolManifests(root = ROOT, required = TOOL_MANIFESTS) {
	const out = new Set(required);
	const walk = (dir) => {
		for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
			if (SKIP.includes(entry.name) || (entry.isDirectory() && entry.name.startsWith('.') && entry.name !== '.config')) continue;
			const full = path.join(dir, entry.name);
			if (entry.isDirectory()) walk(full);
			else if (entry.name === 'dotnet-tools.json') out.add(path.relative(root, full).split(path.sep).join('/'));
		}
	};
	walk(root);
	return [...out].sort();
}
const APPHOST_PROJECT = 'Platform/Cloud/Ariva.AppHost/Ariva.AppHost.csproj';

/**
 * ARV-214: the tooling NuGet resolves by version alone, without a lock file. Problems for the tool manifests ([file, text or
 * null]), the AppHost project's text and Directory.Packages.props' text: every tool names an exact version (no range, no
 * wildcard) with rollForward false; the AppHost's MSBuild SDK (Aspire.AppHost.Sdk/x.y.z) is an exact version equal to every
 * Aspire.Hosting package's version and to the aspire.cli tool's, so one Aspire release is in use. Where they come from and
 * that their content is what nuget.org signed is NuGet's (nuget.config, package signatures; wiki 13 section 9).
 */
export function toolPinProblems(manifests, appHost, packagesProps) {
	const problems = [];
	const exact = /^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/;
	const tools = new Map();
	for (const [file, text] of manifests) {
		if (text === null) {
			problems.push(`${file}: missing (TOOL_MANIFESTS in scripts/base-images.mjs lists the tool manifests)`);
			continue;
		}
		let manifest;
		try {
			manifest = JSON.parse(text);
		} catch {
			problems.push(`${file}: not JSON`);
			continue;
		}
		for (const [id, tool] of Object.entries(manifest.tools ?? {})) {
			tools.set(id, tool.version);
			if (!exact.test(tool.version ?? '')) problems.push(`${file}: ${id} must name an exact version (got ${tool.version ?? 'none'})`);
			if (tool.rollForward !== false) problems.push(`${file}: ${id} must set rollForward to false`);
		}
	}
	const sdk = appHost?.match(/<Project\s+Sdk="Aspire\.AppHost\.Sdk\/([^"]+)"/)?.[1];
	if (!sdk || !exact.test(sdk)) problems.push(`${APPHOST_PROJECT}: the Aspire SDK must be named with an exact version (Aspire.AppHost.Sdk/x.y.z; got ${sdk ?? 'none'})`);
	else {
		for (const [, id, version] of (packagesProps ?? '').matchAll(/<PackageVersion\s+Include="(Aspire\.Hosting[^"]*)"\s+Version="([^"]+)"/g)) {
			if (version !== sdk) problems.push(`Directory.Packages.props: ${id} ${version} must be the Aspire SDK's version ${sdk} (${APPHOST_PROJECT})`);
		}
		if (tools.has('aspire.cli') && tools.get('aspire.cli') !== sdk) problems.push(`.config/dotnet-tools.json: aspire.cli ${tools.get('aspire.cli')} must be the Aspire SDK's version ${sdk}`);
	}
	return problems;
}

function selfTestPipelinePins() {
	const failures = [];
	const ryuk = 'testcontainers/ryuk:0.14.0@sha256:' + 'a'.repeat(64);
	const ci = (env) => `jobs:\n  backend:\n    steps:\n      - name: Integration tests\n        id: integration\n${env}        run: timeout 27m dotnet Platform/Backplane/Ariva.IntegrationTests/bin/Release/net10.0/Ariva.IntegrationTests.dll > integration.log\n      - name: Count guard\n        env:\n          ${RYUK_VARIABLE}: ${ryuk}\n        run: node scripts/test-count-guard.mjs --dll Platform/Backplane/Ariva.IntegrationTests/bin/Release/net10.0/Ariva.IntegrationTests.dll\n`;
	const ado = (env, inputs = '') => `steps:\n      - task: DotNetCoreCLI@2\n        displayName: 'Dotnet Test'\n${env}        inputs:\n          command: 'test'\n${inputs}          projects: |\n            Platform/Backplane/Ariva.UnitTests/Ariva.UnitTests.csproj\n            Platform/Backplane/Ariva.IntegrationTests/Ariva.IntegrationTests.csproj\n`;
	const env = (value = ryuk) => `        env:\n          OTHER: 1\n          ${RYUK_VARIABLE}: ${value}\n`;
	const runners = (ciText, adoText, more = []) => [[RYUK_RUNNERS[0], ciText], [RYUK_RUNNERS[1], adoText], ...more];
	for (const [name, files, expected] of [
		['both set', runners(ci(env()), ado(env())), []],
		['quoted value', runners(ci(env(`'${ryuk}'`)), ado(env(`"${ryuk}"`))), []],
		['ci sets it in the count guard step only', runners(ci(''), ado(env())), [`.github/workflows/ci.yml:6: the step that runs the integration tests must set ${RYUK_VARIABLE} in its own env`]],
		['set at the job level only', runners(ci('').replace('    steps:\n', `    env:\n      ${RYUK_VARIABLE}: ${ryuk}\n    steps:\n`), ado(env())), ['ci.yml:8: the step that runs the integration tests must set']],
		['azure devops without it', runners(ci(env()), ado('')), ['Analyze-solution.yaml:8: the step that runs the integration tests must set']],
		['azure devops under inputs', runners(ci(env()), ado('', `          ${RYUK_VARIABLE}: ${ryuk}\n`)), ['Analyze-solution.yaml:9: the step that runs the integration tests must set']],
		['github under with', runners(ci(`        with:\n          ${RYUK_VARIABLE}: ${ryuk}\n`), ado(env())), ['ci.yml:8: the step that runs the integration tests must set']],
		['nested under another env key', runners(ci(`        env:\n          OTHER:\n            ${RYUK_VARIABLE}: ${ryuk}\n`), ado(env())), ['ci.yml:9: the step that runs the integration tests must set']],
		['a github expression', runners(ci(env('${{ env.RYUK }}')), ado(env())), [`ci.yml:9: ${RYUK_VARIABLE} must be a literal image reference by tag and digest (no expression or macro)`]],
		['an azure devops macro', runners(ci(env()), ado(env('$(ryukImage)'))), [`Analyze-solution.yaml:11: ${RYUK_VARIABLE} must be a literal image reference`]],
		['by tag only', runners(ci(env('testcontainers/ryuk:0.14.0')), ado(env())), ['must be a literal image reference by tag and digest']],
		['a commented value does not count', runners(ci(`        env:\n          # ${RYUK_VARIABLE}: ${ryuk}\n`), ado(env())), ['the step that runs the integration tests must set']],
		['an env mapping nested under inputs', runners(ci(env()), ado('', `          env:\n            ${RYUK_VARIABLE}: ${ryuk}\n`)), ['Analyze-solution.yaml:10: the step that runs the integration tests must set']],
		['no slash in the name', runners(ci(env('ryuk:0.14.0@sha256:' + 'a'.repeat(64))), ado(env())), ['must be a literal image reference by tag and digest']],
		['a registry macro in front', runners(ci(env()), ado(env('$(containerRegistry)/testcontainers/ryuk:0.14.0@sha256:' + 'a'.repeat(64)))), ['must be a literal image reference by tag and digest']],
		['a registry variable in front', runners(ci(env('$REG/testcontainers/ryuk:0.14.0@sha256:' + 'a'.repeat(64))), ado(env())), ['must be a literal image reference by tag and digest']],
		['parentheses in the name', runners(ci(env('(x)/testcontainers/ryuk:0.14.0@sha256:' + 'a'.repeat(64))), ado(env())), ['must be a literal image reference by tag and digest']],
		['braces in the name', runners(ci(env('{x}/testcontainers/ryuk:0.14.0@sha256:' + 'a'.repeat(64))), ado(env())), ['must be a literal image reference by tag and digest']],
		['no tag', runners(ci(env('testcontainers/ryuk@sha256:' + 'a'.repeat(64))), ado(env())), ['must be a literal image reference by tag and digest']],
		['a registry with a port', runners(ci(env('registry.example:5000/testcontainers/ryuk:0.14.0@sha256:' + 'a'.repeat(64))), ado(env())), []],
		['a short digest', runners(ci(env('testcontainers/ryuk:0.14.0@sha256:' + 'a'.repeat(63))), ado(env())), ['must be a literal image reference by tag and digest']],
		['another workflow runs the suite', runners(ci(env()), ado(env()), [['.github/workflows/nightly.yml', 'jobs:\n  n:\n    steps:\n      - run: dotnet test Platform/Backplane/Ariva.IntegrationTests/Ariva.IntegrationTests.csproj\n']]), ['nightly.yml:4: the step that runs the integration tests must set']],
		['the runner step gone', runners(ci(env()).replace(/dotnet Platform\S+IntegrationTests\.dll/, 'true'), ado(env())), ['.github/workflows/ci.yml: no step runs the integration tests']],
		['a file gone', runners(ci(env()), null), ['Analyze-solution.yaml: missing']]
	]) {
		const found = ryukProblems(files).join('\n');
		if (!expected.length && found) failures.push(`ryuk ${name}: expected clean, got ${found}`);
		for (const fragment of expected) if (!found.includes(fragment)) failures.push(`ryuk ${name}: did not report "${fragment}" (got: ${found || 'nothing'})`);
	}
	const holdStep = (id = 'sdk', pattern = '10.0.4[0-9][0-9]', write = '> "$GITHUB_WORKSPACE/global.json"') => `      - name: Hold\n        env:\n          SDK: \${{ steps.${id}.outputs.dotnet-version }}\n        run: |\n          case "$SDK" in ${pattern}) ;; *) exit 1 ;; esac\n          printf '{}' ${write}\n          cd "$GITHUB_WORKSPACE"\n          test "$(dotnet --version)" = "$SDK"\n`;
	{
		// The value is read in linear time: a long value without a digest is refused at once.
		const started = performance.now();
		const long = ryukProblems(runners(ci(env(`a/${'b:'.repeat(20000)}c`)), ado(env())));
		if (!long.some((problem) => problem.includes('must be a literal image reference')) || performance.now() - started > 1000) failures.push('ryuk a 40,000 character value: not refused within a second');
	}
	const setup = (version, hold = holdStep(), id = '        id: sdk\n') => `jobs:\n  build:\n    steps:\n      - uses: actions/setup-dotnet@a98b # v6.0.0\n${id}        with:\n${version}${hold}      - run: dotnet build\n`;
	const useDotNet = (version) => `steps:\n      - task: UseDotNet@2\n        inputs:\n          packageType: 'sdk'\n${version}`;
	for (const [name, tags, files, expected] of [
		['band and major', ['10.0.402', '10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n")], ['a.yaml', useDotNet("          version: '10.x'\n")]], []],
		['an exact version', ['10.0.402'], [['ci.yml', setup('          dotnet-version: 10.0.402\n')], ['a.yaml', useDotNet('          version: 10.0.402\n')]], []],
		['a floating version', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.x'\n")]], ["ci.yml:4: setup-dotnet's dotnet-version 10.0.x must be the images' SDK band '10.0.4xx'"]],
		['another band', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.1xx'\n")]], ["dotnet-version 10.0.1xx must be the images' SDK band"]],
		['no version', ['10.0.402'], [['ci.yml', setup('          cache: true\n')]], ['setup-dotnet must name one dotnet-version']],
		['a version list', ['10.0.402'], [['ci.yml', setup('          dotnet-version: |\n            8.0.x\n            10.0.4xx\n')]], ['setup-dotnet must name one dotnet-version']],
		['a global.json', ['10.0.402'], [['ci.yml', setup("          global-json-file: global.json\n          dotnet-version: '10.0.4xx'\n")]], ['setup-dotnet must not read a global-json-file']],
		['setup on the item line', ['10.0.402'], [['ci.yml', "jobs:\n  b:\n    steps:\n      - name: SDK\n        uses: actions/setup-dotnet@a98b\n        with:\n          dotnet-version: '9.0.x'\n"]], ['ci.yml:5: setup-dotnet\'s dotnet-version 9.0.x']],
		['no hold step', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", '')]], ['ci.yml:4: setup-dotnet needs an id, and its next step must']],
		['no id', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep(), '')]], ['setup-dotnet needs an id, and its next step must']],
		['a hold step reading another id', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep('other'))]], ['setup-dotnet needs an id, and its next step must']],
		['a hold step on another band', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep('sdk', '10.0.1[0-9][0-9]'))]], ['setup-dotnet needs an id, and its next step must']],
		['a hold step that writes no global.json', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep('sdk', '10.0.4[0-9][0-9]', '> /dev/null'))]], ['setup-dotnet needs an id, and its next step must']],
		['a hold step that proves nothing', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep().replace('          test "$(dotnet --version)" = "$SDK"\n', ''))]], ['setup-dotnet needs an id, and its next step must']],
		['a relative global.json under a job working directory', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep('sdk', '10.0.4[0-9][0-9]', '> global.json')).replace('    steps:\n', '    defaults:\n      run:\n        working-directory: Platform/Testing/Ariva.E2E\n    steps:\n')]], ['write it to "$GITHUB_WORKSPACE/global.json"']],
		['the proof outside the repository root', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep().replace('          cd "$GITHUB_WORKSPACE"\n', ''))]], ['prove dotnet --version runs it after cd "$GITHUB_WORKSPACE"']],
		['the proof before the cd', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep().replace('          cd "$GITHUB_WORKSPACE"\n          test "$(dotnet --version)" = "$SDK"\n', '          test "$(dotnet --version)" = "$SDK"\n          cd "$GITHUB_WORKSPACE"\n'))]], ['setup-dotnet needs an id, and its next step must']],
		['the hold only in comments', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", `      - name: Hold\n        env:\n          SDK: \${{ steps.sdk.outputs.dotnet-version }}\n        # case "$SDK" in 10.0.4[0-9][0-9]) > "$GITHUB_WORKSPACE/global.json" cd "$GITHUB_WORKSPACE" dotnet --version\n        run: echo skipped\n`)]], ['setup-dotnet needs an id, and its next step must']],
		['the hold in comments inside its run script', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", `      - name: Hold\n        env:\n          SDK: \${{ steps.sdk.outputs.dotnet-version }}\n        run: |\n          echo skipped\n          # case "$SDK" in 10.0.4[0-9][0-9]) > "$GITHUB_WORKSPACE/global.json"\n          # cd "$GITHUB_WORKSPACE"; dotnet --version\n`)]], ['setup-dotnet needs an id, and its next step must']],
		['the outputs reference only in a comment', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep().replace('          SDK: ${{ steps.sdk.outputs.dotnet-version }}\n', '          # SDK: ${{ steps.sdk.outputs.dotnet-version }}\n          SDK: 10.0.402\n'))]], ['setup-dotnet needs an id, and its next step must']],
		['the hold in trailing shell comments', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", `      - name: Hold\n        env:\n          SDK: \${{ steps.sdk.outputs.dotnet-version }}\n        run: |\n          true # 10.0.4[0-9][0-9] > "$GITHUB_WORKSPACE/global.json" cd "$GITHUB_WORKSPACE" dotnet --version\n`)]], ['setup-dotnet needs an id, and its next step must']],
		['the hold outside its run script', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", `      - name: Hold 10.0.4[0-9][0-9] > "$GITHUB_WORKSPACE/global.json" cd "$GITHUB_WORKSPACE" dotnet --version\n        env:\n          SDK: \${{ steps.sdk.outputs.dotnet-version }}\n        run: echo skipped\n`)]], ['setup-dotnet needs an id, and its next step must']],
		['only another job holds', ['10.0.402'], [['ci.yml', `${setup("          dotnet-version: '10.0.4xx'\n", '')}  e2e:\n    steps:\n${setup("          dotnet-version: '10.0.4xx'\n").split('    steps:\n')[1]}`]], ['ci.yml:4: setup-dotnet needs an id, and its next step must']],
		['the hold after a dotnet step', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", `      - run: dotnet restore Ariva.slnx\n${holdStep()}`)]], ['setup-dotnet needs an id, and its next step must']],
		['a comment between them', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", `      # holds the job\n\n${holdStep()}`)]], []],
		['a hold step that continues on error', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep().replace('        env:\n', '        continue-on-error: true\n        env:\n'))]], ['the step that holds the job to the SDK must not continue on error']],
		['a hold step with its own if', ['10.0.402'], [['ci.yml', setup("          dotnet-version: '10.0.4xx'\n", holdStep().replace('        env:\n', "        if: github.event_name == 'push'\n        env:\n"))]], ['must run whenever setup-dotnet does (the same if:)']],
		['the same if on both', ['10.0.402'], [['ci.yml', "jobs:\n  a:\n    steps:\n      - if: matrix.language == 'csharp'\n        uses: actions/setup-dotnet@a98b\n        id: sdk\n        with:\n          dotnet-version: '10.0.4xx'\n" + holdStep().replace('        env:\n', "        if: matrix.language == 'csharp'\n        env:\n")]], []],
		['an if on the setup only', ['10.0.402'], [['ci.yml', "jobs:\n  a:\n    steps:\n      - if: matrix.language == 'csharp'\n        uses: actions/setup-dotnet@a98b\n        id: sdk\n        with:\n          dotnet-version: '10.0.4xx'\n" + holdStep()]], ['must run whenever setup-dotnet does (the same if:)']],
		['a job runs dotnet with no setup', ['10.0.402'], [['.github/workflows/x.yml', `${setup("          dotnet-version: '10.0.4xx'\n")}  other:\n    steps:\n      - run: dotnet test Ariva.slnx\n`]], ['.github/workflows/x.yml: job other runs dotnet without an actions/setup-dotnet step']],
		['a job runs a test assembly with no setup', ['10.0.402'], [['.github/workflows/x.yml', 'jobs:\n  t:\n    steps:\n      - run: timeout 5m dotnet Tests/bin/Release/net10.0/Tests.dll\n']], ['job t runs dotnet without an actions/setup-dotnet step']],
		['a job that only names dotnet in a comment', ['10.0.402'], [['.github/workflows/x.yml', 'jobs:\n  t:\n    steps:\n      # dotnet test would go here\n      - run: echo ok\n']], []],
		['use dotnet on another major', ['10.0.402'], [['a.yaml', useDotNet("          version: '11.x'\n")]], ["UseDotNet's version 11.x must be 10.x, 10.0.x, 10.0.402"]],
		['images on two bands', ['10.0.402', '10.0.110'], [], ['the Dockerfiles must build with SDKs of one feature band']],
		['no SDK image', [], [], ['the Dockerfiles must build with SDKs of one feature band (found none)']]
	]) {
		const found = sdkPinProblems(tags, files).join('\n');
		if (!expected.length && found) failures.push(`sdk ${name}: expected clean, got ${found}`);
		for (const fragment of expected) if (!found.includes(fragment)) failures.push(`sdk ${name}: did not report "${fragment}" (got: ${found || 'nothing'})`);
	}
	const manifest = (version, rollForward = false) => JSON.stringify({ version: 1, isRoot: true, tools: { 'aspire.cli': { version, commands: ['aspire'], rollForward } } });
	const appHost = (version) => `<Project Sdk="Aspire.AppHost.Sdk/${version}">\n</Project>`;
	const props = (version) => `<Project><ItemGroup><PackageVersion Include="Aspire.Hosting.Redis" Version="${version}" /><PackageVersion Include="Npgsql" Version="9.0.0" /></ItemGroup></Project>`;
	{
		// toolManifests finds every dotnet-tools.json under a root (a .config folder included, node_modules, bin and obj not)
		// besides the required ones, which stay listed even when missing so a removed manifest is reported.
		const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-tools-'));
		try {
			for (const file of ['.config/dotnet-tools.json', 'src/App/.config/dotnet-tools.json', 'src/App/node_modules/x/dotnet-tools.json', 'src/App/bin/dotnet-tools.json', '.hidden/dotnet-tools.json']) {
				fs.mkdirSync(path.join(root, path.dirname(file)), { recursive: true });
				fs.writeFileSync(path.join(root, file), '{}');
			}
			const found = toolManifests(root, ['gone/.config/dotnet-tools.json']);
			const expected = ['.config/dotnet-tools.json', 'gone/.config/dotnet-tools.json', 'src/App/.config/dotnet-tools.json'];
			if (JSON.stringify(found) !== JSON.stringify(expected)) failures.push(`tools discovery: expected ${JSON.stringify(expected)}, got ${JSON.stringify(found)}`);
		} finally {
			fs.rmSync(root, { recursive: true, force: true });
		}
	}
	for (const [name, manifests, project, packages, expected] of [
		['one Aspire release, exact tools', [['a.json', manifest('13.6.0')]], appHost('13.6.0'), props('13.6.0'), []],
		['a tool on a range', [['a.json', manifest('13.*')]], appHost('13.6.0'), props('13.6.0'), ['a.json: aspire.cli must name an exact version (got 13.*)']],
		['a tool rolling forward', [['a.json', manifest('13.6.0', true)]], appHost('13.6.0'), props('13.6.0'), ['a.json: aspire.cli must set rollForward to false']],
		['the SDK on a range', [['a.json', manifest('13.6.0')]], appHost('13.*'), props('13.6.0'), ['the Aspire SDK must be named with an exact version']],
		['a package on another release', [['a.json', manifest('13.6.0')]], appHost('13.6.0'), props('13.5.0'), ['Directory.Packages.props: Aspire.Hosting.Redis 13.5.0 must be the Aspire SDK\'s version 13.6.0']],
		['the CLI on another release', [['a.json', manifest('13.5.0')]], appHost('13.6.0'), props('13.6.0'), ['aspire.cli 13.5.0 must be the Aspire SDK\'s version 13.6.0']],
		['a manifest gone', [['a.json', null]], appHost('13.6.0'), props('13.6.0'), ['a.json: missing']]
	]) {
		const found = toolPinProblems(manifests, project, packages).join('\n');
		if (!expected.length && found) failures.push(`tools ${name}: expected clean, got ${found}`);
		for (const fragment of expected) if (!found.includes(fragment)) failures.push(`tools ${name}: did not report "${fragment}" (got: ${found || 'nothing'})`);
	}
	if (failures.length) {
		console.error(`base images: pipeline pins self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

/**
 * ARV-214: a line's words as BuildKit splits it to find here-documents: white space outside quotes ends a word, and quotes
 * and backslashes stay in the word.
 */
function rawWords(line) {
	const words = [];
	let word = '';
	let quote = null;
	for (let i = 0; i < line.length; i++) {
		const c = line[i];
		if (quote) {
			word += c;
			if (c === quote) quote = null;
			else if (c === '\\' && quote === '"' && i + 1 < line.length) word += line[++i];
		} else if (c === '"' || c === "'") {
			quote = c;
			word += c;
		} else if (c === '\\' && i + 1 < line.length) word += c + line[++i];
		else if (/\s/.test(c)) {
			if (word) words.push(word);
			word = '';
		} else word += c;
	}
	if (word) words.push(word);
	return words;
}

/**
 * ARV-214: a Dockerfile's instructions and the problems met reading them. Each instruction is one entry, its continuation lines
 * joined (a trailing backslash; comment lines inside a continuation dropped, as Docker drops them). A RUN, COPY or ADD (ONBUILD
 * included) with a here-document gets the document's lines appended after a new line, so a command written in it is read with
 * the instruction. A here-document is found as BuildKit finds it: a word of the instruction, outside quotes, that is <<NAME or
 * <<-NAME (optionally after digits), its name unquoted (any characters but white space and <); the document ends at the first
 * line equal to the name (tabs stripped first for <<-). One whose end is never found is reported (it cannot be read) and takes
 * no lines, so the instructions after it are still read.
 */
export function parseDockerfile(dockerfile) {
	const lines = dockerfile.split(/\r?\n/);
	const instructions = [];
	const problems = [];
	for (let i = 0; i < lines.length; i++) {
		let text = lines[i];
		if (!text.trim() || /^\s*#/.test(text)) continue;
		while (/\\\s*$/.test(text) && i + 1 < lines.length) {
			i++;
			if (/^\s*#/.test(lines[i])) continue;
			text = `${text.replace(/\\\s*$/, ' ')}${lines[i]}`;
		}
		if (/^\s*(?:ONBUILD\s+)?(?:RUN|COPY|ADD)\b/i.test(text)) {
			for (const word of rawWords(text)) {
				const heredoc = /^(\d*)<<(-?)([^<]*)$/.exec(word);
				if (!heredoc) continue;
				const name = heredoc[3].replace(/\\(.)/g, '$1').replace(/["']/g, '');
				if (!name || /\s/.test(name)) continue;
				const end = lines.findIndex((line, index) => index > i && (heredoc[2] ? line.replace(/^\t+/, '') : line) === name);
				if (end < 0) {
					problems.push(`an unterminated here-document (<<${heredoc[2]}${name}) cannot be read: ${text.trim().slice(0, 120)}`);
					break;
				}
				text = `${text}\n${lines.slice(i + 1, end).join('\n')}`;
				i = end;
			}
		}
		instructions.push(text);
	}
	return { instructions, problems };
}

/** ARV-214: a Dockerfile's instructions (parseDockerfile). */
export function dockerInstructions(dockerfile) {
	return parseDockerfile(dockerfile).instructions;
}

const SHELLS = new Set(['sh', 'bash', 'ash', 'dash', 'zsh', 'ksh', 'mksh']);
/** Words that run the command after them (shell keywords included), skipped with the options they take. */
const COMMAND_PREFIXES = new Set(['exec', 'command', 'builtin', 'nohup', 'time', 'eval', 'then', 'do', 'else', 'elif', 'if', 'while', 'until', '!', '{', '}']);
const baseName = (word) => word.replace(/^.*\//, '');
/** A word the shell expands before it runs (a variable, a command substitution, a glob, a brace list): its tool is unknown. */
const isExpansion = (word) => /[$`*?]/.test(word) || (word.length > 1 && word.indexOf('[') >= 0 && word.indexOf('[') < word.lastIndexOf(']')) || /\{[^}]*,[^}]*\}/.test(word);

/**
 * ARV-214: one command's words with the tool first. Skipped in front of the tool: VAR=value assignments; env with its options
 * (-u NAME, -C DIR and their long forms take a value; -S is read as the command line itself); exec, command, builtin, nohup,
 * time, eval and the shell keywords; a shell with its options (sh -c, bash -euo pipefail -c: with quotes dropped the script's
 * words follow); timeout with its options and its duration; nice with -n; corepack (the tool it runs follows). A tool named
 * by a path is named by its file name (/usr/local/bin/npm is npm), and node running npm's own script (npm-cli.js, npx-cli.js)
 * is npm or npx. An empty list when nothing is left.
 */
export function commandWords(input) {
	let words = input;
	let i = 0;
	const valueOf = (options) => {
		if (i < words.length && options.has(words[i])) i += 2;
		else i++;
	};
	while (i < words.length) {
		const word = words[i];
		// An expansion keeps its whole text, so unreadableRunProblems sees it rather than the name after its last slash.
		const base = isExpansion(word) ? word : baseName(word);
		if (/^[A-Za-z_][A-Za-z0-9_]*=/.test(word) || COMMAND_PREFIXES.has(word)) {
			i++;
			if (word === 'command' || word === 'builtin') while (i < words.length && /^-[pvV]+$/.test(words[i])) i++;
			continue;
		}
		if (base === 'env') {
			i++;
			while (i < words.length && words[i].startsWith('-')) {
				// -S 'npm install' splits its text into the command; written -S'npm install', the text starts in the same word.
				const split = words[i].match(/^(?:-S|--split-string=)(.+)$/)?.[1];
				if (split) words = [...words.slice(0, i), split, ...words.slice(i + 1)];
				else valueOf(new Set(['-u', '--unset', '-C', '--chdir']));
			}
			continue;
		}
		if (SHELLS.has(base)) {
			i++;
			// -o and -O take a value, also at the end of a cluster (bash -euo pipefail -c ...).
			while (i < words.length && /^[-+]/.test(words[i])) i += /^[-+][a-zA-Z]*[oO]$/.test(words[i]) ? 2 : 1;
			continue;
		}
		if (base === 'timeout') {
			i++;
			while (i < words.length && words[i].startsWith('-')) valueOf(new Set(['-s', '--signal', '-k', '--kill-after']));
			i++;
			continue;
		}
		if (base === 'nice') {
			i++;
			while (i < words.length && words[i].startsWith('-')) valueOf(new Set(['-n', '--adjustment']));
			continue;
		}
		if (base === 'corepack') {
			i++;
			continue;
		}
		if (base === 'node' || base === 'nodejs') {
			const script = words.slice(i + 1).find((next) => !next.startsWith('-'));
			if (script && /^(npm|npx)-cli\.js$/.test(baseName(script))) {
				const rest = words.slice(words.indexOf(script, i + 1) + 1);
				return [baseName(script).slice(0, 3), ...rest];
			}
		}
		return [base, ...words.slice(i + 1)];
	}
	return [];
}

/**
 * ARV-214: the commands of a shell script as a RUN line's shell form runs them, each as commandWords. Read fail closed, so a
 * command is never missed for its spelling: commands are split at &&, ||, ;, a pipe, a background &, a new line, a subshell's
 * or a group's brackets and a command substitution ($(...) or backticks), also inside quotes; quotes are removed and a
 * backslash keeps the character after it, as the shell's quote removal does (n"p"m and n\pm are npm), so a script wrapped in
 * sh -c or eval is read as commands of its own. A word that held a space inside quotes is read as several words.
 */
export function shellCommands(script) {
	const flat = script.replace(/\\\r?\n/g, ' ').replace(/\\(.)/g, '$1').replace(/["']/g, '');
	return flat.split(/&&|\|\||[;|&\n()`]/).map((part) => commandWords(part.trim().split(/\s+/).filter(Boolean))).filter((words) => words.length);
}

/**
 * ARV-214: the commands of one Dockerfile instruction (dockerInstructions), each as commandWords; none unless it is RUN (in any
 * case, as Docker reads instructions). The RUN flags (--mount=..., --network=...) are skipped. The exec form, a JSON array of
 * strings, is one command with its words as written, except that a shell or eval at its head has the rest read as a script
 * (["sh", "-c", "npm install"]); text that is not such an array is the shell form, as Docker runs it.
 */
export function runCommands(instruction) {
	const words = execWords(instruction);
	let commands;
	if (words) {
		const command = commandWords(words);
		commands = command.length ? [command] : [];
		// A word holding white space is a script some wrapper hands a shell (["su", "node", "-c", "npm install"]): read it too.
		for (const word of words.slice(1)) if (/\s/.test(word.trim())) commands.push(...shellCommands(word));
	} else {
		const script = runScript(instruction);
		commands = script === null ? [] : shellCommands(script);
	}
	return withEmbeddedTools(commands);
}

/**
 * ARV-214: the tools whose packages the npm and restore rules guard. A command that holds one of them after its first word
 * (sudo npm install, xargs npm install, su node -c npm install, find -execdir dotnet restore, a redirection before the tool,
 * npm explore pkg -- npm install, node /usr/local/bin/npm install) also yields a command that starts there, so a wrapper the
 * reader does not know cannot hide the tool: read fail closed, a word that only names the tool (apt-get install npm) yields a
 * command with no verb, which no rule refuses.
 */
const GUARDED_TOOLS = new Set(['npm', 'npx', 'yarn', 'yarnpkg', 'pnpm', 'pnpx', 'bun', 'bunx', 'dotnet', 'dnx']);
function withEmbeddedTools(commands) {
	const out = [];
	for (const words of commands) {
		out.push(words);
		for (let k = 1; k < words.length; k++) {
			if (!words[k].startsWith('-') && GUARDED_TOOLS.has(baseName(words[k]))) out.push([baseName(words[k]), ...words.slice(k + 1)]);
		}
	}
	return out;
}

/**
 * ARV-214: the commands an instruction runs or writes for a later RUN: a RUN's (runCommands), and a COPY's or ADD's
 * here-document read as a script (COPY <<EOF /install.sh ... EOF, then RUN sh /install.sh), fail closed even when the
 * document is not run.
 */
export function instructionCommands(instruction) {
	if (/^\s*(?:ONBUILD\s+)?RUN\b/i.test(instruction)) return runCommands(instruction);
	if (/^\s*(?:ONBUILD\s+)?(?:COPY|ADD)\b/i.test(instruction) && instruction.includes('\n')) return withEmbeddedTools(shellCommands(instruction.slice(instruction.indexOf('\n') + 1)));
	return [];
}

/** The body of a RUN instruction after its flags, or null for another instruction. */
function runBody(instruction) {
	const match = instruction.match(/^\s*(?:ONBUILD\s+)?RUN\s+((?:--[a-z-]+(?:=\S*)?\s+)*)([\s\S]*)$/i);
	return match ? match[2].trim() : null;
}

/** The words of a RUN instruction in the exec form whose head is not a shell, eval or env (those run a script); else null. */
function execWords(instruction) {
	const body = runBody(instruction);
	if (body === null || !body.startsWith('[')) return null;
	try {
		const parsed = JSON.parse(body);
		if (!Array.isArray(parsed) || !parsed.every((word) => typeof word === 'string')) return null;
		const head = parsed.length ? baseName(parsed[0]) : '';
		return SHELLS.has(head) || head === 'eval' || head === 'env' ? null : parsed;
	} catch {
		return null; // not an array: Docker runs it as the shell form
	}
}

/**
 * The shell script a RUN instruction runs: its shell form's text, or for the exec form with a shell, eval or env at its head
 * (["sh", "-c", "npm install"]) its words joined, quotes and backslashes taken out of each; null for the exec form of any other
 * tool and for another instruction.
 */
function runScript(instruction) {
	const body = runBody(instruction);
	if (body === null) return null;
	if (body.startsWith('[')) {
		try {
			const parsed = JSON.parse(body);
			if (Array.isArray(parsed) && parsed.every((word) => typeof word === 'string')) {
				if (execWords(instruction)) return null;
				return parsed.map((word) => word.replace(/[\\"']/g, '')).join(' ');
			}
		} catch {
			// not an array: the shell form
		}
	}
	return body;
}

/**
 * ARV-214: the npm command a command's words run (canonical name), or undefined: the first word after npm that is neither an
 * option nor an option's value (--prefix web, -C web, --cache dir and the other options that take one; a word after an unknown
 * option is read as its value unless it is a command), resolved as npm resolves it: an alias (i, add, isntall, ci's ic) or a
 * unique abbreviation of a command or alias (npm insta, npm upd) names the command it stands for.
 */
const NPM_VALUE_OPTIONS = new Set(['--prefix', '-C', '--cache', '--registry', '--userconfig', '--globalconfig', '--loglevel', '-w', '--workspace', '--omit', '--include', '--location', '--script-shell', '--tag', '--otp', '--before', '--cafile', '--ca', '--cert', '--key', '--proxy', '--https-proxy', '--noproxy', '--install-strategy', '--node-options', '--shell', '--editor', '--auth-type', '--scope', '--init-module', '--workspaces-update', '--call', '-c', '--package']);
/** npm options known to take no value, so the word after one is the command (npm --no-audit install). */
const NPM_FLAGS = new Set(['-g', '--global', '-f', '--force', '-s', '--silent', '-q', '--quiet', '-d', '-dd', '-ddd', '--verbose', '--no-audit', '--audit', '--no-fund', '--fund', '--ignore-scripts', '--no-ignore-scripts', '--legacy-peer-deps', '--strict-peer-deps', '--prefer-offline', '--prefer-online', '--offline', '--dry-run', '--production', '--no-optional', '--no-save', '-D', '-S', '-E', '-O', '-B', '-P', '--save', '--save-dev', '--save-exact', '--save-optional', '--save-peer', '--no-package-lock', '--package-lock-only', '--foreground-scripts', '--if-present', '--ws', '--workspaces', '--include-workspace-root', '-y', '--yes', '--no', '--color', '--no-color', '--progress', '--no-progress', '--json', '--parseable', '--long', '-l', '--version', '-v', '-h', '--help', '--usage', '--unicode', '--no-update-notifier', '--update-notifier']);
const NPM_COMMANDS = ['access', 'adduser', 'audit', 'bugs', 'cache', 'ci', 'completion', 'config', 'dedupe', 'deprecate', 'diff', 'dist-tag', 'docs', 'doctor', 'edit', 'exec', 'explain', 'explore', 'find-dupes', 'fund', 'get', 'help', 'help-search', 'hook', 'init', 'install', 'install-ci-test', 'install-test', 'link', 'll', 'login', 'logout', 'ls', 'org', 'outdated', 'owner', 'pack', 'ping', 'pkg', 'prefix', 'profile', 'prune', 'publish', 'query', 'rebuild', 'repo', 'restart', 'root', 'run-script', 'sbom', 'search', 'set', 'shrinkwrap', 'star', 'stars', 'start', 'stop', 'team', 'test', 'token', 'undeprecate', 'uninstall', 'unpublish', 'unstar', 'update', 'version', 'view', 'whoami'];
const NPM_ALIASES = {
	add: 'install', i: 'install', in: 'install', ins: 'install', inst: 'install', insta: 'install', instal: 'install', isnt: 'install', isnta: 'install', isntal: 'install', isntall: 'install',
	'clean-install': 'ci', ic: 'ci', 'install-clean': 'ci', 'isntall-clean': 'ci', cit: 'install-ci-test', 'clean-install-test': 'install-ci-test', sit: 'install-ci-test', it: 'install-test',
	up: 'update', upgrade: 'update', udpate: 'update', x: 'exec', create: 'init', innit: 'init', ln: 'link', run: 'run-script', rum: 'run-script', urn: 'run-script',
	t: 'test', tst: 'test', un: 'uninstall', unlink: 'uninstall', remove: 'uninstall', rm: 'uninstall', r: 'uninstall', list: 'ls', la: 'ls', c: 'config', ddp: 'dedupe', rb: 'rebuild',
	v: 'view', info: 'view', show: 'view', find: 'search', s: 'search', se: 'search', author: 'owner', home: 'docs', issues: 'bugs', login: 'adduser', 'add-user': 'adduser', hlep: 'help', verison: 'version', why: 'explain'
};
function npmCommand(written) {
	// npm reads camelCase as kebab case (installTest is install-test), then an exact name, then a unique abbreviation.
	const word = written.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);
	const names = [...NPM_COMMANDS, ...Object.keys(NPM_ALIASES)];
	let name = names.includes(word) ? word : undefined;
	if (!name) {
		const matches = names.filter((candidate) => candidate.startsWith(word));
		if (matches.length === 1) name = matches[0];
	}
	while (name && NPM_ALIASES[name]) name = NPM_ALIASES[name];
	return name;
}
export function npmVerbs(words) {
	const verbs = [];
	for (let i = 1; i < words.length; i++) {
		const word = words[i];
		if (word === '--') {
			if (words[i + 1] !== undefined) verbs.push(npmCommand(words[i + 1]) ?? words[i + 1]);
			return verbs;
		}
		if (word.startsWith('-')) {
			if (!word.includes('=') && NPM_VALUE_OPTIONS.has(word)) i++;
			continue;
		}
		// A word right after an option npm may read a value for (one not known to take none) is either that value or the
		// command: both are candidates, and the next word that follows no such option is the command for certain.
		const previous = words[i - 1];
		const ambiguous = i > 1 && previous.startsWith('-') && !previous.includes('=') && !NPM_VALUE_OPTIONS.has(previous) && !NPM_FLAGS.has(previous);
		verbs.push(npmCommand(word) ?? word);
		if (!ambiguous) return verbs;
	}
	return verbs;
}
/** The npm command words most likely run (the last candidate of npmVerbs), or undefined. */
export const npmVerb = (words) => npmVerbs(words).at(-1);

/**
 * ARV-214: the dotnet command a command's words run, or undefined: the first word after dotnet that is neither a host option
 * (-d, --diagnostics) nor the value of one that takes a value (--additionalprobingpath, --depsfile, --runtimeconfig,
 * --roll-forward, --fx-version and the like). msbuild with a restore switch or target (-restore, -r, -t:Restore) is a restore.
 */
const DOTNET_VALUE_OPTIONS = new Set(['--additionalprobingpath', '--additional-deps', '--depsfile', '--runtimeconfig', '--roll-forward', '--fx-version', '--roll-forward-on-no-candidate-fx']);
export function dotnetVerb(words) {
	for (let i = 1; i < words.length; i++) {
		const word = words[i];
		if (word.startsWith('-')) {
			if (DOTNET_VALUE_OPTIONS.has(word)) i++;
			continue;
		}
		if (word.toLowerCase() === 'msbuild' && words.slice(i + 1).some((arg) => /^(?:-{1,2}|\/)(?:restore|r)(?::true)?$/i.test(arg) || /^(?:-{1,2}|\/)(?:t|target):(?:[^;,]*[;,])*restore(?:[;,].*)?$/i.test(arg))) return 'restore';
		return word.toLowerCase();
	}
	return undefined;
}

/** ARV-214: the commands of every RUN instruction of a Dockerfile, with the instruction each came from. */
export function dockerRunCommands(dockerfile) {
	return dockerInstructions(dockerfile).flatMap((instruction) => instructionCommands(instruction).map((words) => ({ instruction, words })));
}

/**
 * ARV-214: a RUN command whose tool is a shell expansion ($TOOL install, ${NPM} ci, $(which npm) install, `which npm` install)
 * could be npm or dotnet, which the checks below could then not see: it is refused, with the command named. The shell text is
 * split at &&, ||, ;, a pipe, a background & and new lines only, so a command substitution stays in the word it starts and is
 * seen as the tool. The exec form expands nothing, so its words are what runs (a shell or eval at its head excepted).
 */
export function unreadableRunProblems(dockerfile) {
	const { instructions, problems } = parseDockerfile(dockerfile);
	for (const instruction of instructions) {
		// A SHELL other than a shell would run every later shell-form RUN through another tool (SHELL ["/usr/local/bin/npm"]).
		const shell = instruction.match(/^\s*SHELL\s+(\[[\s\S]*\])\s*$/i)?.[1];
		if (/^\s*SHELL\b/i.test(instruction)) {
			let head = null;
			try {
				head = JSON.parse(shell ?? '')[0];
			} catch {
				// not an array
			}
			if (typeof head !== 'string' || !SHELLS.has(baseName(head))) problems.push(`SHELL must name a shell (${[...SHELLS].join(', ')}): ${instruction.trim().slice(0, 120)}`);
		}
		const script = runScript(instruction);
		if (script === null) continue;
		const flat = script.replace(/\\\r?\n/g, ' ').replace(/\\(.)/g, '$1').replace(/["']/g, '');
		for (const part of flat.split(/&&|\|\||[;|&\n]/)) {
			const words = commandWords(part.trim().replace(/^[({\s]+/, '').split(/\s+/).filter(Boolean));
			if (words.length && isExpansion(words[0])) problems.push(`a RUN command named by an expansion cannot be checked: name its tool (${words.join(' ')})`);
			// node running a script named by an expansion (node "$(command -v npm)" install) could be npm.
			const script = (words[0] === 'node' || words[0] === 'nodejs') ? words.slice(1).find((word) => !word.startsWith('-')) : undefined;
			if (script && isExpansion(script)) problems.push(`a RUN command runs node on a script named by an expansion, which cannot be checked: name the script (${words.join(' ')})`);
		}
	}
	return problems;
}

/**
 * ARV-096 (ASVS 5.0 V15.1.2, supply chain): a Dockerfile that restores NuGet packages restores what the lock files name, from
 * the repository's sources. Problems for a Dockerfile text, given read(path) for repository files (null when missing):
 * nuget.config is copied before the first restore; every dotnet restore passes --locked-mode; every dotnet publish or build
 * holds the restore it runs to the lock files (-p:RestoreLockedMode=true) or skips it (--no-restore), and so do dotnet build,
 * run, test and pack (ARV-214, which also reads the RUN forms runCommands reads); the projects copied
 * before a restore are the restored project's whole closure (ProjectReference, followed), each with its packages.lock.json
 * beside it; and each of those lock files records the runtime the restore names (-r linux-x64 needs RuntimeIdentifiers).
 */
export function lockedRestoreProblems(dockerfile, read) {
	// ARV-214: the forms instructionCommands reads: the exec form, a tool named by a path, env and VAR=value prefixes, a
	// shell wrapper, a here-document, dotnet anywhere in a command; build, publish, run, test, pack and format restore on their
	// own unless told not to.
	const instructions = dockerInstructions(dockerfile);
	const commandsOf = (instruction) => instructionCommands(instruction).filter((words) => words[0] === 'dotnet' || words[0] === 'dnx');
	const verbOf = (words) => (words[0] === 'dnx' ? 'dnx' : dotnetVerb(words));
	const isRestore = (instruction) => commandsOf(instruction).some((words) => verbOf(words) === 'restore');
	const all = instructions.flatMap(commandsOf);
	const restores = all.filter((words) => verbOf(words) === 'restore');
	const builds = all.filter((words) => DOTNET_RESTORING.includes(verbOf(words)));
	const fetches = all.filter((words) => DOTNET_FETCHING(words));
	if (!restores.length && !builds.length && !fetches.length) return [];
	const problems = [];
	const first = instructions.findIndex(isRestore);
	const copied = new Set();
	for (const line of instructions.slice(0, first < 0 ? instructions.length : first)) {
		const args = line.match(/^\s*COPY\s+(?!--from)(.+)$/i)?.[1]?.trim().replace(/^(--[a-z-]+(=\S*)?\s+)+/i, '');
		if (!args) continue;
		let parts;
		try {
			parts = args.startsWith('[') ? JSON.parse(args) : args.split(/\s+/);
		} catch {
			problems.push(`a COPY line could not be read: ${line.trim()}`);
			continue;
		}
		for (const source of parts.slice(0, -1)) copied.add(source.replace(/^\.\//, ''));
	}
	for (const words of fetches) problems.push(`${words[0] === 'dnx' ? 'dnx' : `dotnet ${words.slice(1, 3).join(' ')}`} fetches or changes packages the lock files do not name: an image build restores only what packages.lock.json names (${words.join(' ')})`);
	for (const words of [...restores, ...builds]) {
		if (words.some((word) => /^(?:-{1,2}|\/)p(?:roperty)?:RestoreLockedMode=(?!true$)/i.test(word))) problems.push(`dotnet ${verbOf(words)} must not turn locked mode off (${words.join(' ')})`);
		if (words.some((word) => /^--(?:source|configfile)(?:[=:].*)?$/.test(word)) || (verbOf(words) === 'restore' && words.includes('-s'))) problems.push(`dotnet ${verbOf(words)} must take its package sources from nuget.config alone (${words.join(' ')})`);
	}
	const lockedProperty = (word) => /^(?:-{1,2}|\/)p(?:roperty)?:RestoreLockedMode=true$/i.test(word);
	if (!restores.length) problems.push('a Dockerfile that builds .NET must restore first, in locked mode (dotnet restore --locked-mode)');
	else if (!copied.has('nuget.config')) problems.push('nuget.config (package sources and their mapping) must be copied before the restore');
	for (const restore of restores) {
		if (!restore.includes('--locked-mode') && !restore.some(lockedProperty)) problems.push(`dotnet restore must pass --locked-mode (or -p:RestoreLockedMode=true) (${restore.join(' ')})`);
	}
	for (const build of builds) {
		if (!build.includes('--no-restore') && !build.includes('--no-build') && !build.some(lockedProperty)) problems.push(`dotnet ${dotnetVerb(build)} must hold the restore it runs to the lock files (-p:RestoreLockedMode=true) or skip it (--no-restore, or --no-build) (${build.join(' ')})`);
	}
	for (const restore of restores) {
		const project = restore.find((word) => /\.csproj$/i.test(word))?.replace(/^\.\//, '');
		if (!project) {
			problems.push(`dotnet restore must name the project it restores (${restore.join(' ')})`);
			continue;
		}
		const runtimeAt = restore.findIndex((word) => word === '-r' || word === '--runtime');
		const runtime = runtimeAt >= 0 ? restore[runtimeAt + 1] : restore.map((word) => word.match(/^(?:-r|--runtime)[=:](.+)$/)?.[1]).find(Boolean);
		const closure = new Set();
		const visit = (csproj) => {
			if (closure.has(csproj)) return;
			closure.add(csproj);
			const text = read(csproj);
			if (text === null) {
				problems.push(`${csproj} (in the closure of ${project}) does not exist`);
				return;
			}
			for (const ref of text.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) visit(path.posix.normalize(path.posix.join(path.posix.dirname(csproj), ref[1].replace(/\\/g, '/'))));
		};
		visit(project);
		for (const csproj of closure) {
			if (!copied.has(csproj)) problems.push(`${csproj} is in the closure of ${project} and must be copied before the restore`);
			const lock = path.posix.join(path.posix.dirname(csproj), 'packages.lock.json');
			if (!copied.has(lock)) {
				problems.push(`${lock} must be copied beside ${csproj} before the restore`);
				continue;
			}
			const lockText = read(lock);
			let graphs = [];
			try {
				graphs = Object.keys(JSON.parse(lockText ?? '').dependencies ?? {});
			} catch {
				problems.push(`${lock} is missing or not a lock file`);
				continue;
			}
			if (runtime && !graphs.some((graph) => graph.endsWith(`/${runtime}`))) problems.push(`${lock} has no ${runtime} graph, which the restore needs (RuntimeIdentifiers in Directory.Build.props)`);
		}
	}
	return [...new Set(problems)];
}

/** ARV-214: dotnet commands that restore on their own unless told not to. */
const DOTNET_RESTORING = ['publish', 'build', 'run', 'test', 'pack', 'format'];
/** ARV-214: dotnet commands that fetch or change packages outside the lock files: a .NET tool, a package added, a workload, dnx. */
const DOTNET_FETCHING = (words) => {
	if (words[0] === 'dnx') return words.length > 1;
	const verb = dotnetVerb(words);
	const next = words[words.indexOf(words.find((word, index) => index > 0 && word.toLowerCase() === verb)) + 1]?.toLowerCase();
	if (verb === 'tool') return ['install', 'exec', 'restore', 'update', 'execute'].includes(next);
	// dotnet add [<project>] package <id>
	if (verb === 'add') return words.slice(1).map((word) => word.toLowerCase()).includes('package');
	if (verb === 'package') return ['add', 'update'].includes(next);
	if (verb === 'workload') return ['install', 'update', 'restore', 'repair'].includes(next);
	return false;
};

/**
 * ARV-096 (ASVS 5.0 V15.1.2): a Dockerfile that installs npm packages installs exactly what its package-lock.json names.
 * Problems for a Dockerfile text: any command that may resolve on its own (npm install and its aliases, update, install-test,
 * link, dedupe, init and create, audit fix, exec and npx, or yarn, pnpm and bun, which have no lock file here) is refused,
 * and every npm ci needs a package-lock.json copied before it, so that it cannot run against a lock file that is not there.
 * ARV-214: the RUN forms runCommands reads are read, and npm's command is found as npm finds it (npmVerb).
 */
export function lockedNpmProblems(dockerfile) {
	const problems = [];
	let lockCopied = false;
	for (const instruction of dockerInstructions(dockerfile)) {
		// Each stage starts with what its FROM gives it: a lock file copied into another stage does not count.
		if (/^\s*FROM\s/i.test(instruction)) lockCopied = false;
		const copy = instruction.match(/^\s*COPY\s+(?!--from)(.+)$/i)?.[1];
		if (copy && /(^|[\s"/])package-lock\.json\b/.test(copy)) lockCopied = true;
		// ARV-214: the forms instructionCommands reads: the exec form, a tool named by a path, env and VAR=value prefixes,
		// shell wrappers, here-documents, a guarded tool anywhere in a command; npm's command found as npm finds it, every
		// candidate when an option before it may take a value (npmVerbs).
		for (const words of instructionCommands(instruction)) {
			const tool = words[0];
			const command = words.join(' ');
			const verbs = tool === 'npm' ? npmVerbs(words) : [];
			if (NPM_FETCHERS.has(tool) ? words.length > 1 : verbs.includes('exec')) {
				problems.push(`${tool === 'npm' ? 'npm exec' : tool} fetches a package the lock file does not name: run it from package-lock.json's tree (${command})`);
				continue;
			}
			const refused = verbs.find((verb) => NPM_REFUSED.includes(verb)) ?? (verbs.includes('audit') && words.includes('fix') ? 'audit' : undefined);
			if (refused) problems.push(`npm ${refused}${refused === 'audit' ? ' fix' : ''} may change the tree the lock file names: install with npm ci from package-lock.json (${command})`);
			else if (verbs.some((verb) => ['ci', 'install-ci-test'].includes(verb)) && !lockCopied) problems.push(`npm ci needs package-lock.json copied before it (${command})`);
			else if (NPM_OTHERS.has(tool)) problems.push(`${tool} is not used here: install with npm ci from package-lock.json (${command})`);
		}
	}
	return [...new Set(problems)];
}

/** ARV-214: npm commands that may change the tree its lock file names (canonical names), refused in an image build. */
const NPM_REFUSED = ['install', 'update', 'install-test', 'link', 'dedupe', 'init'];
/** ARV-214: tools that fetch and run a package by name. */
const NPM_FETCHERS = new Set(['npx', 'pnpx', 'bunx']);
/** ARV-214: package managers with no lock file here. */
const NPM_OTHERS = new Set(['yarn', 'yarnpkg', 'pnpm', 'bun']);

function selfTestLockedNpm() {
	const good = [
		'FROM node AS builder',
		'COPY ./web/package.json ./web/package-lock.json ./',
		'RUN --mount=type=cache,id=npm,target=/root/.npm \\',
		'    npm set cache /root/.npm && \\',
		'    npm ci',
		'COPY ./web .',
		'RUN npm run build --prod',
		''
	].join('\n');
	const cases = [
		['good', good, []],
		['not node', 'FROM sdk\nRUN dotnet restore x.csproj --locked-mode\n', []],
		['npm install', good.replace('npm ci', 'npm install --force'), ['npm install may change the tree']],
		['npm i', good.replace('npm ci', 'npm i'), ['npm install may change the tree']],
		['npm with a flag first', good.replace('npm ci', 'npm --no-audit install'), ['npm install may change the tree']],
		['npm update', good.replace('npm run build --prod', 'npm update && npm run build'), ['npm update may change the tree']],
		['yarn', good.replace('npm ci', 'yarn install --frozen-lockfile'), ['yarn is not used here']],
		['no lock file', good.replace(' ./web/package-lock.json', ''), ['npm ci needs package-lock.json copied before it']],
		['lock file copied after', good.replace(' ./web/package-lock.json', '').replace('COPY ./web .', 'COPY ./web .\nCOPY ./web/package-lock.json ./'), ['npm ci needs package-lock.json copied before it']],
		['clean-install without a lock file', good.replace(' ./web/package-lock.json', '').replace('npm ci', 'npm clean-install'), ['npm ci needs package-lock.json copied before it']],
		['lock file in another stage', good.replace('COPY ./web .', 'FROM node AS second\nCOPY ./web .\nRUN npm ci'), ['npm ci needs package-lock.json copied before it']],
		['npx', good.replace('npm run build --prod', 'npx vite build'), ['npx fetches a package the lock file does not name']],
		['npm exec', good.replace('npm run build --prod', 'npm exec -- vite build'), ['npm exec fetches a package the lock file does not name']],
		['wrapped in sh -c', good.replace('npm ci', 'sh -c "npm install"'), ['npm install may change the tree']],
		['after a pipe', good.replace('npm run build --prod', 'echo ok | npm i left-pad'), ['npm install may change the tree']],
		// ARV-214: the RUN forms the reader reads.
		['exec form install', good.replace('RUN npm run build --prod', 'RUN ["npm", "install"]'), ['install with npm ci from package-lock.json (npm install)']],
		['exec form through sh -c', good.replace('RUN npm run build --prod', 'RUN ["/bin/dash", "-c", "npm install --omit=dev && npm run build"]'), ['npm install may change the tree']],
		['exec form npx', good.replace('RUN npm run build --prod', 'RUN ["npx", "vite", "build"]'), ['npx fetches a package the lock file does not name']],
		['exec form ci without a lock file', good.replace(' ./web/package-lock.json', '').replace('npm set cache /root/.npm && \\\n    npm ci', 'true').replace('RUN npm run build --prod', 'RUN ["npm", "ci"]'), ['npm ci needs package-lock.json copied before it']],
		['exec form ci with its lock file', good.replace('RUN npm run build --prod', 'RUN ["npm", "ci", "--omit=dev"]'), []],
		['tool named by a path', good.replace('npm ci', '/usr/local/bin/npm install'), ['npm install may change the tree']],
		['exec form tool named by a path', good.replace('RUN npm run build --prod', 'RUN ["/usr/local/bin/npm", "i", "left-pad"]'), ['npm install may change the tree']],
		['node running npm-cli.js', good.replace('npm ci', 'node /usr/local/lib/node_modules/npm/bin/npm-cli.js install'), ['npm install may change the tree']],
		['env prefix', good.replace('npm ci', 'env NODE_ENV=production npm install'), ['npm install may change the tree']],
		['env with options', good.replace('npm ci', 'env -i -u HOME PATH=/usr/bin npm install'), ['npm install may change the tree']],
		['env -S joined to its text', good.replace('npm ci', "env -S'npm install'"), ['npm install may change the tree']],
		['VAR=value prefix', good.replace('npm ci', 'CI=1 NODE_ENV=production npm install'), ['npm install may change the tree']],
		['verb after --prefix', good.replace('npm ci', 'npm --prefix web install'), ['npm install may change the tree']],
		['verb after --prefix=', good.replace('npm ci', 'npm --prefix=web install'), ['npm install may change the tree']],
		['verb after -C and --cache', good.replace('npm ci', 'npm -C web --cache /tmp/npm i'), ['npm install may change the tree']],
		['verb after an unknown option', good.replace('npm ci', 'npm --some-flag value install'), ['npm install may change the tree']],
		['ci after --prefix is still ci', good.replace('npm ci', 'npm --prefix web ci'), []],
		['an abbreviation', good.replace('npm ci', 'npm upd'), ['npm update may change the tree']],
		['camel case', good.replace('npm ci', 'npm installTest'), ['npm install-test may change the tree']],
		['after --', good.replace('npm ci', 'npm -- install'), ['npm install may change the tree']],
		['audit fix', good.replace('npm run build --prod', 'npm audit fix'), ['npm audit fix may change the tree']],
		['link', good.replace('npm run build --prod', 'npm link left-pad'), ['npm link may change the tree']],
		['init fetches an initializer', good.replace('npm run build --prod', 'npm create vite'), ['npm init may change the tree']],
		['quotes inside the tool', good.replace('npm ci', 'n"p"m install'), ['npm install may change the tree']],
		['backslash inside the tool', good.replace('npm ci', 'n\\pm install'), ['npm install may change the tree']],
		['bash with options', good.replace('npm ci', 'bash -euo pipefail -c "npm install"'), ['npm install may change the tree']],
		['eval', good.replace('npm ci', 'eval "npm install"'), ['npm install may change the tree']],
		['command substitution', good.replace('npm run build --prod', 'echo "$(npm install)"'), ['npm install may change the tree']],
		['subshell and group', good.replace('npm run build --prod', '(npm install) && { npm update; }'), ['npm install may change the tree', 'npm update may change the tree']],
		['if then', good.replace('npm run build --prod', 'if true; then npm install; fi'), ['npm install may change the tree']],
		['timeout and nice', good.replace('npm run build --prod', 'timeout -s KILL 60 nice -n 5 npm install'), ['npm install may change the tree']],
		['corepack', good.replace('npm run build --prod', 'corepack pnpm install'), ['pnpm is not used here']],
		['lower case run', good.replace('RUN npm run build --prod', 'run npm install'), ['npm install may change the tree']],
		['here-document', good.replace('RUN npm run build --prod', 'RUN <<EOF\nset -e\nnpm install\nEOF\nRUN npm run build'), ['npm install may change the tree']],
		['comment inside a continuation', good.replace('npm set cache /root/.npm && \\\n', 'npm set cache /root/.npm && \\\n# a comment\n'), []]
	];
	const failures = [];
	for (const [name, text, expected] of cases) {
		const found = lockedNpmProblems(text).join('\n');
		if (!expected.length && found) failures.push(`${name}: expected clean, got ${found}`);
		for (const fragment of expected) if (!found.includes(fragment)) failures.push(`${name}: did not report "${fragment}" (got: ${found || 'nothing'})`);
	}
	if (failures.length) {
		console.error(`base images: locked npm self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

/**
 * ARV-214: every entry of the reader's tables, each from a list written here rather than read from the table, so removing an
 * entry fails: the prefixes, shells and wrappers commandWords skips, npm's value options and flags, the refused npm commands,
 * fetchers and other managers, the guarded tools found anywhere in a command, here-documents, ONBUILD, a COPY here-document,
 * SHELL and node on an expansion.
 */
function selfTestCommandTables() {
	const failures = [];
	const same = (name, actual, expected) => {
		if (JSON.stringify(actual) !== JSON.stringify(expected)) failures.push(`${name}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
	};
	for (const prefix of ['exec', 'command', 'builtin', 'nohup', 'time', 'eval', 'then', 'do', 'else', 'elif', 'if', 'while', 'until', '!', '{', '}', 'X=1'])
		same(`prefix ${prefix}`, commandWords([prefix, 'npm', 'install']), ['npm', 'install']);
	for (const shell of ['sh', 'bash', 'ash', 'dash', 'zsh', 'ksh', 'mksh', '/usr/bin/zsh'])
		same(`shell ${shell}`, commandWords([shell, '-e', '-o', 'pipefail', '-c', 'npm', 'install']), ['npm', 'install']);
	same('env options', commandWords(['env', '-i', '-u', 'A', '--unset', 'B', '-C', '/w', '--chdir', '/x', 'npm', 'ci']), ['npm', 'ci']);
	same('env -S', commandWords(['env', '-Snpm', 'install']), ['npm', 'install']);
	same('timeout short options', commandWords(['timeout', '-s', 'KILL', '-k', '5', '60', 'npm', 'ci']), ['npm', 'ci']);
	same('timeout long options', commandWords(['timeout', '--signal', 'KILL', '--kill-after', '5', '60', 'npm', 'ci']), ['npm', 'ci']);
	same('nice -n', commandWords(['nice', '-n', '5', 'npm', 'ci']), ['npm', 'ci']);
	same('nice --adjustment', commandWords(['nice', '--adjustment', '5', 'npm', 'ci']), ['npm', 'ci']);
	same('command -v', commandWords(['command', '-v', 'npm']), ['npm']);
	same('corepack', commandWords(['corepack', 'pnpm', 'i']), ['pnpm', 'i']);
	same('node npm-cli.js', commandWords(['node', '/usr/lib/node_modules/npm/bin/npm-cli.js', 'install']), ['npm', 'install']);
	same('nodejs npx-cli.js', commandWords(['nodejs', 'npx-cli.js', 'vite']), ['npx', 'vite']);
	const valueOptions = ['--prefix', '-C', '--cache', '--registry', '--userconfig', '--globalconfig', '--loglevel', '-w', '--workspace', '--omit', '--include', '--location', '--script-shell', '--tag', '--otp', '--before', '--cafile', '--ca', '--cert', '--key', '--proxy', '--https-proxy', '--noproxy', '--install-strategy', '--node-options', '--shell', '--editor', '--auth-type', '--scope', '--init-module', '--workspaces-update', '--call', '-c', '--package'];
	for (const option of valueOptions) same(`npm value option ${option}`, npmVerbs(['npm', option, 'ci', 'install']), ['install']);
	const flags = ['-g', '--global', '-f', '--force', '-s', '--silent', '-q', '--quiet', '-d', '-dd', '-ddd', '--verbose', '--no-audit', '--audit', '--no-fund', '--fund', '--ignore-scripts', '--no-ignore-scripts', '--legacy-peer-deps', '--strict-peer-deps', '--prefer-offline', '--prefer-online', '--offline', '--dry-run', '--production', '--no-optional', '--no-save', '-D', '-S', '-E', '-O', '-B', '-P', '--save', '--save-dev', '--save-exact', '--save-optional', '--save-peer', '--no-package-lock', '--package-lock-only', '--foreground-scripts', '--if-present', '--ws', '--workspaces', '--include-workspace-root', '-y', '--yes', '--no', '--color', '--no-color', '--progress', '--no-progress', '--json', '--parseable', '--long', '-l', '--version', '-v', '-h', '--help', '--usage', '--unicode', '--no-update-notifier', '--update-notifier'];
	for (const flag of flags) same(`npm flag ${flag}`, npmVerbs(['npm', flag, 'run', 'install']), ['run-script']);
	same('npm unknown option before a command', npmVerbs(['npm', '--cpu', 'ci', 'install']), ['ci', 'install']);
	const npmGood = 'FROM node AS web\nCOPY package.json package-lock.json ./\nRUN npm ci\n';
	const npmFinds = (name, run, fragment) => {
		const found = lockedNpmProblems(`${npmGood}${run}\n`).join('\n');
		if (fragment === null ? found : !found.includes(fragment)) failures.push(`${name}: ${fragment === null ? `expected clean, got ${found}` : `did not report "${fragment}" (got: ${found || 'nothing'})`}`);
	};
	for (const verb of ['install', 'update', 'install-test', 'link', 'dedupe', 'init']) npmFinds(`npm ${verb} refused`, `RUN npm ${verb} left-pad`, `npm ${verb} may change the tree`);
	for (const tool of ['npx', 'pnpx', 'bunx']) npmFinds(`${tool} refused`, `RUN ${tool} vite build`, `${tool} fetches a package`);
	for (const tool of ['npx', 'pnpx', 'bunx']) npmFinds(`${tool} alone runs nothing`, `RUN ln -s /x /usr/local/bin/${tool}`, null);
	for (const tool of ['yarn', 'yarnpkg', 'pnpm', 'bun']) npmFinds(`${tool} refused`, `RUN ${tool} install`, `${tool} is not used here`);
	for (const [tool, fragment] of [['npm', 'npm install may change'], ['npx', 'npx fetches'], ['yarn', 'yarn is not used'], ['yarnpkg', 'yarnpkg is not used'], ['pnpm', 'pnpm is not used'], ['pnpx', 'pnpx fetches'], ['bun', 'bun is not used'], ['bunx', 'bunx fetches']])
		npmFinds(`${tool} behind sudo`, `RUN sudo -u node ${tool} install`, fragment);
	for (const [name, run] of [
		['su -c', 'RUN su node -c "npm install"'], ['xargs', 'RUN echo left-pad | xargs npm install'], ['find -execdir', 'RUN find . -name package.json -execdir npm install \;'],
		['a redirection first', 'RUN >/dev/null npm install'], ['npm explore', 'RUN npm explore left-pad -- npm install'], ['node on the npm path', 'RUN node /usr/local/bin/npm install'],
		['time -p', 'RUN time -p npm install'], ['exec -a', 'RUN exec -a x npm install'], ['busybox sh', 'RUN busybox sh -c "npm install"'], ['stdbuf', 'RUN stdbuf -oL npm install'],
		['gosu', 'RUN gosu node npm install'], ['su-exec', 'RUN su-exec node npm install'], ['runuser', 'RUN runuser -u node -- npm install'], ['setsid', 'RUN setsid npm install'],
		['ONBUILD', 'ONBUILD RUN npm install'], ['a COPY here-document', 'COPY <<EOF /install.sh\nnpm install\nEOF\nRUN sh /install.sh'],
		['a here-document with a dash in its name', 'RUN <<END-OF\nnpm install\nEND-OF'], ['a quoted here-document name', "RUN <<'EOF'\nnpm install\nEOF"],
		['a here-document after digits', 'RUN cat 0<<EOF | sh\nnpm install\nEOF'],
		['exec form su -c', 'RUN ["su", "node", "-c", "npm install"]'], ['exec form sudo sh -c', 'RUN ["sudo", "sh", "-c", "npm install"]'],
		['exec form tini then sh -c', 'RUN ["tini", "--", "sh", "-c", "npm install"]'], ['exec form busybox sh -c', 'RUN ["busybox", "sh", "-c", "npm install"]']
	]) npmFinds(name, run, 'npm install may change the tree');
	for (const [name, run] of [['apt names npm', 'RUN apt-get install -y nodejs npm'], ['npm version', 'RUN npm --version'], ['the dev container links', 'RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx'], ['npm run with a flag first', 'RUN npm --silent run install']])
		npmFinds(name, run, null);
	same('a quoted <<EOF is not a here-document', dockerInstructions('FROM node\nRUN echo "see <<EOF"\nRUN npm install\nEOF\n').length, 4);
	same('an unterminated here-document takes no lines', dockerInstructions('FROM node\nRUN <<EOF\nRUN npm install\n').length, 3);
	same('an unterminated here-document is reported', unreadableRunProblems('FROM node\nRUN <<EOF\nRUN npm install\n').some((problem) => problem.includes('unterminated here-document (<<EOF)')), true);
	npmFinds('after an unterminated here-document', 'RUN <<EOF\nRUN npm install', 'npm install may change the tree');
	same('a later restore after a dashed here-document', lockedRestoreProblems('FROM sdk\nRUN <<END-OF\ntrue\nEND-OF\nRUN dotnet restore a.csproj\n', () => null).some((problem) => problem.includes('dotnet restore must pass --locked-mode')), true);
	same('SHELL a tool', unreadableRunProblems('FROM node\nSHELL ["/usr/local/bin/npm"]\nRUN install x\n').some((problem) => problem.startsWith('SHELL must name a shell')), true);
	same('SHELL a shell', unreadableRunProblems('FROM node\nSHELL ["bash", "-o", "pipefail", "-c"]\nRUN true\n').length, 0);
	same('SHELL not an array', unreadableRunProblems('FROM node\nSHELL bash -c\n').length, 1);
	same('node on an expansion', unreadableRunProblems('FROM node\nRUN node "$(command -v npm)" install\n').some((problem) => problem.includes('node on a script named by an expansion')), true);
	for (const option of ['--additionalprobingpath', '--additional-deps', '--depsfile', '--runtimeconfig', '--roll-forward', '--fx-version', '--roll-forward-on-no-candidate-fx'])
		same(`dotnet host option ${option}`, dotnetVerb(['dotnet', option, 'restore', 'build']), 'build');
	for (const argument of ['-restore', '/restore', '--restore', '-r', '/r', '-restore:true', '-t:Restore', '/t:restore', '--target:Restore', '-target:Build;Restore', '-t:Restore,Build', '-t:Build,Restore'])
		same(`msbuild ${argument}`, dotnetVerb(['dotnet', 'msbuild', 'a.csproj', argument]), 'restore');
	same('msbuild without a restore', dotnetVerb(['dotnet', 'msbuild', 'a.csproj', '-t:Build', '-p:Restore=x']), 'msbuild');
	if (failures.length) {
		console.error(`base images: RUN reader tables self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

/** ARV-214: the RUN reader itself (dockerInstructions, runCommands, npmVerb, dotnetVerb) and the expansion refusal. */
function selfTestRunCommands() {
	const failures = [];
	const same = (name, actual, expected) => {
		if (JSON.stringify(actual) !== JSON.stringify(expected)) failures.push(`${name}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
	};
	same('a here-string is not a here-document', dockerInstructions('FROM node\nRUN cat <<< "x"\nRUN npm install\nx\n').length, 4);
	same('a here-document joins its instruction', dockerInstructions('FROM node\nRUN <<EOF\nnpm install\nEOF\nRUN true\n').length, 3);
	same('a tab-stripped here-document', dockerInstructions('FROM node\nRUN <<-EOF\n\tnpm install\n\tEOF\nRUN true\n').length, 3);
	same('continuations joined, comments dropped', dockerInstructions('RUN a \\\n# note\n    b\n').map((text) => text.replace(/\s+/g, ' ')), ['RUN a b']);
	same('exec form keeps its words, a word with a space also read as a script', runCommands('RUN ["dotnet", "restore", "my app/App.csproj"]'), [['dotnet', 'restore', 'my app/App.csproj'], ['my', 'app/App.csproj']]);
	same('exec form through a shell', runCommands('RUN ["bash", "-c", "a && npm ci"]'), [['a'], ['npm', 'ci']]);
	same('RUN flags skipped', runCommands('RUN --mount=type=cache,target=/x --network=none npm ci'), [['npm', 'ci']]);
	same('a path names its tool', runCommands('RUN /usr/local/bin/npm ci'), [['npm', 'ci']]);
	same('env and assignments skipped', runCommands('RUN env -u A -C /w B=1 C=2 npm ci'), [['npm', 'ci']]);
	same('npx-cli.js is npx', runCommands('RUN node --no-warnings /usr/lib/node_modules/npm/bin/npx-cli.js vite'), [['npx', 'vite']]);
	same('not RUN', runCommands('CMD ["npm", "install"]'), []);
	for (const [words, verb] of [
		[['npm', 'ci'], 'ci'], [['npm', 'i'], 'install'], [['npm', 'isntall'], 'install'], [['npm', 'insta'], 'install'], [['npm', 'ic'], 'ci'],
		[['npm', '--prefix', 'web', 'install'], 'install'], [['npm', '-C', 'web', 'ci'], 'ci'], [['npm', '--no-audit', 'install'], 'install'],
		[['npm', '--loglevel', 'install', 'ci'], 'ci'], [['npm', '--version'], undefined], [['npm', 'run', 'install'], 'run-script'], [['npm', 'cit'], 'install-ci-test']
	]) same(`npm verb of ${words.join(' ')}`, npmVerb(words), verb);
	for (const [words, verb] of [
		[['dotnet', 'restore', 'a.csproj'], 'restore'], [['dotnet', '-d', 'publish'], 'publish'], [['dotnet', '--roll-forward', 'Major', 'build'], 'build'],
		[['dotnet', 'msbuild', '-restore'], 'restore'], [['dotnet', 'msbuild', '/t:Build;Restore'], 'restore'], [['dotnet', 'msbuild', '-t:Build'], 'msbuild'], [['dotnet', '--version'], undefined]
	]) same(`dotnet verb of ${words.join(' ')}`, dotnetVerb(words), verb);
	for (const [name, dockerfile, expected] of [
		['a variable as the tool', 'FROM node\nRUN $NPM install\n', 1],
		['a braced variable as the tool', 'FROM node\nRUN A=1 ${NPM} ci\n', 1],
		['a command substitution as the tool', 'FROM node\nRUN $(which npm) install\n', 1],
		['backticks as the tool', 'FROM node\nRUN `which npm` install\n', 1],
		['an expansion inside sh -c', 'FROM node\nRUN ["sh", "-c", "$TOOL install"]\n', 1],
		['a glob as the tool', 'FROM node\nRUN /usr/*/npm install\n', 1],
		['a bracket glob as the tool', 'FROM node\nRUN /usr/bin/np[m] install\n', 1],
		['a brace list as the tool', 'FROM node\nRUN /usr/bin/{npm,true} install\n', 1],
		['test brackets are a tool', 'FROM node\nRUN [ -f x ] && [[ -d y ]] || true\n', 0],
		['variables as arguments', 'FROM node\nRUN cp ${A} ${B} && echo $(date) && X=$((1+2)) true\n', 0],
		['exec form expands nothing', 'FROM node\nRUN ["$NPM", "install"]\n', 0]
	]) same(name, unreadableRunProblems(dockerfile).length, expected);
	if (failures.length) {
		console.error(`base images: RUN reader self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

function selfTestLockedRestore() {
	const lock = (runtime) => JSON.stringify({ version: 2, dependencies: { 'net10.0': {}, ...(runtime ? { [`net10.0/${runtime}`]: {} } : {}) } });
	const repo = {
		'src/App/App.csproj': '<Project><ItemGroup><ProjectReference Include="..\\Lib\\Lib.csproj" /></ItemGroup></Project>',
		'src/App/packages.lock.json': lock('linux-x64'),
		'src/Lib/Lib.csproj': '<Project><ItemGroup><ProjectReference Include="../Base/Base.csproj" /></ItemGroup></Project>',
		'src/Lib/packages.lock.json': lock('linux-x64'),
		'src/Base/Base.csproj': '<Project />',
		'src/Base/packages.lock.json': lock('linux-x64')
	};
	const read = (overrides = {}) => (file) => ({ ...repo, ...overrides })[file] ?? null;
	const good = [
		'FROM sdk AS build',
		'COPY ["nuget.config", "Directory.Packages.props", "./"]',
		'COPY ["src/App/App.csproj", "src/App/packages.lock.json", "src/App/"]',
		'COPY ["src/Lib/Lib.csproj", "src/Lib/packages.lock.json", "src/Lib/"]',
		'COPY src/Base/Base.csproj src/Base/packages.lock.json src/Base/',
		'RUN --mount=type=cache,target=/root/.nuget/packages \\',
		'    dotnet restore "src/App/App.csproj" -r linux-x64 --locked-mode',
		'COPY src src',
		'RUN dotnet publish "src/App/App.csproj" -r linux-x64 -c Release -p:RestoreLockedMode=true',
		''
	].join('\n');
	const restore = 'RUN --mount=type=cache,target=/root/.nuget/packages \\\n    dotnet restore "src/App/App.csproj" -r linux-x64 --locked-mode';
	const PUBLISH = 'RUN dotnet publish "src/App/App.csproj" -r linux-x64 -c Release -p:RestoreLockedMode=true';
	if (!good.includes(restore) || !good.includes(PUBLISH)) {
		console.error('base images: locked restore self-test is out of date (its restore line moved)');
		process.exit(1);
	}
	const cases = [
		['good', good, read(), []],
		['not .NET', 'FROM node AS build\nRUN npm ci\n', read(), []],
		['no nuget.config', good.replace('"nuget.config", ', ''), read(), ['nuget.config (package sources and their mapping) must be copied']],
		['restore not locked', good.replace(' --locked-mode', ''), read(), ['dotnet restore must pass --locked-mode']],
		['publish restores freely', good.replace(' -p:RestoreLockedMode=true', ''), read(), ['dotnet publish must hold the restore it runs to the lock files']],
		['publish without a restore', 'FROM sdk\nCOPY . .\nRUN dotnet publish x.csproj\n', read(), ['must restore first, in locked mode']],
		['a project of the closure missing', good.replace('COPY ["src/Lib/Lib.csproj", "src/Lib/packages.lock.json", "src/Lib/"]\n', ''), read(), ['src/Lib/Lib.csproj is in the closure of src/App/App.csproj and must be copied', 'src/Lib/packages.lock.json must be copied beside src/Lib/Lib.csproj']],
		['a lock file not copied', good.replace(' src/Base/packages.lock.json', ''), read(), ['src/Base/packages.lock.json must be copied beside src/Base/Base.csproj']],
		['a lock file without the runtime', good, read({ 'src/Base/packages.lock.json': lock() }), ['src/Base/packages.lock.json has no linux-x64 graph']],
		['a referenced project that does not exist', good, read({ 'src/Base/Base.csproj': '<Project><ItemGroup><ProjectReference Include="../Gone/Gone.csproj" /></ItemGroup></Project>' }), ['src/Gone/Gone.csproj (in the closure of src/App/App.csproj) does not exist']],
		// ARV-214: the RUN forms the reader reads.
		['exec form restore not locked', good.replace(restore, 'RUN ["dotnet", "restore", "src/App/App.csproj", "-r", "linux-x64"]'), read(), ['dotnet restore must pass --locked-mode']],
		['exec form restore locked', good.replace(restore, 'RUN ["dotnet", "restore", "src/App/App.csproj", "-r", "linux-x64", "--locked-mode"]'), read(), []],
		['exec form publish restores freely', good.replace('RUN dotnet publish "src/App/App.csproj" -r linux-x64 -c Release -p:RestoreLockedMode=true', 'RUN ["dotnet", "publish", "src/App/App.csproj", "-c", "Release"]'), read(), ['dotnet publish must hold the restore it runs to the lock files']],
		['exec form restore through sh -c', good.replace(restore, 'RUN ["sh", "-c", "dotnet restore src/App/App.csproj -r linux-x64"]'), read(), ['dotnet restore must pass --locked-mode']],
		['tool named by a path', good.replace(restore, 'RUN /usr/share/dotnet/dotnet restore "src/App/App.csproj" -r linux-x64'), read(), ['dotnet restore must pass --locked-mode']],
		['env prefix', good.replace(restore, 'RUN env DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet restore "src/App/App.csproj" -r linux-x64'), read(), ['dotnet restore must pass --locked-mode']],
		['VAR=value prefix on publish', good.replace(' -p:RestoreLockedMode=true', '').replace('RUN dotnet publish', 'RUN DOTNET_NOLOGO=1 dotnet publish'), read(), ['dotnet publish must hold the restore it runs to the lock files']],
		['a host option before the verb', good.replace(restore, 'RUN dotnet --diagnostics restore "src/App/App.csproj" -r linux-x64'), read(), ['dotnet restore must pass --locked-mode']],
		['the locked property on restore', good.replace(restore, 'RUN dotnet restore "src/App/App.csproj" -r linux-x64 -p:RestoreLockedMode=true'), read(), []],
		['msbuild restore', good.replace(restore, 'RUN dotnet msbuild "src/App/App.csproj" -t:Restore -p:RuntimeIdentifier=linux-x64'), read(), ['dotnet restore must pass --locked-mode']],
		['test restores on its own', good.replace('RUN dotnet publish "src/App/App.csproj" -r linux-x64 -c Release -p:RestoreLockedMode=true', 'RUN dotnet test "src/App/App.csproj"'), read(), ['dotnet test must hold the restore it runs to the lock files']],
		['runtime written with =', good.replace(restore, 'RUN dotnet restore "src/App/App.csproj" --runtime=linux-x64 --locked-mode'), read({ 'src/Base/packages.lock.json': lock() }), ['src/Base/packages.lock.json has no linux-x64 graph']],
		['lower case run', good.replace(restore, 'run dotnet restore "src/App/App.csproj" -r linux-x64'), read(), ['dotnet restore must pass --locked-mode']],
		['COPY with flags', good.replace('COPY src/Base/Base.csproj', 'COPY --chown=10001:10001 --link src/Base/Base.csproj'), read(), []],
		...['publish', 'build', 'run', 'test', 'pack', 'format'].map((verb) => [`${verb} restores on its own`, good.replace(PUBLISH, `RUN dotnet ${verb} "src/App/App.csproj"`), read(), [`dotnet ${verb} must hold the restore it runs to the lock files`]]),
		...['publish', 'test', 'run', 'pack'].map((verb) => [`${verb} --no-build restores nothing`, good.replace(PUBLISH, `RUN dotnet ${verb} "src/App/App.csproj" --no-build`), read(), []]),
		...[['dnx', 'RUN dnx dotnetsay'], ['tool install', 'RUN dotnet tool install -g dotnet-ef'], ['tool exec', 'RUN dotnet tool exec dotnet-ef'], ['tool restore', 'RUN dotnet tool restore'], ['tool update', 'RUN dotnet tool update -g dotnet-ef'], ['tool execute', 'RUN dotnet tool execute dotnet-ef'],
			['add package', 'RUN dotnet add src/App/App.csproj package Newtonsoft.Json'], ['package add', 'RUN dotnet package add Newtonsoft.Json'], ['package update', 'RUN dotnet package update'],
			['workload install', 'RUN dotnet workload install wasm-tools'], ['workload update', 'RUN dotnet workload update'], ['workload restore', 'RUN dotnet workload restore'], ['workload repair', 'RUN dotnet workload repair']
		].map(([name, run]) => [name, good.replace(PUBLISH, `${PUBLISH}\n${run}`), read(), ['fetches or changes packages the lock files do not name']]),
		['dnx alone runs nothing', good.replace(PUBLISH, `${PUBLISH}\nRUN ln -s /x /usr/local/bin/dnx`), read(), []],
		['dnx behind sudo', good.replace(PUBLISH, `${PUBLISH}\nRUN sudo -u app dnx dotnetsay`), read(), ['dnx fetches or changes packages the lock files do not name']],
		['locked mode turned off after it', good.replace(restore, 'RUN dotnet restore "src/App/App.csproj" -r linux-x64 --locked-mode -p:RestoreLockedMode=false'), read(), ['dotnet restore must not turn locked mode off']],
		['locked mode turned off on publish', good.replace(PUBLISH, `${PUBLISH} -p:RestoreLockedMode=false`), read(), ['dotnet publish must not turn locked mode off']],
		['a source on the command line', good.replace(restore, 'RUN dotnet restore "src/App/App.csproj" -r linux-x64 --locked-mode --source https://example.invalid/v3/index.json'), read(), ['dotnet restore must take its package sources from nuget.config alone']],
		['a config file on the command line', good.replace(PUBLISH, `${PUBLISH} --configfile other.config`), read(), ['dotnet publish must take its package sources from nuget.config alone']],
		['-s on restore', good.replace(restore, 'RUN dotnet restore "src/App/App.csproj" -r linux-x64 --locked-mode -s https://example.invalid/'), read(), ['must take its package sources from nuget.config alone']],
		['-s on test is its settings', good.replace(PUBLISH, 'RUN dotnet test "src/App/App.csproj" --no-restore -s test.runsettings'), read(), []],
		['restore and publish both wrapped', good.replace(restore, 'RUN gosu app dotnet restore "src/App/App.csproj" -r linux-x64').replace(PUBLISH, 'RUN gosu app dotnet publish "src/App/App.csproj"'), read(), ['dotnet restore must pass --locked-mode', 'dotnet publish must hold the restore']]
	];
	const failures = [];
	for (const [name, text, reader, expected] of cases) {
		const found = lockedRestoreProblems(text, reader).join('\n');
		if (!expected.length && found) failures.push(`${name}: expected clean, got ${found}`);
		for (const fragment of expected) if (!found.includes(fragment)) failures.push(`${name}: did not report "${fragment}" (got: ${found || 'nothing'})`);
	}
	if (failures.length) {
		console.error(`base images: locked restore self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

function playwrightTag() {
	const lock = JSON.parse(fs.readFileSync(path.join(ROOT, 'Platform', 'Testing', 'Ariva.E2E', 'package-lock.json'), 'utf8'));
	return `v${lock.packages['node_modules/@playwright/test'].version}-noble`;
}

function dockerfiles(dir = ROOT, out = []) {
	for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
		if (SKIP.includes(entry.name)) continue;
		const full = path.join(dir, entry.name);
		if (entry.isDirectory()) dockerfiles(full, out);
		// ARV-214: Dockerfile.<name> and <name>.Dockerfile too (none today), so a variant is not left unchecked.
		else if (/^Dockerfile(\.[^/]+)?$|\.Dockerfile$/i.test(entry.name)) out.push(full);
	}
	return out;
}

/** FROM lines that reference a registry image (not a previous build stage). */
function fromLines(file) {
	const text = fs.readFileSync(file, 'utf8');
	const stages = new Set();
	const found = [];
	text.split(/\r?\n/).forEach((line, index) => {
		const m = line.match(/^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?/i);
		if (!m) return;
		const ref = m[1];
		if (!stages.has(ref.toLowerCase())) {
			const [nameTag, digest] = ref.split('@');
			const lastSlash = nameTag.lastIndexOf('/');
			const colon = nameTag.indexOf(':', lastSlash + 1);
			const image = colon === -1 ? nameTag : nameTag.slice(0, colon);
			const tag = colon === -1 ? '' : nameTag.slice(colon + 1);
			found.push({ file, line: index + 1, ref, image, tag, digest: digest ?? '' });
		}
		if (m[2]) stages.add(m[2].toLowerCase());
	});
	return found;
}

/**
 * ARV-076 (CWE-269): the dev container's privilege posture. Problems for a Dockerfile text and a parsed
 * devcontainer.json; checked on the repository's dev container and self-tested on a good and a bad sample.
 */
export function devcontainerProblems(dockerfile, config) {
	const problems = [];
	const users = [...dockerfile.matchAll(/^\s*USER\s+(\S+)/gim)].map((m) => m[1]);
	if (!users.length || ['root', '0'].includes(users.at(-1).split(':')[0])) problems.push('the Dockerfile must end on a non-root USER');
	if (/\bsudo\b/i.test(dockerfile.split(/\r?\n/).filter((line) => !line.trim().startsWith('#')).join('\n'))) problems.push('the Dockerfile must not install or use sudo');
	for (const key of ['remoteUser', 'containerUser']) {
		if (['root', '0'].includes(config[key])) problems.push(`${key} must not be root`);
	}
	if (!config.remoteUser) problems.push('remoteUser must name the non-root user');
	for (const arg of config.runArgs ?? []) {
		if (/^--(privileged|cap-add|security-opt|pid=host|userns=host|device)/.test(arg)) problems.push(`runArgs must not grant privileges (${arg})`);
	}
	if (config.privileged === true || (config.capAdd ?? []).length) problems.push('the dev container must not be privileged or add capabilities');
	for (const [feature, options] of Object.entries(config.features ?? {})) {
		if (!/@sha256:[0-9a-f]{64}$/.test(feature)) problems.push(`feature ${feature} must be pinned by digest`);
		if (feature.includes('/docker-outside-of-docker@')) {
			const cli = dockerfile.match(/^\s*FROM\s+docker:(\d+\.\d+\.\d+)-cli@/m)?.[1];
			if (options?.version !== cli) problems.push(`docker-outside-of-docker version ${options?.version} must be the image's Docker CLI ${cli}`);
			if (options?.moby !== false) problems.push('docker-outside-of-docker must set moby false (the pinned upstream CLI)');
		}
		if (/\/docker-in-docker[@:]/.test(feature)) problems.push('docker-in-docker needs a privileged container; use docker-outside-of-docker');
	}
	return problems;
}

/**
 * ARV-184 (CWE-1395): a Dockerfile on runtime-deps publishes self-contained, so what it ships is the SDK's runtime pack, not
 * the base's. It must check the publish output with scripts/published-runtime.mjs, giving --runtime its runtime-deps tag, in a
 * named stage, and its final stage must copy from that stage only. Problems for a Dockerfile text (none without runtime-deps).
 */
export function publishedRuntimeProblems(dockerfile) {
	const base = dockerfile.match(/^\s*FROM\s+mcr\.microsoft\.com\/dotnet\/runtime-deps:([^@\s]+)@/m)?.[1];
	if (!base) return [];
	const stages = dockerfile.split(/^(?=\s*FROM\s)/m).filter((stage) => /^\s*FROM\s/.test(stage))
		.map((body) => ({ name: body.match(/^\s*FROM\s+\S+\s+AS\s+(\S+)/i)?.[1]?.toLowerCase(), body }));
	const problems = [];
	const checks = stages.filter((stage) => /^\s*RUN\s+node\s+\/check\/published-runtime\.mjs\s+--check\s+\/app\/publish\s+--runtime\s+\S+\s*$/m.test(stage.body));
	if (checks.length !== 1) return [`one stage must run "node /check/published-runtime.mjs --check /app/publish --runtime ${base}" (found ${checks.length})`];
	const checked = checks[0];
	const runtime = checked.body.match(/--runtime\s+(\S+)/)[1];
	if (runtime !== base) problems.push(`the published runtime check uses --runtime ${runtime}; it must be the runtime-deps tag ${base}, with the SDK that bundles it`);
	if (!/^\s*COPY\s+.*scripts\/published-runtime\.mjs.*\/check\/\S*\s*$/m.test(checked.body)) problems.push('the check stage must copy scripts/published-runtime.mjs to /check/');
	if (!checked.name) problems.push('the check stage must be named (FROM ... AS checked)');
	const final = stages.at(-1);
	const sources = [...final.body.matchAll(/^\s*COPY\s+--from=(\S+)/gim)].map((m) => m[1].toLowerCase());
	if (final === checked || !checked.name || !sources.includes(checked.name) || sources.some((source) => source !== checked.name)) {
		problems.push(`the final stage must copy the app from the check stage only (copies from: ${sources.join(', ') || 'nothing'})`);
	}
	return problems;
}

function selfTestPublishedRuntime() {
	const digest = '@sha256:' + '0'.repeat(64);
	const good = `FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.13${digest} AS base\nFROM mcr.microsoft.com/dotnet/sdk:10.0.402${digest} AS build\nFROM build AS publish\nRUN dotnet publish -o /app/publish\nFROM node:22.23.3-alpine3.24${digest} AS checked\nCOPY ["scripts/published-runtime.mjs", "/check/"]\nCOPY --from=publish /app/publish /app/publish\nRUN node /check/published-runtime.mjs --check /app/publish --runtime 10.0.13\nFROM base AS final\nCOPY --from=checked /app/publish .\nUSER 10001\n`;
	const cases = [
		['good', good, []],
		['no runtime-deps base', 'FROM node:22.23.3-alpine3.24\nCOPY . .\n', []],
		['no check stage', good.replace(/^RUN node \/check.*$/m, 'RUN true'), ['one stage must run']],
		['stale --runtime', good.replace('--runtime 10.0.13', '--runtime 10.0.12'), ['--runtime 10.0.12; it must be the runtime-deps tag 10.0.13']],
		['base moved alone', good.replace('runtime-deps:10.0.13', 'runtime-deps:10.0.14'), ['must be the runtime-deps tag 10.0.14']],
		['script not copied', good.replace('COPY ["scripts/published-runtime.mjs", "/check/"]\n', ''), ['must copy scripts/published-runtime.mjs']],
		['final ships the unchecked output', good.replace('COPY --from=checked /app/publish .', 'COPY --from=publish /app/publish .'), ['from the check stage only (copies from: publish)']],
		['final copies from both', good.replace('COPY --from=checked /app/publish .', 'COPY --from=checked /app/publish .\nCOPY --from=build /src/x .'), ['copies from: checked, build']],
		['unnamed check stage', good.replace(' AS checked', '').replace('--from=checked', '--from=2'), ['must be named']]
	];
	const failures = [];
	for (const [name, text, expected] of cases) {
		const found = publishedRuntimeProblems(text).join('\n');
		if (!expected.length && found) failures.push(`${name}: expected clean, got ${found}`);
		for (const fragment of expected) if (!found.includes(fragment)) failures.push(`${name}: did not report "${fragment}" (got: ${found || 'nothing'})`);
	}
	if (failures.length) {
		console.error(`base images: published runtime self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

/** JSON with // comments (devcontainer.json). Comments start a line or follow a comma, a bracket or a brace. */
function parseJsonc(text) {
	return JSON.parse(text.split(/\r?\n/).map((line) => line.replace(/^\s*\/\/.*$/, '')).join('\n'));
}

function selfTestDevcontainer() {
	const digest = '@sha256:' + '0'.repeat(64);
	const goodFile = `FROM docker:29.4.3-cli${digest} AS docker\nFROM base${digest}\nUSER pwuser\n`;
	const good = { remoteUser: 'pwuser', runArgs: ['--network=host'], features: { [`ghcr.io/devcontainers/features/docker-outside-of-docker${digest}`]: { moby: false, version: '29.4.3' } } };
	const badFile = 'FROM base:1\nRUN apt-get install -y sudo\nUSER 0:root\n';
	const bad = { remoteUser: 'root', containerUser: 'root', privileged: true, capAdd: ['NET_ADMIN'], runArgs: ['--privileged', '--cap-add=SYS_ADMIN', '--security-opt=seccomp=unconfined', '--pid=host', '--userns=host', '--device=/dev/fuse'], features: { 'ghcr.io/devcontainers/features/docker-in-docker:2': {}, [`ghcr.io/devcontainers/features/docker-outside-of-docker${digest}`]: { version: '28.0.0' } } };
	const found = devcontainerProblems(badFile, bad).join('\n');
	const expected = ['non-root USER', 'sudo', 'remoteUser must not be root', 'containerUser must not be root', '(--privileged)', '(--cap-add', '(--security-opt', '(--pid=host)', '(--userns=host)', '(--device', 'must not be privileged or add capabilities', 'pinned by digest', 'docker-in-docker', 'version 28.0.0', 'moby false'];
	const missing = expected.filter((rule) => !found.includes(rule));
	const clean = devcontainerProblems(goodFile, good);
	if (missing.length || clean.length) {
		console.error(`base images: dev container self-test failed; did not fire: ${missing.join(', ') || 'none'}; good sample: ${clean.join('; ') || 'clean'}`);
		process.exit(1);
	}
}

/**
 * ARV-127: the jobs of one workflow that can sign as it and run a package manager. A job can sign when its permissions give
 * it the OIDC token (id-token: write, or write-all), or when it has no permissions of its own and the workflow's do. An
 * install script or a build plugin in such a job could ask for the token and sign what it likes (images.yml builds inside
 * docker, which has no access to the token). Only run steps are read: a package manager started by a uses: action, by bun
 * or corepack, or through a node_modules/.bin path is not seen. Job keys are expected two spaces under jobs:, as in every
 * workflow here; a workflow indented otherwise is not read.
 */
export function signingJobProblems(text) {
	const code = text.replace(/\r\n/g, '\n').split('\n').map((line) => (/^\s*#/.test(line) ? '' : line.replace(/\s#\s.*$/, ''))).join('\n');
	const jobsAt = code.search(/^jobs:\s*$/m);
	if (jobsAt < 0) return [];
	const signs = (block, indent) => {
		const lines = block.split('\n');
		const at = lines.findIndex((line) => new RegExp(`^ {${indent}}permissions:`).test(line));
		if (at < 0) return null;
		const inline = lines[at].slice(indent + 'permissions:'.length).trim();
		if (inline) return /^["']?write-all["']?$/.test(inline) || /id-token["']?:\s*["']?write\b/.test(inline);
		const body = [];
		for (const line of lines.slice(at + 1)) {
			if (line.trim() === '') continue;
			if (line.match(/^ */)[0].length <= indent) break;
			body.push(line);
		}
		return body.some((line) => /^\s+["']?id-token["']?:\s*["']?write\b/.test(line));
	};
	const runBodies = (job) => {
		const lines = job.split('\n');
		const bodies = [];
		for (let i = 0; i < lines.length; i++) {
			const step = lines[i].match(/^(\s*)(-\s+)?run:\s*(.*)$/);
			if (!step) continue;
			const indent = step[1].length + (step[2] ? step[2].length : 0);
			if (step[3] && !/^[|>][-+]?$/.test(step[3])) {
				bodies.push(step[3]);
				continue;
			}
			for (const line of lines.slice(i + 1)) {
				if (line.trim() === '') continue;
				if (line.match(/^ */)[0].length <= indent) break;
				bodies.push(line);
			}
		}
		return bodies.join('\n');
	};
	const workflowSigns = signs(code.slice(0, jobsAt), 0) === true;
	const problems = [];
	for (const job of code.slice(jobsAt).split(/\n(?= {2}[A-Za-z0-9_-]+:[ \t]*$)/m).slice(1)) {
		const name = job.match(/^ {2}([A-Za-z0-9_-]+):/)?.[1];
		const own = signs(job, 4);
		if ((own ?? workflowSigns) && /(^|[\s;&|(`'"])(npm|npx|yarn|pnpm)(\s|$)/m.test(runBodies(job))) {
			problems.push(`job ${name} can sign (id-token: write${own === null ? ', from the workflow' : ''}) and runs a package manager; build in another job`);
		}
	}
	return problems;
}

function selfTestSigningJobs() {
	const job = (name, permissions, run) => `  ${name}:\n    runs-on: ubuntu-24.04\n${permissions}    steps:\n      - uses: actions/setup-node@x # v7\n        with:\n          cache: npm\n${run}`;
	const block = (run) => `      - name: step\n        run: |\n          ${run}\n`;
	const signing = '    permissions:\n      contents: read\n      id-token: write\n';
	const cases = [
		['job id-token, npm', `on: push\njobs:\n${job('build', signing, block('npm ci'))}`, ['job build can sign']],
		['job write-all, pnpm', `on: push\njobs:\n${job('build', '    permissions: write-all\n', block('pnpm install'))}`, ['job build can sign']],
		['job flow map, npx', `on: push\njobs:\n${job('build', '    permissions: { id-token: write }\n', '      - run: npx vite build\n')}`, ['job build can sign']],
		['quoted values, npm', `on: push\njobs:\n${job('build', '    permissions:\n      "id-token": "write"\n', block('npm ci'))}${job('all', "    permissions: 'write-all'\n", block('npm ci'))}`, ['job build can sign', 'job all can sign']],
		['workflow id-token, yarn', `on: push\npermissions:\n  id-token: write\njobs:\n${job('web', '', block('cd web && yarn build'))}`, ['job web can sign (id-token: write, from the workflow)']],
		['workflow write-all, npm', `on: push\npermissions: write-all\njobs:\n${job('web', '', block('npm run build'))}`, ['job web can sign (id-token: write, from the workflow)']],
		['job narrows the workflow', `on: push\npermissions:\n  id-token: write\njobs:\n${job('web', '    permissions:\n      contents: read\n', block('npm ci'))}`, []],
		['npm only in a job that cannot sign', `on: push\npermissions:\n  contents: read\njobs:\n${job('web', '', block('npm ci'))}${job('sign', signing, block('cosign sign-blob x'))}`, []],
		['npm only in comments and the cache input', `on: push\njobs:\n${job('sign', signing, '      # npm ci would go here\n      - run: cosign sign-blob x # not npm ci\n')}`, []]
	];
	const failures = [];
	for (const [name, text, expected] of cases) {
		const found = signingJobProblems(text).join('\n');
		if (!expected.length && found) failures.push(`${name}: expected clean, got ${found}`);
		for (const fragment of expected) if (!found.includes(fragment)) failures.push(`${name}: did not report "${fragment}" (got: ${found || 'nothing'})`);
	}
	if (failures.length) {
		console.error(`base images: signing job self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
}

function check() {
	const problems = [];
	let count = 0;
	let runtimeChecked = 0;
	let lockedRestores = 0;
	let lockedNpmInstalls = 0;
	let playwrightFrom = false;
	selfTestDevcontainer();
	selfTestPublishedRuntime();
	selfTestSigningJobs();
	selfTestRunCommands();
	selfTestCommandTables();
	selfTestPipelinePins();
	selfTestLockedRestore();
	selfTestLockedNpm();
	selfTestOutsideImages();
	selfTestThirdPartyImages();
	const readRepo = (file) => {
		const full = path.join(ROOT, ...file.split('/'));
		return fs.existsSync(full) ? fs.readFileSync(full, 'utf8') : null;
	};
	const devDockerfile = path.join(ROOT, '.devcontainer', 'Dockerfile');
	const devConfig = path.join(ROOT, '.devcontainer', 'devcontainer.json');
	if (fs.existsSync(devConfig)) {
		for (const problem of devcontainerProblems(fs.readFileSync(devDockerfile, 'utf8'), parseJsonc(fs.readFileSync(devConfig, 'utf8')))) {
			problems.push(`.devcontainer: ${problem}`);
		}
	}
	for (const file of dockerfiles()) {
		const runtimeProblems = publishedRuntimeProblems(fs.readFileSync(file, 'utf8'));
		for (const problem of runtimeProblems) problems.push(`${path.relative(ROOT, file)}: ${problem}`);
		if (/^\s*FROM\s+mcr\.microsoft\.com\/dotnet\/runtime-deps:/m.test(fs.readFileSync(file, 'utf8')) && !runtimeProblems.length) runtimeChecked++;
		// ARV-096: a .NET restore in an image build is locked, from nuget.config, with the whole closure and its lock files.
		const text = fs.readFileSync(file, 'utf8');
		const commands = dockerRunCommands(text).map(({ words }) => words);
		const restoreProblems = lockedRestoreProblems(text, readRepo);
		for (const problem of restoreProblems) problems.push(`${path.relative(ROOT, file)}: ${problem}`);
		if (commands.some((words) => words[0] === 'dotnet' && dotnetVerb(words) === 'restore') && !restoreProblems.length) lockedRestores++;
		// ARV-096: an npm install in an image build is npm ci from the committed package-lock.json.
		const npmProblems = lockedNpmProblems(text);
		for (const problem of npmProblems) problems.push(`${path.relative(ROOT, file)}: ${problem}`);
		if (commands.some((words) => words[0] === 'npm' && npmVerb(words) === 'ci') && !npmProblems.length) lockedNpmInstalls++;
		// ARV-214: a RUN command named by an expansion could be either, unseen.
		for (const problem of unreadableRunProblems(text)) problems.push(`${path.relative(ROOT, file)}: ${problem}`);
		for (const from of fromLines(file)) {
			count++;
			const where = `${path.relative(ROOT, file)}:${from.line}`;
			const line = LINES[from.image];
			if (!line) problems.push(`${where}: ${from.image} is not a known base image line (add it to LINES in scripts/base-images.mjs)`);
			else if (!line.patterns.some((pattern) => pattern.test(from.tag))) problems.push(`${where}: ${from.ref} must use a full version tag matching ${line.patterns.join(' or ')}`);
			else if (line.follow === 'playwright' && from.tag !== playwrightTag()) problems.push(`${where}: ${from.ref} must be ${playwrightTag()}, the @playwright/test version of Ariva.E2E`);
			if (!/^sha256:[0-9a-f]{64}$/.test(from.digest)) problems.push(`${where}: ${from.ref} must be pinned by digest (@sha256:...)`);
			if (from.image === 'alpine/helm') {
				const ci = fs.readFileSync(path.join(ROOT, '.github', 'workflows', 'ci.yml'), 'utf8').match(/setup-helm@[^\n]*\n\s*with:\s*\n\s*version:\s*v(\d+\.\d+\.\d+)/)?.[1];
				if (ci !== from.tag) problems.push(`${where}: Helm ${from.tag} must be CI's Helm (${ci ?? 'not found in ci.yml'})`);
			}
			if (from.image === 'mcr.microsoft.com/playwright') {
				playwrightFrom = true;
				const visual = fs.readFileSync(VISUAL_BROWSER, 'utf8').match(/image = '(mcr\.microsoft\.com\/playwright@sha256:[0-9a-f]{64})'/)?.[1];
				if (visual !== `${from.image}@${from.digest}`) problems.push(`${where}: scripts/visual-browser.mjs renders with ${visual ?? 'no pinned image'}; it must use this digest`);
			}
		}
	}
	// ARV-096: the visual baselines' browser is checked against the dev container's Playwright line; without that line its pin
	// would go unchecked, so the line itself is required.
	if (!playwrightFrom && fs.existsSync(VISUAL_BROWSER)) problems.push('scripts/visual-browser.mjs pins the Playwright image against the dev container\'s FROM mcr.microsoft.com/playwright line, which is missing');
	// ARV-002: the GitHub dev release renders and applies with CI's Helm, as the Azure DevOps release does. ARV-127: so does every
	// other CI job and the offline bundle, which packages the charts; and every workflow that signs or verifies uses one cosign.
	{
		const workflow = (file) => {
			const full = path.join(ROOT, '.github', 'workflows', file);
			return fs.existsSync(full) ? fs.readFileSync(full, 'utf8') : null;
		};
		const helmsOf = (text) => [...text.matchAll(/setup-helm@[^\n]*\n\s*with:\s*\n\s*version:\s*v(\d+\.\d+\.\d+)/g)].map((m) => m[1]);
		const ci = helmsOf(workflow('ci.yml') ?? '')[0];
		for (const file of ['ci.yml', 'release-dev.yml', 'offline-bundle.yml']) {
			const text = workflow(file);
			if (text === null) continue;
			const helms = helmsOf(text);
			if (helms.length === 0 || helms.some((version) => version !== ci)) problems.push(`.github/workflows/${file}: Helm ${helms.join(', ') || 'not found'} must be CI's Helm (${ci ?? 'not found in ci.yml'})`);
		}
		const cosigns = ['images.yml', 'release-dev.yml', 'offline-bundle.yml'].flatMap((file) => [...(workflow(file) ?? '').matchAll(/cosign-release:\s*'?(v[\d.]+)'?/g)].map((m) => `${file} ${m[1]}`));
		if (new Set(cosigns.map((entry) => entry.split(' ')[1])).size > 1) problems.push(`the workflows must use one cosign release: ${cosigns.join(', ')}`);
		// ARV-127: no job that can sign as its workflow runs a package manager (signingJobProblems).
		for (const file of fs.readdirSync(path.join(ROOT, '.github', 'workflows')).filter((name) => /\.ya?ml$/.test(name))) {
			for (const problem of signingJobProblems(workflow(file))) problems.push(`.github/workflows/${file}: ${problem}`);
		}
	}
	// ARV-214: the pipelines that run the integration tests pin Testcontainers' Ryuk image, and CI builds with the images' SDK band.
	const ciTexts = outsideFiles().filter(([file, kind]) => kind === 'yaml' && file !== 'docker-compose.dev.yml').map(([file]) => [file.split(path.sep).join('/'), fs.readFileSync(path.join(ROOT, file), 'utf8')]);
	for (const problem of ryukProblems([...ciTexts, ...RYUK_RUNNERS.filter((file) => !ciTexts.some(([name]) => name === file)).map((file) => [file, readRepo(file)])])) problems.push(problem);
	{
		const sdkTags = dockerfiles().flatMap(fromLines).filter((from) => from.image === 'mcr.microsoft.com/dotnet/sdk').map((from) => from.tag);
		for (const problem of sdkPinProblems(sdkTags, ciTexts)) problems.push(problem);
	}
	// ARV-214: the local tools and the Aspire SDK, which NuGet resolves by version without a lock file, are exact and one release.
	for (const problem of toolPinProblems(toolManifests().map((file) => [file, readRepo(file)]), readRepo(APPHOST_PROJECT), readRepo('Directory.Packages.props'))) problems.push(problem);
	// ARV-062: the cluster's TimescaleDB runs the image the developers, the integration tests and CI run, pinned by digest.
	const databaseValues = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.K8s', 'Helm', 'Charts', 'timescaledb', 'values.yaml');
	if (fs.existsSync(databaseValues)) {
		const values = fs.readFileSync(databaseValues, 'utf8');
		const field = (name) => values.match(new RegExp(`^\\s+${name}:\\s*(\\S+)`, 'm'))?.[1];
		const compose = fs.readFileSync(path.join(ROOT, 'docker-compose.dev.yml'), 'utf8').match(/image:\s*timescale\/timescaledb-ha:([^@\s]+)/)?.[1];
		if (field('repository') !== 'timescale/timescaledb-ha' || field('tag') !== compose) {
			problems.push(`Charts/timescaledb/values.yaml: image ${field('repository')}:${field('tag')} must be timescale/timescaledb-ha:${compose}, the tag of docker-compose.dev.yml`);
		}
		if (!/^sha256:[0-9a-f]{64}$/.test(field('digest') ?? '')) problems.push('Charts/timescaledb/values.yaml: image.digest must be a sha256 digest');
		// ARV-227: the chart's appVersion names the database line it deploys (helm list), so it moves with the image tag.
		const chartFile = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.K8s', 'Helm', 'Charts', 'timescaledb', 'Chart.yaml');
		const appVersion = fs.existsSync(chartFile) ? fs.readFileSync(chartFile, 'utf8').match(/^appVersion:\s*["']?([^"'\s]+)/m)?.[1] : undefined;
		if (appVersion !== field('tag')) problems.push(`Charts/timescaledb/Chart.yaml: appVersion ${appVersion} must be the image tag ${field('tag')} of values.yaml`);
	}
	// ARV-092c: the scanner beside api-main runs the ClamAV image docker-compose.dev.yml, the AppHost and CI run, pinned by digest
	// (every other copy is held to the chart's tag and digest below).
	const scanner = chartScanner();
	if (scanner === null) problems.push('Charts/platform/values.yaml: no uploadScan.image block with repository, tag and digest');
	else {
		if (scanner.repository !== 'clamav/clamav' || !OUTSIDE_LINES['clamav/clamav'].patterns.some((pattern) => pattern.test(scanner.tag))) {
			problems.push(`Charts/platform/values.yaml: uploadScan.image ${scanner.repository}:${scanner.tag} must be clamav/clamav on a tag of its line (${OUTSIDE_LINES['clamav/clamav'].patterns.join(' or ')})`);
		}
		if (!/^sha256:[0-9a-f]{64}$/.test(scanner.digest ?? '')) problems.push('Charts/platform/values.yaml: uploadScan.image.digest must be a sha256 digest');
	}
	// ARV-063: the dynamic scan's images. ZAP is pinned by digest in the scan and in its workflow, the same in both; the web
	// app is scanned under the nginx image its Dockerfile builds on.
	const zapSpec = path.join(ROOT, 'Platform', 'Testing', 'Ariva.E2E', 'tests', 'zap', 'zap.spec.ts');
	if (fs.existsSync(zapSpec)) {
		const spec = fs.readFileSync(zapSpec, 'utf8');
		const specZap = spec.match(/zapImage = '([^']+)'/)?.[1];
		const workflowZap = fs.readFileSync(path.join(ROOT, '.github', 'workflows', 'security-zap.yml'), 'utf8').match(/ZAP_IMAGE:\s*(\S+)/)?.[1];
		if (!/^ghcr\.io\/zaproxy\/zaproxy@sha256:[0-9a-f]{64}$/.test(specZap ?? '')) problems.push(`tests/zap/zap.spec.ts: the ZAP image ${specZap} must be pinned by digest`);
		if (specZap !== workflowZap) problems.push(`security-zap.yml: ZAP_IMAGE ${workflowZap} must be the scan's ${specZap}`);
		const specNginx = spec.match(/nginxImage = '([^']+)'/)?.[1];
		const webNginx = fs.readFileSync(path.join(ROOT, 'Platform', 'Frontplane', 'Ariva.Web', 'Dockerfile'), 'utf8').match(/^FROM\s+(nginx:\S+)/m)?.[1];
		if (specNginx !== webNginx) problems.push(`tests/zap/zap.spec.ts: nginxImage ${specNginx} must be the web image's base ${webNginx}`);
	}
	// ARV-096: the third-party images run outside the chart, pinned by tag and digest with one digest per tag (OUTSIDE_LINES), the
	// integration tests' constants included (INTEGRATION_IMAGES), and no image named by a literal in the integration tests.
	const pins = outsidePins();
	for (const problem of outsideImageProblems(pins, { chart: chartDatabase(), scanner: scanner ?? undefined, tools: toolVersions(), tagOnly: APPHOST_TAG_ONLY })) problems.push(problem);
	// ARV-227: every other copy of a third-party pin carries its digest, and the weekly watch scans a change to any pin file.
	const thirdParty = thirdPartyPins();
	const copies = otherCopies(thirdParty);
	for (const problem of copyProblems(copies, thirdParty)) problems.push(problem);
	const watch = path.join(ROOT, '.github', 'workflows', 'third-party-images.yml');
	if (!fs.existsSync(watch)) problems.push('.github/workflows/third-party-images.yml is missing: the weekly scan of the third-party images (ARV-227)');
	else for (const problem of watchPathProblems([...new Set(thirdParty.map((pin) => pin.where.replace(/:\d+$/, '')))], workflowPaths(fs.readFileSync(watch, 'utf8')))) problems.push(problem);
	for (const file of integrationSources()) {
		for (const problem of integrationImageLiterals(file.split(path.sep).join('/'), fs.readFileSync(path.join(ROOT, file), 'utf8'))) problems.push(problem);
	}
	if (problems.length) {
		console.error(`base images: ${problems.length} problem(s) in ${count} FROM lines and ${pins.length} other image pins\n  ${problems.join('\n  ')}`);
		console.error('Run the base-images workflow (Actions, base-images, Run workflow) to get pinned lines.');
		process.exit(1);
	}
	const tagOnly = pins.filter((pin) => pin.resource && APPHOST_TAG_ONLY[pin.resource] && !pin.digest).map((pin) => `${pin.resource} (${APPHOST_TAG_ONLY[pin.resource]})`);
	const integration = pins.filter((pin) => pin.where.startsWith(`${INTEGRATION_TESTS.split(path.sep).join('/')}/`)).length;
	console.log(`base images: ${count} FROM lines pinned by version and digest; dev container non-root and unprivileged; ${runtimeChecked} runtime-deps images check their published runtime against their base; ${lockedRestores} image builds restore in locked mode with their whole closure; ${lockedNpmInstalls} image builds install npm packages with npm ci from their lock file; ${pins.length} images run outside the chart pinned by tag and digest (${integration} of them the integration tests')${tagOnly.length ? `, except on a tag: ${tagOnly.join(', ')}` : ''}; ${copies.length} further copies of third-party pins carry their digests; the ${RYUK_RUNNERS.length} pipelines that run the integration tests pin Testcontainers' Ryuk image; CI's .NET SDK steps name the images' SDK band; the local tools and the Aspire SDK are exact versions of one release.`);
}

/** The TimescaleDB chart's image tag and digest, the pin every other copy follows (null without the chart). */
function chartDatabase() {
	const file = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.K8s', 'Helm', 'Charts', 'timescaledb', 'values.yaml');
	if (!fs.existsSync(file)) return null;
	const values = fs.readFileSync(file, 'utf8');
	const field = (name) => values.match(new RegExp(`^\\s+${name}:\\s*["']?([^\\s"']+)`, 'm'))?.[1];
	return { tag: field('tag'), digest: field('digest') };
}

/** ARV-092c: the platform chart's ClamAV image (uploadScan.image): repository, tag and digest, the pin every other copy follows (null without it). */
function chartScanner() {
	const file = path.join(ROOT, 'Platform', 'Cloud', 'Ariva.K8s', 'Helm', 'Charts', 'platform', 'values.yaml');
	if (!fs.existsSync(file)) return null;
	const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
	const start = lines.indexOf('uploadScan:');
	if (start === -1) return null;
	const end = lines.findIndex((line, index) => index > start && /^\S/.test(line));
	const block = lines.slice(start + 1, end === -1 ? undefined : end);
	const image = block.indexOf('  image:');
	if (image === -1) return null;
	const field = (offset, name) => block[image + offset]?.match(new RegExp(`^    ${name}:\\s*"?([^"\\s]+)"?\\s*$`))?.[1];
	const found = { repository: field(1, 'repository'), tag: field(2, 'tag'), digest: field(3, 'digest') };
	return Object.values(found).every(Boolean) ? found : null;
}

/** The versions of the local .NET tools (.config/dotnet-tools.json) by package id. */
function toolVersions() {
	const file = path.join(ROOT, '.config', 'dotnet-tools.json');
	if (!fs.existsSync(file)) return {};
	return Object.fromEntries(Object.entries(JSON.parse(fs.readFileSync(file, 'utf8')).tools ?? {}).map(([id, tool]) => [id, tool.version]));
}

async function tagsFor(image) {
	const line = LINES[image];
	if (line.follow === 'playwright') return [playwrightTag()];
	if (line.list === 'ghcr') {
		const auth = await fetch(`https://ghcr.io/token?scope=repository:${line.ghcrName}:pull`);
		if (!auth.ok) throw new Error(`ghcr.io token for ${line.ghcrName}: ${auth.status}`);
		const { token } = await auth.json();
		const list = await fetch(`https://ghcr.io/v2/${line.ghcrName}/tags/list?n=10000`, { headers: { Authorization: `Bearer ${token}` } });
		if (!list.ok) throw new Error(`ghcr.io tags of ${line.ghcrName}: ${list.status}`);
		return (await list.json()).tags ?? [];
	}
	if (line.list === 'mcr') {
		const repo = image.replace('mcr.microsoft.com/', '');
		// The registry pages tag lists; follow the Link header so the newest patch is never missed.
		const tags = [];
		let url = `https://mcr.microsoft.com/v2/${repo}/tags/list?n=1000`;
		for (let page = 0; url && page < 50; page++) {
			const res = await fetch(url);
			tags.push(...((await res.json()).tags ?? []));
			const next = res.headers.get('link')?.match(/<([^>]+)>;\s*rel="next"/)?.[1];
			url = next ? new URL(next, 'https://mcr.microsoft.com').toString() : null;
		}
		return tags;
	}
	const names = [];
	let url = `https://hub.docker.com/v2/repositories/${line.hubName}/tags?page_size=100&name=${encodeURIComponent(line.hubFilter)}`;
	for (let page = 0; url && page < 10; page++) {
		const body = await (await fetch(url)).json();
		names.push(...(body.results ?? []).map((r) => r.name));
		url = hubNextPage(body.next);
	}
	return names;
}

/** ARV-227: Docker Hub's next page of a tag list, followed only on hub.docker.com (a registry answer never steers a request elsewhere). */
function hubNextPage(next) {
	if (!next) return null;
	if (new URL(next).origin !== 'https://hub.docker.com') throw new Error('Docker Hub named a next page off hub.docker.com');
	return next;
}

/** The current digest of an outside line's tag: Docker Hub's tags API (no pull, so no pull rate limit) or the tag's manifest on ghcr.io. */
async function registryDigest(line, tag) {
	if (line.hubName) {
		const res = await fetch(`https://hub.docker.com/v2/repositories/${line.hubName}/tags/${encodeURIComponent(tag)}`);
		if (!res.ok) throw new Error(`Docker Hub tag ${line.hubName}:${tag}: ${res.status}`);
		return (await res.json()).digest;
	}
	const auth = await fetch(`https://ghcr.io/token?scope=repository:${line.ghcrName}:pull`);
	if (!auth.ok) throw new Error(`ghcr.io token for ${line.ghcrName}: ${auth.status}`);
	const { token } = await auth.json();
	const accept = ['application/vnd.oci.image.index.v1+json', 'application/vnd.docker.distribution.manifest.list.v2+json', 'application/vnd.oci.image.manifest.v1+json', 'application/vnd.docker.distribution.manifest.v2+json'].join(', ');
	const res = await fetch(`https://ghcr.io/v2/${line.ghcrName}/manifests/${encodeURIComponent(tag)}`, { method: 'HEAD', headers: { Authorization: `Bearer ${token}`, Accept: accept } });
	if (!res.ok) throw new Error(`ghcr.io ${line.ghcrName}:${tag}: ${res.status}`);
	return res.headers.get('docker-content-digest');
}

/** ARV-227: the tags of an outside line that start with prefix (Docker Hub filters by name; ghcr.io lists every tag). */
async function registryTags(line, prefix) {
	if (line.hubName) {
		const names = [];
		let url = `https://hub.docker.com/v2/repositories/${line.hubName}/tags?page_size=100&name=${encodeURIComponent(prefix)}`;
		for (let page = 0; url && page < 10; page++) {
			const res = await fetch(url);
			if (!res.ok) throw new Error(`Docker Hub tags of ${line.hubName}: ${res.status}`);
			const body = await res.json();
			names.push(...(body.results ?? []).map((r) => r.name));
			url = hubNextPage(body.next);
		}
		return names.filter((name) => name.startsWith(prefix));
	}
	const auth = await fetch(`https://ghcr.io/token?scope=repository:${line.ghcrName}:pull`);
	if (!auth.ok) throw new Error(`ghcr.io token for ${line.ghcrName}: ${auth.status}`);
	const { token } = await auth.json();
	const list = await fetch(`https://ghcr.io/v2/${line.ghcrName}/tags/list?n=10000`, { headers: { Authorization: `Bearer ${token}` } });
	if (!list.ok) throw new Error(`ghcr.io tags of ${line.ghcrName}: ${list.status}`);
	return ((await list.json()).tags ?? []).filter((name) => name.startsWith(prefix));
}

/**
 * ARV-096: the current digest of each image and tag pinned outside the chart, from Docker Hub's tags API (no pull, so no pull
 * rate limit) or the manifest of its tag on ghcr.io (Toxiproxy; ZAP's stable tag). With write, every copy of an old digest moves
 * to the new one: compose, the workflows, Azure DevOps, the AppHost, the integration tests' constants, the scan (zap.spec.ts)
 * and, for TimescaleDB and ClamAV, the charts, so the check keeps holding them to one digest. The tags themselves stay (they are
 * the lines the team chose).
 */
async function resolveOutside(write) {
	const pins = outsidePins();
	const thirdParty = thirdPartyPins();
	const copies = otherCopies(thirdParty);
	if (write) refuseThirdPartyProblems();
	const groups = new Map();
	for (const pin of pins.filter((p) => p.image && Object.hasOwn(OUTSIDE_LINES, p.image))) {
		const key = `${pin.image}:${pin.tag}`;
		if (!groups.has(key)) groups.set(key, { image: pin.image, tag: pin.tag, digests: new Set() });
		if (/^sha256:[0-9a-f]{64}$/.test(pin.digest ?? '')) groups.get(key).digests.add(pin.digest);
	}
	const moves = [];
	for (const [key, group] of groups) {
		const line = OUTSIDE_LINES[group.image];
		// ZAP follows its stable tag; any other image keeps the tag it is pinned on.
		const digest = await registryDigest(line, line.resolveTag ?? group.tag);
		if (!/^sha256:[0-9a-f]{64}$/.test(digest ?? '')) throw new Error(`${key}: no digest from the registry`);
		console.log(`${key.padEnd(40)} ${group.image}${group.tag ? `:${group.tag}` : ''}@${digest}${line.resolveTag ? ` (${line.resolveTag})` : ''}${group.digests.has(digest) ? '' : ' (new)'}`);
		for (const old of group.digests) if (old !== digest) moves.push([old.slice('sha256:'.length), digest.slice('sha256:'.length)]);
	}
	if (!write || !moves.length) return;
	const targets = [...outsideFiles().map(([file]) => file), path.join('Platform', 'Cloud', 'Ariva.K8s', 'Helm', 'Charts', 'timescaledb', 'values.yaml'),
		path.join('Platform', 'Cloud', 'Ariva.K8s', 'Helm', 'Charts', 'platform', 'values.yaml'), ...new Set(copies.map((copy) => copy.where.replace(/:\d+$/, '')))];
	for (const file of targets.filter((target) => fs.existsSync(path.join(ROOT, target)))) {
		const text = fs.readFileSync(path.join(ROOT, file), 'utf8');
		const next = moves.reduce((current, [from, to]) => current.split(from).join(to), text);
		if (next !== text) fs.writeFileSync(path.join(ROOT, file), next);
	}
}

/**
 * ARV-227: throws when the third-party pins or their copies have a problem --check reports (a tag changed in some places only, a
 * malformed digest, a stale copy), which a write would spread to the wrong copies. Called before anything is written.
 */
function refuseThirdPartyProblems() {
	const thirdParty = thirdPartyPins();
	const problems = [...outsideImageProblems(outsidePins(), { chart: chartDatabase(), scanner: chartScanner() ?? undefined, tools: toolVersions(), tagOnly: APPHOST_TAG_ONLY }), ...copyProblems(otherCopies(thirdParty), thirdParty)];
	if (problems.length) throw new Error(`nothing written: ${problems.length} problem(s) in the third-party pins (node scripts/base-images.mjs --check)\n  ${problems.join('\n  ')}`);
}

/** ARV-227: the third-party images pinned outside the Dockerfiles (pinnedImages over this repository). */
function thirdPartyImages() {
	const pins = thirdPartyPins();
	// An untagged copy (an expectation holding image@digest) is listed under the tag of the pin whose digest it carries.
	const copies = otherCopies(pins).map((copy) => (copy.tag ? copy : { ...copy, tag: pins.find((pin) => pin.image === copy.image && pin.digest === copy.digest)?.tag ?? '' }));
	return pinnedImages([...outsidePins(), ...visualBrowserPins(), ...copies], { chart: chartDatabase(), scanner: chartScanner() });
}

/** ARV-227: every third-party pin: the outside files', the visual baselines' Playwright image and the charts' TimescaleDB and ClamAV. */
function thirdPartyPins() {
	return [...outsidePins(), ...visualBrowserPins(), ...chartPins(chartDatabase(), chartScanner())];
}

/** ARV-227: the copies of third-party pins in the repository (COPY_ROOTS and compose) other than the pins themselves. */
function otherCopies(pins) {
	const images = new Set(pins.map((pin) => pin.image).filter(Boolean));
	const pinned = new Set(pins.map((pin) => pin.where));
	const copies = [];
	const read = (file) => copies.push(...imageCopies(file.split(path.sep).join('/'), fs.readFileSync(path.join(ROOT, file), 'utf8'), images));
	const walk = (relative) => {
		const full = path.join(ROOT, relative);
		if (!fs.existsSync(full)) return;
		for (const entry of fs.readdirSync(full, { withFileTypes: true })) {
			if (COPY_SKIP.includes(entry.name)) continue;
			const child = path.join(relative, entry.name);
			// This script's own self-test values are not copies.
			if (entry.isDirectory()) walk(child);
			else if (COPY_NAMES.test(entry.name) && child !== path.join('scripts', 'base-images.mjs')) read(child);
		}
	};
	for (const root of COPY_ROOTS) walk(root);
	for (const name of fs.readdirSync(ROOT).filter((file) => /^docker-compose.*\.ya?ml$/.test(file))) read(name);
	return copies.filter((copy) => !pinned.has(copy.where));
}

/**
 * The Playwright image CI renders the visual baselines with: pinned by digest in visual-browser.mjs, its tag on the dev container's
 * FROM line (check() holds the two to one digest). Scanned with the others; its newer releases come from Dependabot, with
 * @playwright/test.
 */
function visualBrowserPins() {
	const devcontainer = path.join(ROOT, '.devcontainer', 'Dockerfile');
	if (!fs.existsSync(devcontainer) || !fs.existsSync(VISUAL_BROWSER)) return [];
	const from = fromLines(devcontainer).find((f) => f.image === 'mcr.microsoft.com/playwright');
	if (!from) return [];
	const line = fs.readFileSync(VISUAL_BROWSER, 'utf8').split(/\r?\n/).findIndex((text) => /image = 'mcr\.microsoft\.com\/playwright@sha256:/.test(text));
	return [
		{ where: `.devcontainer/Dockerfile:${from.line}`, image: from.image, tag: from.tag, digest: from.digest },
		...(line === -1 ? [] : [{ where: `Platform/Testing/Ariva.E2E/scripts/visual-browser.mjs:${line + 1}`, image: from.image, tag: from.tag, digest: from.digest }])
	];
}

/** --list: one line per image, tag and digest with every place it is pinned; with json, the matrix of third-party-images.yml. */
function list(json) {
	const images = thirdPartyImages();
	if (json) {
		console.log(JSON.stringify(images.map(({ name, ref, where }) => ({ name, ref, where }))));
		return;
	}
	for (const image of images) console.log(`${image.ref}\n    ${image.where}`);
}

/**
 * --patches (ARV-227): each third-party line pinned outside the Dockerfiles against its registry (patchStatus). Behind when a
 * version tag x.y.z has a newer patch on x.y, when the pinned tag (ZAP: its stable tag) now names another digest (an image
 * rebuilt or patched under a moving tag such as redis:8.2-alpine), or when a line that retires has a newer line (TimescaleDB);
 * another newer line is a note. Exit 1 when one is behind or cannot be read; the bump is wiki 04's "Third-party images".
 */
async function patches() {
	let behind = 0;
	for (const image of thirdPartyImages()) {
		const line = Object.hasOwn(OUTSIDE_LINES, image.image) ? OUTSIDE_LINES[image.image] : null;
		if (!line) {
			console.log(`-      ${image.name.padEnd(48)} newer releases through Dependabot (the dev container's FROM lines)`);
			continue;
		}
		let result;
		try {
			const prefix = tagLine(image.tag)?.prefix;
			result = patchStatus(image, line, { tags: prefix ? await registryTags(line, prefix) : [], digest: await registryDigest(line, line.resolveTag ?? image.tag) });
		} catch (error) {
			result = { status: 'ERROR', notes: [error.message] };
		}
		if (result.status !== 'ok') behind++;
		console.log(`${result.status.padEnd(6)} ${image.name.padEnd(48)} ${result.notes.join('; ')}`);
	}
	console.log(behind ? `\n${behind} pinned line(s) behind their registry or not readable: bump every copy at once (wiki 04, "Third-party images").` : '\nEvery pinned line is current.');
	if (behind) process.exitCode = 1;
}

const versionKey = (tag) => tag.split(/[.-]/).map((part) => (/^\d+$/.test(part) ? part.padStart(6, '0') : part.replace(/\d+/g, (d) => d.padStart(6, '0')))).join('.');

async function resolve(write) {
	// One newest tag per variant: image and pattern index.
	const latest = {};
	for (const image of Object.keys(LINES)) {
		const all = await tagsFor(image);
		for (const [index, pattern] of LINES[image].patterns.entries()) {
			const tags = all.filter((tag) => pattern.test(tag));
			if (!tags.length) continue;
			const tag = tags.sort((a, b) => versionKey(a).localeCompare(versionKey(b))).at(-1);
			const manifest = JSON.parse(execFileSync('docker', ['buildx', 'imagetools', 'inspect', `${image}:${tag}`, '--format', '{{json .Manifest}}'], { encoding: 'utf8' }));
			latest[`${image}#${index}`] = `${image}:${tag}@${manifest.digest}`;
			console.log(`${image.padEnd(40)} ${latest[`${image}#${index}`]}`);
		}
	}
	if (!write) return;
	for (const file of dockerfiles()) {
		let text = fs.readFileSync(file, 'utf8');
		for (const from of fromLines(file)) {
			const index = LINES[from.image]?.patterns.findIndex((pattern) => pattern.test(from.tag)) ?? -1;
			const next = latest[`${from.image}#${index}`];
			if (next) text = text.replace(from.ref, next);
			// ARV-184: the published runtime check follows the runtime-deps tag (the image build then proves the SDK bundles it).
			if (next && from.image === 'mcr.microsoft.com/dotnet/runtime-deps') {
				text = text.replace(/(published-runtime\.mjs --check \S+ --runtime )\S+/, `$1${next.split('@')[0].slice(from.image.length + 1)}`);
			}
			if (next && from.image === 'mcr.microsoft.com/playwright') {
				const visual = fs.readFileSync(VISUAL_BROWSER, 'utf8');
				fs.writeFileSync(VISUAL_BROWSER, visual.replace(/image = 'mcr\.microsoft\.com\/playwright@sha256:[0-9a-f]{64}'/, `image = '${next.replace(/:v[^@]+@/, '@')}'`));
			}
		}
		fs.writeFileSync(file, text);
	}
}

const args = process.argv.slice(2);
if (args.includes('--list')) list(args.includes('--json'));
else if (args.includes('--patches')) await patches();
else if (args.includes('--resolve')) {
	try {
		if (args.includes('--write')) refuseThirdPartyProblems();
		if (!args.includes('--outside')) await resolve(args.includes('--write'));
		await resolveOutside(args.includes('--write'));
	} catch (error) {
		console.error(`base images: ${error.message}`);
		process.exit(1);
	}
} else check();
