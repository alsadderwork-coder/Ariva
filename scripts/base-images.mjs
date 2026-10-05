// Base image pinning for every Dockerfile (ARV-002, supply chain), the dev container's included (ARV-076).
//   node scripts/base-images.mjs --check      every FROM uses a full version tag plus @sha256 digest (offline; CI gate)
//   node scripts/base-images.mjs --resolve    finds the newest patch of each pinned line and its digest (needs registry
//                                             access and docker buildx; runs in .github/workflows/base-images.yml)
//   node scripts/base-images.mjs --resolve --write   also rewrites the FROM lines
// Dependabot (docker ecosystem) keeps the digests current once they are pinned.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SKIP = ['node_modules', '.git', 'bin', 'obj', 'fixtures', 'build', '.svelte-kit'];

/**
 * The version line each base image follows, and how to recognise a full version tag of it. A line may have several
 * variants (patterns): --resolve keeps each FROM on the variant it uses. The Playwright line follows the version of
 * @playwright/test in Ariva.E2E's package-lock.json rather than the newest tag, since its browsers must match it.
 */
const LINES = {
	'mcr.microsoft.com/dotnet/runtime-deps': { patterns: [/^10\.0\.\d+$/], list: 'mcr' },
	'mcr.microsoft.com/dotnet/aspnet': { patterns: [/^10\.0\.\d+$/], list: 'mcr' },
	'mcr.microsoft.com/dotnet/sdk': { patterns: [/^10\.0\.1\d\d$/], list: 'mcr' },
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

function check() {
	const problems = [];
	let count = 0;
	selfTestDevcontainer();
	const devDockerfile = path.join(ROOT, '.devcontainer', 'Dockerfile');
	const devConfig = path.join(ROOT, '.devcontainer', 'devcontainer.json');
	if (fs.existsSync(devConfig)) {
		for (const problem of devcontainerProblems(fs.readFileSync(devDockerfile, 'utf8'), parseJsonc(fs.readFileSync(devConfig, 'utf8')))) {
			problems.push(`.devcontainer: ${problem}`);
		}
	}
	for (const file of dockerfiles()) {
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
				const visual = fs.readFileSync(VISUAL_BROWSER, 'utf8').match(/image = '(mcr\.microsoft\.com\/playwright@sha256:[0-9a-f]{64})'/)?.[1];
				if (visual !== `${from.image}@${from.digest}`) problems.push(`${where}: scripts/visual-browser.mjs renders with ${visual ?? 'no pinned image'}; it must use this digest`);
			}
		}
	}
	if (problems.length) {
		console.error(`base images: ${problems.length} problem(s) in ${count} FROM lines\n  ${problems.join('\n  ')}`);
		console.error('Run the base-images workflow (Actions, base-images, Run workflow) to get pinned lines.');
		process.exit(1);
	}
	console.log(`base images: ${count} FROM lines pinned by version and digest; dev container non-root and unprivileged.`);
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
			if (next && from.image === 'mcr.microsoft.com/playwright') {
				const visual = fs.readFileSync(VISUAL_BROWSER, 'utf8');
				fs.writeFileSync(VISUAL_BROWSER, visual.replace(/image = 'mcr\.microsoft\.com\/playwright@sha256:[0-9a-f]{64}'/, `image = '${next.replace(/:v[^@]+@/, '@')}'`));
			}
		}
		fs.writeFileSync(file, text);
	}
}

const args = process.argv.slice(2);
if (args.includes('--resolve')) await resolve(args.includes('--write'));
else check();
