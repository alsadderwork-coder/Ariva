// Base image pinning for every Dockerfile (ARV-002, supply chain).
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

/** The version line each base image follows, and how to recognise a full version tag of it. */
const LINES = {
	'mcr.microsoft.com/dotnet/runtime-deps': { pattern: /^10\.0\.\d+$/, list: 'mcr' },
	'mcr.microsoft.com/dotnet/aspnet': { pattern: /^10\.0\.\d+$/, list: 'mcr' },
	'mcr.microsoft.com/dotnet/sdk': { pattern: /^10\.0\.1\d\d$/, list: 'mcr' },
	nginx: { pattern: /^1\.28\.\d+$/, list: 'hub', hubName: 'library/nginx', hubFilter: '1.28.' },
	node: { pattern: /^22\.\d+\.\d+-alpine\d+\.\d+$/, list: 'hub', hubName: 'library/node', hubFilter: '22.' }
};

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

function check() {
	const problems = [];
	let count = 0;
	for (const file of dockerfiles()) {
		for (const from of fromLines(file)) {
			count++;
			const where = `${path.relative(ROOT, file)}:${from.line}`;
			const line = LINES[from.image];
			if (!line) problems.push(`${where}: ${from.image} is not a known base image line (add it to LINES in scripts/base-images.mjs)`);
			else if (!line.pattern.test(from.tag)) problems.push(`${where}: ${from.ref} must use a full version tag matching ${line.pattern}`);
			if (!/^sha256:[0-9a-f]{64}$/.test(from.digest)) problems.push(`${where}: ${from.ref} must be pinned by digest (@sha256:...)`);
		}
	}
	if (problems.length) {
		console.error(`base images: ${problems.length} problem(s) in ${count} FROM lines\n  ${problems.join('\n  ')}`);
		console.error('Run the base-images workflow (Actions, base-images, Run workflow) to get pinned lines.');
		process.exit(1);
	}
	console.log(`base images: ${count} FROM lines pinned by version and digest.`);
}

async function tagsFor(image) {
	const line = LINES[image];
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
	const latest = {};
	for (const image of Object.keys(LINES)) {
		const tags = (await tagsFor(image)).filter((tag) => LINES[image].pattern.test(tag));
		if (!tags.length) continue;
		const tag = tags.sort((a, b) => versionKey(a).localeCompare(versionKey(b))).at(-1);
		const manifest = JSON.parse(execFileSync('docker', ['buildx', 'imagetools', 'inspect', `${image}:${tag}`, '--format', '{{json .Manifest}}'], { encoding: 'utf8' }));
		latest[image] = `${image}:${tag}@${manifest.digest}`;
		console.log(`${image.padEnd(40)} ${latest[image]}`);
	}
	if (!write) return;
	for (const file of dockerfiles()) {
		let text = fs.readFileSync(file, 'utf8');
		for (const from of fromLines(file)) {
			if (latest[from.image]) text = text.replace(from.ref, latest[from.image]);
		}
		fs.writeFileSync(file, text);
	}
}

const args = process.argv.slice(2);
if (args.includes('--resolve')) await resolve(args.includes('--write'));
else check();
