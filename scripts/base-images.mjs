// Base image pinning for every Dockerfile (ARV-002, supply chain), the dev container's included (ARV-076), and the
// third-party images run outside the chart (ARV-096: the local stack, the CI service and job containers, the Azure DevOps E2E
// database, the dynamic scan, the visual baselines' browser and the integration tests' Testcontainers images).
//   node scripts/base-images.mjs --check      every FROM uses a full version tag plus @sha256 digest, every image run
//                                             outside the chart is pinned by tag and digest with one digest per tag, and
//                                             every .NET restore in a Dockerfile is locked (offline; CI gate)
//   node scripts/base-images.mjs --resolve    finds the newest patch of each pinned line and its digest, and the current
//                                             digest of each image run outside the chart (needs registry access and
//                                             docker buildx; runs in .github/workflows/base-images.yml)
//   node scripts/base-images.mjs --resolve --write   also rewrites the FROM lines and every copy of the other pins
// Dependabot (docker ecosystem) keeps the Dockerfile digests current once they are pinned.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
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
	'timescale/timescaledb-ha': { patterns: [/^pg17-ts2\.\d+$/], hubName: 'timescale/timescaledb-ha', chart: true },
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
	'ghcr.io/shopify/toxiproxy': { patterns: [/^2\.\d+\.\d+$/], ghcrName: 'shopify/toxiproxy' }
};

/**
 * AppHost resources allowed on a tag, each with the reason (resource name: reason). None today: TimescaleDB carries the chart's
 * digest since AppHostModelTests reads the digest (Aspire drops the tag once a digest is set and runs image@sha256).
 */
const APPHOST_TAG_ONLY = {};

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
		}
	}
	for (const [key, byDigest] of digests) {
		if (byDigest.size > 1) problems.push(`${key} is pinned to ${byDigest.size} digests; pin one everywhere (${[...byDigest].map(([digest, where]) => `${digest.slice(7, 19)} in ${where.join(', ')}`).join('; ')})`);
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
 * ARV-096 (ASVS 5.0 V15.1.2, supply chain): a Dockerfile that restores NuGet packages restores what the lock files name, from
 * the repository's sources. Problems for a Dockerfile text, given read(path) for repository files (null when missing):
 * nuget.config is copied before the first restore; every dotnet restore passes --locked-mode; every dotnet publish or build
 * holds the restore it runs to the lock files (-p:RestoreLockedMode=true) or skips it (--no-restore); the projects copied
 * before a restore are the restored project's whole closure (ProjectReference, followed), each with its packages.lock.json
 * beside it; and each of those lock files records the runtime the restore names (-r linux-x64 needs RuntimeIdentifiers).
 */
export function lockedRestoreProblems(dockerfile, read) {
	const lines = dockerfile.replace(/\\\r?\n/g, ' ').split(/\r?\n/);
	const isRestore = (line) => /^\s*RUN\b.*\bdotnet\s+restore\b/.test(line);
	const builds = lines.filter((line) => /^\s*RUN\b.*\bdotnet\s+(publish|build)\b/.test(line));
	const restores = lines.filter(isRestore);
	if (!restores.length && !builds.length) return [];
	const problems = [];
	const first = lines.findIndex(isRestore);
	const copied = new Set();
	for (const line of lines.slice(0, first < 0 ? lines.length : first)) {
		const args = line.match(/^\s*COPY\s+(?!--from)(.+)$/i)?.[1]?.trim();
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
	if (!restores.length) problems.push('a Dockerfile that builds .NET must restore first, in locked mode (dotnet restore --locked-mode)');
	else if (!copied.has('nuget.config')) problems.push('nuget.config (package sources and their mapping) must be copied before the restore');
	for (const restore of restores) {
		if (!/\s--locked-mode\b/.test(restore)) problems.push(`dotnet restore must pass --locked-mode (${restore.trim().replace(/^RUN\s+(--mount=\S+\s+)*/, '')})`);
	}
	for (const build of builds) {
		if (!/\s(--no-restore|-p:RestoreLockedMode=true)\b/.test(build)) problems.push('dotnet publish must hold the restore it runs to the lock files (-p:RestoreLockedMode=true) or skip it (--no-restore)');
	}
	for (const restore of restores) {
		const project = restore.match(/\bdotnet\s+restore\s+"?([^"\s]+\.csproj)"?/)?.[1];
		if (!project) {
			problems.push('dotnet restore must name the project it restores');
			continue;
		}
		const runtime = restore.match(/\s(?:-r|--runtime)\s+(\S+)/)?.[1];
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
	return problems;
}

/**
 * ARV-096 (ASVS 5.0 V15.1.2): a Dockerfile that installs npm packages installs exactly what its package-lock.json names.
 * Problems for a Dockerfile text: any install that may resolve on its own (npm install, i, add, update, or yarn and pnpm,
 * which have no lock file here) is refused, and every npm ci needs a package-lock.json copied before it, so that it
 * cannot run against a lock file that is not there.
 */
export function lockedNpmProblems(dockerfile) {
	const lines = dockerfile.replace(/\\\r?\n/g, ' ').split(/\r?\n/);
	const problems = [];
	let lockCopied = false;
	for (const line of lines) {
		// Each stage starts with what its FROM gives it: a lock file copied into another stage does not count.
		if (/^\s*FROM\s/i.test(line)) lockCopied = false;
		const copy = line.match(/^\s*COPY\s+(?!--from)(.+)$/i)?.[1];
		if (copy && /(^|[\s"/])package-lock\.json\b/.test(copy)) lockCopied = true;
		if (!/^\s*RUN\b/.test(line)) continue;
		// Commands chained by &&, ||, ;, a pipe, or wrapped in sh -c or bash -c (quotes dropped, so the wrapped text is read too).
		const body = line.replace(/^\s*RUN\s+(--mount=\S+\s+)*/, '').replace(/\b(?:sh|bash|ash)\s+-c\s+/g, '').replace(/["']/g, ' ');
		for (const command of body.split(/&&|;|\|\||\|/)) {
			const words = command.trim().split(/\s+/);
			const tool = words[0];
			if (tool === 'npx' || (tool === 'npm' && ['exec', 'x'].includes(words.find((word, index) => index > 0 && !word.startsWith('-'))))) {
				problems.push(`${tool === 'npx' ? 'npx' : 'npm exec'} fetches a package the lock file does not name: run it from package-lock.json's tree (${command.trim()})`);
				continue;
			}
			const verb = words.find((word, index) => index > 0 && !word.startsWith('-'));
			if (tool === 'npm' && ['install', 'i', 'add', 'update', 'up', 'upgrade', 'isntall', 'in', 'ins', 'inst', 'insta', 'instal', 'isnt', 'isnta', 'isntal'].includes(verb))
				problems.push(`npm ${verb} may change the tree the lock file names: install with npm ci from package-lock.json (${command.trim()})`);
			else if (tool === 'npm' && ['ci', 'clean-install', 'install-clean', 'isntall-clean'].includes(verb) && !lockCopied)
				problems.push('npm ci needs package-lock.json copied before it');
			else if (tool === 'yarn' || tool === 'pnpm') problems.push(`${tool} is not used here: install with npm ci from package-lock.json (${command.trim()})`);
		}
	}
	return problems;
}

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
		['npm i', good.replace('npm ci', 'npm i'), ['npm i may change the tree']],
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
		['after a pipe', good.replace('npm run build --prod', 'echo ok | npm i left-pad'), ['npm i may change the tree']]
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
		['a referenced project that does not exist', good, read({ 'src/Base/Base.csproj': '<Project><ItemGroup><ProjectReference Include="../Gone/Gone.csproj" /></ItemGroup></Project>' }), ['src/Gone/Gone.csproj (in the closure of src/App/App.csproj) does not exist']]
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
		else if (entry.name === 'Dockerfile') out.push(full);
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
	selfTestLockedRestore();
	selfTestLockedNpm();
	selfTestOutsideImages();
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
		const restoreProblems = lockedRestoreProblems(fs.readFileSync(file, 'utf8'), readRepo);
		for (const problem of restoreProblems) problems.push(`${path.relative(ROOT, file)}: ${problem}`);
		if (/\bdotnet\s+restore\b/.test(fs.readFileSync(file, 'utf8')) && !restoreProblems.length) lockedRestores++;
		// ARV-096: an npm install in an image build is npm ci from the committed package-lock.json.
		const npmProblems = lockedNpmProblems(fs.readFileSync(file, 'utf8'));
		for (const problem of npmProblems) problems.push(`${path.relative(ROOT, file)}: ${problem}`);
		if (/\bnpm\s+ci\b/.test(fs.readFileSync(file, 'utf8')) && !npmProblems.length) lockedNpmInstalls++;
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
	console.log(`base images: ${count} FROM lines pinned by version and digest; dev container non-root and unprivileged; ${runtimeChecked} runtime-deps images check their published runtime against their base; ${lockedRestores} image builds restore in locked mode with their whole closure; ${lockedNpmInstalls} image builds install npm packages with npm ci from their lock file; ${pins.length} images run outside the chart pinned by tag and digest (${integration} of them the integration tests')${tagOnly.length ? `, except on a tag: ${tagOnly.join(', ')}` : ''}.`);
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
		url = body.next;
	}
	return names;
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
	const groups = new Map();
	for (const pin of pins.filter((p) => p.image && OUTSIDE_LINES[p.image])) {
		const key = `${pin.image}:${pin.tag}`;
		if (!groups.has(key)) groups.set(key, { image: pin.image, tag: pin.tag, digests: new Set() });
		if (pin.digest) groups.get(key).digests.add(pin.digest);
	}
	const moves = [];
	for (const [key, group] of groups) {
		const line = OUTSIDE_LINES[group.image];
		let digest;
		if (line.hubName) {
			const res = await fetch(`https://hub.docker.com/v2/repositories/${line.hubName}/tags/${encodeURIComponent(group.tag)}`);
			if (!res.ok) throw new Error(`Docker Hub tag ${line.hubName}:${group.tag}: ${res.status}`);
			digest = (await res.json()).digest;
		} else {
			const auth = await fetch(`https://ghcr.io/token?scope=repository:${line.ghcrName}:pull`);
			if (!auth.ok) throw new Error(`ghcr.io token for ${line.ghcrName}: ${auth.status}`);
			const { token } = await auth.json();
			const accept = ['application/vnd.oci.image.index.v1+json', 'application/vnd.docker.distribution.manifest.list.v2+json', 'application/vnd.oci.image.manifest.v1+json', 'application/vnd.docker.distribution.manifest.v2+json'].join(', ');
			// ZAP follows its stable tag; any other ghcr.io image keeps the tag it is pinned on.
			const tag = line.resolveTag ?? group.tag;
			const res = await fetch(`https://ghcr.io/v2/${line.ghcrName}/manifests/${encodeURIComponent(tag)}`, { method: 'HEAD', headers: { Authorization: `Bearer ${token}`, Accept: accept } });
			if (!res.ok) throw new Error(`ghcr.io ${line.ghcrName}:${tag}: ${res.status}`);
			digest = res.headers.get('docker-content-digest');
		}
		if (!/^sha256:[0-9a-f]{64}$/.test(digest ?? '')) throw new Error(`${key}: no digest from the registry`);
		console.log(`${key.padEnd(40)} ${group.image}${group.tag ? `:${group.tag}` : ''}@${digest}${line.resolveTag ? ` (${line.resolveTag})` : ''}${group.digests.has(digest) ? '' : ' (new)'}`);
		for (const old of group.digests) if (old !== digest) moves.push([old.slice('sha256:'.length), digest.slice('sha256:'.length)]);
	}
	if (!write || !moves.length) return;
	const targets = [...outsideFiles().map(([file]) => file), path.join('Platform', 'Cloud', 'Ariva.K8s', 'Helm', 'Charts', 'timescaledb', 'values.yaml'),
		path.join('Platform', 'Cloud', 'Ariva.K8s', 'Helm', 'Charts', 'platform', 'values.yaml'), path.join('Platform', 'Testing', 'Ariva.E2E', 'tests', 'zap', 'zap.spec.ts')];
	for (const file of targets.filter((target) => fs.existsSync(path.join(ROOT, target)))) {
		const text = fs.readFileSync(path.join(ROOT, file), 'utf8');
		const next = moves.reduce((current, [from, to]) => current.split(from).join(to), text);
		if (next !== text) fs.writeFileSync(path.join(ROOT, file), next);
	}
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
if (args.includes('--resolve')) {
	await resolve(args.includes('--write'));
	await resolveOutside(args.includes('--write'));
} else check();
