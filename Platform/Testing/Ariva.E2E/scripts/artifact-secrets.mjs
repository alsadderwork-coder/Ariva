#!/usr/bin/env node
// ARV-152 (CWE-532, ASVS V13.3.1): no credential in the E2E artifacts. Playwright's HTML report (its data is an embedded
// base64 zip), the test output (error contexts, attachments, trace zips) and the run's log may hold whatever a step title,
// an error, an attachment or a trace recorded. This scans plain files, every zip (a trace, or a zip file of the report) and
// every zip a file embeds as a data URL (the HTML report), also zips inside zips, and names the file and the kind of secret
// found, never the value. scripts/verify.mjs runs it after every Playwright run it starts; CI and the Azure DevOps pipeline
// run it before they upload or publish the report or the test output, and fail instead of uploading.
//
//   node scripts/artifact-secrets.mjs [--require-seed] [paths...]   scan the paths (default: playwright-report, test-results)
//   node scripts/artifact-secrets.mjs --self-test                    prove every kind is found in a plain file, a report's
//                                                                     embedded zip, a trace zip and an error context, and a
//                                                                     clean set passes; and that redactSecrets clears each one
//   node scripts/artifact-secrets.mjs --mask github|azure            print the log masking command for the seed and every value
//                                                                     derived from ARIVA_E2E_ACCOUNT_SEED (CI runs it right after
//                                                                     setting the seed)
//   node scripts/artifact-secrets.mjs --env github|azure             print the run's derived variables (DERIVED_VARIABLES) for
//                                                                     $GITHUB_ENV or as Azure DevOps variables
//
// Kinds by shape (scripts/secret-shapes.mjs): device and display credentials (ardk_, ardp_: 48 characters), integration
// client secrets (ics_: 47 characters), JWTs (three base64url segments, header and payload starting eyJ), bearer credentials
// and access_token values, TOTP seeds (an otpauth URI's secret, a totpSecret member), recovery codes and temporary
// passwords (as the API answers them, and on their own shapes, as a dialog or an error context shows them; any code in the
// sign-in or step-up recovery field, textbox "Recovery code" in English or Arabic), TOTP codes where
// a TOTP label precedes them (a "code" JSON member, a fill of a code field in a step title, a code field's value in an error
// context's ARIA snapshot; the server issues no other six-digit code).
// Kinds by exact value, each only when known to this process: the run's E2E account passwords and TOTP seeds (derived from
// ARIVA_E2E_ACCOUNT_SEED as tests/support/accounts.ts derives them); the values of SECRET_VARIABLES set in its environment
// (the derived simulator, mock AMAN, ACRIS and Cronz keys and validation reader password, which verify.mjs and CI derive
// from the seed and give to the run, playwright.config.ts keeping a value it is given; the account seed itself; the
// database passwords; the smoke suite's variables when set); and the integration client seeds and break-glass credential
// the run wrote to .e2e-keys. --require-seed fails when ARIVA_E2E_ACCOUNT_SEED is not set (or is a pipeline's unexpanded
// `$(NAME)`), so the passwords cannot go unchecked unseen.

import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { REDACTION_SAMPLES, SHAPES, redactSecrets, shapeMatches } from './secret-shapes.mjs';

const E2E = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ACCOUNTS = path.join(E2E, 'tests', 'support', 'accounts.ts');
const KEYS = path.join(E2E, '.e2e-keys');
export const DEFAULT_PATHS = ['playwright-report', 'test-results'].map((folder) => path.join(E2E, folder));

export { SHAPES };

/** The run's secret variables whose values never belong in an artifact (each only when set and 12 characters or more). */
export const SECRET_VARIABLES = [
	'ARIVA_E2E_SIMULATION_KEY', 'ARIVA_E2E_SIMULATION_READ_KEY', 'ARIVA_E2E_MOCK_AMAN_SECRET', 'ARIVA_E2E_MOCK_AMAN_SEED',
	'ARIVA_E2E_MOCK_AMAN_PULL_SECRET', 'ARIVA_E2E_MOCK_AMAN_PULL_SEED', 'ARIVA_E2E_ACRIS_KEY', 'ARIVA_E2E_CRONZ_KEY',
	'ARIVA_E2E_VALIDATION_READER_PASSWORD', 'ARIVA_E2E_ACCOUNT_SEED', 'Database__Password', 'Database__Migration__Password',
	'ARIVA_SMOKE_PASSWORD', 'ARIVA_SMOKE_TOTP_SECRET', 'ARIVA_SMOKE_DEVICE_KEY', 'ARIVA_SMOKE_DISPLAY_KEY'
];

const base32 = (bytes) => {
	const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
	let bits = 0;
	let value = 0;
	let out = '';
	for (const byte of bytes) {
		value = (value << 8) | byte;
		bits += 8;
		while (bits >= 5) {
			out += alphabet[(value >>> (bits - 5)) & 31];
			bits -= 5;
		}
	}
	return bits > 0 ? out + alphabet[(value << (5 - bits)) & 31] : out;
};
const sha256 = (text) => crypto.createHash('sha256').update(text).digest();

/**
 * The run's other secrets, which playwright.config.ts keeps when it is given them (`||=`) and otherwise draws itself inside the
 * Playwright process, where no other process sees them: derived here from the account seed in the config's own formats (a
 * prefix and 24 bytes in base64url, or 20 bytes in base32), so verify.mjs and CI can give them to the run, mask them in the
 * log and look for them in its artifacts. The hosts receive values of the same form as before.
 */
export const DERIVED_VARIABLES = {
	ARIVA_E2E_SIMULATION_KEY: 'sim-e2e-',
	ARIVA_E2E_SIMULATION_READ_KEY: 'sim-e2e-read-',
	ARIVA_E2E_MOCK_AMAN_SECRET: 'aman-e2e-',
	ARIVA_E2E_MOCK_AMAN_SEED: null,
	ARIVA_E2E_MOCK_AMAN_PULL_SECRET: 'aman-pull-',
	ARIVA_E2E_MOCK_AMAN_PULL_SEED: null,
	ARIVA_E2E_ACRIS_KEY: 'acris-e2e-',
	ARIVA_E2E_CRONZ_KEY: 'cronz-e2e-',
	ARIVA_E2E_VALIDATION_READER_PASSWORD: 'reader-e2e-'
};

/** The values of DERIVED_VARIABLES for a seed (a null prefix is a TOTP seed in base32). */
export function derivedVariables(seed) {
	return Object.fromEntries(
		Object.entries(DERIVED_VARIABLES).map(([name, prefix]) => {
			const bytes = sha256(`${seed}|${name}`);
			return [name, prefix === null ? base32(bytes.subarray(0, 20)) : prefix + bytes.subarray(0, 24).toString('base64url')];
		})
	);
}

/** The derivations tests/support/accounts.ts uses; the self-test fails when its source no longer holds them word for word. */
export const DERIVATIONS = [
	"crypto.createHash('sha256').update(`${seed()}|${userName}`).digest('base64url').slice(0, 24)",
	"crypto.createHash('sha256').update(`${seed()}|${userName}|totp`).digest().subarray(0, 20)"
];

/** The run's E2E account passwords and TOTP seeds, from the seed and the account names in accounts.ts. */
export function accountSecrets(seed, source = fs.readFileSync(ACCOUNTS, 'utf8')) {
	const names = [...new Set([...source.matchAll(/\baccount\(\s*'([A-Za-z0-9._-]+)'/g)].map((m) => m[1]))];
	// changedPassword and webFirstPassword: the same hash of the seed, a name and a word.
	const extras = [...source.matchAll(/update\(`\$\{seed\(\)\}\|([A-Za-z0-9._-]+\|[A-Za-z0-9._-]+)`\)\.digest\('base64url'\)\.slice\(0, 24\)/g)].map((m) => m[1]);
	const password = (text) => sha256(`${seed}|${text}`).toString('base64url').slice(0, 24);
	return [
		...names.map((name) => ({ kind: 'an E2E account password', value: password(name) })),
		...extras.map((text) => ({ kind: 'an E2E account password', value: password(text) })),
		...names.map((name) => ({ kind: 'an E2E account TOTP seed', value: base32(sha256(`${seed}|${name}|totp`).subarray(0, 20)) }))
	];
}

/** A variable's value, or undefined when it is empty or a pipeline macro left unexpanded (Azure DevOps keeps `$(NAME)` as text). */
export function valueOf(env, name) {
	const value = env[name];
	return value && !/^\$\([^)]*\)$/.test(value.trim()) ? value : undefined;
}

/** Every exact value to look for in this run: account secrets, secret variables and what the run wrote to .e2e-keys. */
export function runSecrets(env = process.env, keys = KEYS) {
	const seed = valueOf(env, 'ARIVA_E2E_ACCOUNT_SEED');
	const secrets = seed ? accountSecrets(seed) : [];
	for (const name of SECRET_VARIABLES) if ((valueOf(env, name) ?? '').length >= 12) secrets.push({ kind: `the value of ${name}`, value: env[name] });
	const seeds = path.join(keys, 'integration-seeds.txt');
	if (fs.existsSync(seeds))
		for (const line of fs.readFileSync(seeds, 'utf8').split(/\r?\n/).map((l) => l.trim()).filter((l) => l.length >= 16)) secrets.push({ kind: 'a TOTP seed the run enrolled', value: line });
	const breakGlass = path.join(keys, 'break-glass.txt');
	if (fs.existsSync(breakGlass))
		for (const line of fs.readFileSync(breakGlass, 'utf8').split(/\r?\n/)) {
			const value = line.startsWith('password: ') ? line.slice('password: '.length).trim() : line.startsWith('  ') ? line.trim() : '';
			if (value.length >= 8) secrets.push({ kind: 'the break-glass credential', value });
		}
	// A value is also looked for as a report writes it: URL-encoded and JSON-escaped.
	return secrets.flatMap(({ kind, value }) => [...new Set([value, encodeURIComponent(value), JSON.stringify(value).slice(1, -1)])].map((form) => ({ kind, value: form })));
}

/** The texts of a zip's entries (stored or deflated), read from its central directory; zips inside are read too. */
function zipEntries(buffer, depth) {
	const texts = [];
	let end = buffer.length - 22;
	while (end >= 0 && buffer.readUInt32LE(end) !== 0x06054b50) end--;
	if (end < 0) throw new Error('no end of central directory');
	let at = buffer.readUInt32LE(end + 16);
	for (let count = buffer.readUInt16LE(end + 10); count > 0; count--) {
		if (buffer.readUInt32LE(at) !== 0x02014b50) throw new Error('broken central directory');
		const method = buffer.readUInt16LE(at + 10);
		const size = buffer.readUInt32LE(at + 20);
		const local = buffer.readUInt32LE(at + 42);
		const start = local + 30 + buffer.readUInt16LE(local + 26) + buffer.readUInt16LE(local + 28);
		const data = buffer.subarray(start, start + size);
		if (method !== 0 && method !== 8) throw new Error(`compression method ${method}`);
		const entry = method === 8 ? zlib.inflateRawSync(data) : data;
		texts.push(...textsOf(entry, depth + 1));
		at += 46 + buffer.readUInt16LE(at + 28) + buffer.readUInt16LE(at + 30) + buffer.readUInt16LE(at + 32);
	}
	return texts;
}

const UNREADABLE = '\u0000unreadable zip\u0000';

/** The texts to search in one buffer: itself and, for a zip or a file embedding zips as data URLs, every entry. */
function textsOf(buffer, depth = 0) {
	const text = buffer.toString('latin1');
	const texts = [text];
	if (depth > 3) return texts;
	const zips = [...text.matchAll(/data:application\/zip;base64,([A-Za-z0-9+/=]+)/g)].map((m) => Buffer.from(m[1], 'base64'));
	if (buffer.length >= 4 && buffer.readUInt32LE(0) === 0x04034b50) zips.push(buffer);
	for (const zip of zips) {
		try {
			texts.push(...zipEntries(zip, depth));
		} catch {
			// What a zip that cannot be read holds cannot be shown to be clean.
			texts.push(UNREADABLE);
		}
	}
	return texts;
}

/**
 * The exact values in a form quick to look for: those made of URL-safe characters only (passwords, seeds, keys) are found
 * by length in the runs of such characters a text holds, one set lookup per position, instead of one search of the whole
 * text per value (host logs run to hundreds of megabytes); the others are searched for as they are.
 */
function indexOf(secrets) {
	const byLength = new Map();
	const other = [];
	for (const secret of secrets) {
		if (/^[A-Za-z0-9_-]+$/.test(secret.value)) {
			if (!byLength.has(secret.value.length)) byLength.set(secret.value.length, new Map());
			byLength.get(secret.value.length).set(secret.value, secret.kind);
		} else other.push(secret);
	}
	const lengths = [...byLength.keys()].sort((a, b) => a - b);
	return { byLength, lengths, other, shortest: lengths[0] ?? Infinity };
}

function exactKinds(text, index, kinds) {
	if (index.lengths.length > 0) {
		const runs = new RegExp(`[A-Za-z0-9_-]{${index.shortest},}`, 'g');
		for (const m of text.matchAll(runs)) {
			const run = m[0];
			for (const length of index.lengths) {
				if (length > run.length) break;
				const values = index.byLength.get(length);
				for (let i = 0; i + length <= run.length; i++) {
					const kind = values.get(run.substr(i, length));
					if (kind) kinds.add(kind);
				}
			}
		}
	}
	for (const { kind, value } of index.other) if (!kinds.has(kind) && text.includes(value)) kinds.add(kind);
}

/** The kinds of secret one file holds (never the values). */
export function kindsIn(buffer, secrets, index = indexOf(secrets)) {
	const texts = textsOf(buffer);
	const kinds = new Set();
	if (texts.includes(UNREADABLE)) kinds.add('a zip that cannot be read');
	for (const shape of SHAPES) if (!kinds.has(shape.kind) && texts.some((t) => shapeMatches(shape, t))) kinds.add(shape.kind);
	for (const text of texts) exactKinds(text, index, kinds);
	return [...kinds];
}

/** Findings under the paths (folders are walked, files read): [{ file, kinds }], and how many files were read. */
export function scanArtifacts(paths, secrets = runSecrets()) {
	const findings = [];
	const index = indexOf(secrets);
	let files = 0;
	const scan = (file) => {
		files++;
		const kinds = kindsIn(fs.readFileSync(file), secrets, index);
		if (kinds.length) findings.push({ file, kinds });
	};
	const walk = (target) => {
		if (!fs.existsSync(target)) return;
		const stat = fs.lstatSync(target);
		if (stat.isSymbolicLink()) return;
		if (stat.isFile()) return scan(target);
		for (const entry of fs.readdirSync(target, { withFileTypes: true })) {
			const file = path.join(target, entry.name);
			if (entry.isDirectory()) walk(file);
			else if (entry.isFile()) scan(file);
		}
	};
	for (const target of new Set(paths.map((p) => path.resolve(p)))) walk(target);
	return { findings, files };
}

/** Scans and prints: one line per file and kind, never a value. True when clean. */
export function checkArtifacts(paths, { requireSeed = false, env = process.env, log = console.log } = {}) {
	if (!valueOf(env, 'ARIVA_E2E_ACCOUNT_SEED')) {
		if (requireSeed) {
			log('ARIVA_E2E_ACCOUNT_SEED is not set: the run\'s E2E account passwords cannot be checked (set it before Playwright starts)');
			return false;
		}
		log('note: ARIVA_E2E_ACCOUNT_SEED is not set, so the E2E account passwords are not checked (the shapes and the other values are)');
	}
	const { findings, files } = scanArtifacts(paths, runSecrets(env));
	for (const { file, kinds } of findings) for (const kind of kinds) log(`SECRET ${kind} in ${path.relative(process.cwd(), file) || file}`);
	if (findings.length) log(`${findings.length} artifact files hold a secret (CWE-532): delete them before sharing anything from these folders`);
	else log(`artifacts: no secret in ${files} files under ${paths.map((p) => path.relative(process.cwd(), p) || p).join(', ')}`);
	return findings.length === 0;
}

/** The commands that mask the seed and every value derived from it in a CI log (GitHub Actions or Azure DevOps). */
export function maskCommands(system, env = process.env) {
	const seed = valueOf(env, 'ARIVA_E2E_ACCOUNT_SEED');
	if (!seed) return [];
	const values = [seed, ...new Set([...accountSecrets(seed).map((s) => s.value), ...Object.values(derivedVariables(seed))])];
	return values.map((value) => (system === 'azure' ? `##vso[task.setsecret]${value}` : `::add-mask::${value}`));
}

// A zip as Playwright writes them (local headers, deflated entries, a central directory), for the self-test's fixtures.
function zip(entries) {
	const locals = [];
	const centrals = [];
	let offset = 0;
	for (const [name, content] of Object.entries(entries)) {
		const data = Buffer.from(content);
		const packed = zlib.deflateRawSync(data);
		const nameBytes = Buffer.from(name);
		const crc = zlib.crc32(data);
		const local = Buffer.alloc(30);
		local.writeUInt32LE(0x04034b50, 0);
		local.writeUInt16LE(20, 4);
		local.writeUInt16LE(8, 8);
		local.writeUInt32LE(crc, 14);
		local.writeUInt32LE(packed.length, 18);
		local.writeUInt32LE(data.length, 22);
		local.writeUInt16LE(nameBytes.length, 26);
		const central = Buffer.alloc(46);
		central.writeUInt32LE(0x02014b50, 0);
		central.writeUInt16LE(20, 4);
		central.writeUInt16LE(20, 6);
		central.writeUInt16LE(8, 10);
		central.writeUInt32LE(crc, 16);
		central.writeUInt32LE(packed.length, 20);
		central.writeUInt32LE(data.length, 24);
		central.writeUInt16LE(nameBytes.length, 28);
		central.writeUInt32LE(offset, 42);
		locals.push(local, nameBytes, packed);
		centrals.push(central, nameBytes);
		offset += 30 + nameBytes.length + packed.length;
	}
	const directory = Buffer.concat(centrals);
	const end = Buffer.alloc(22);
	end.writeUInt32LE(0x06054b50, 0);
	end.writeUInt16LE(Object.keys(entries).length, 8);
	end.writeUInt16LE(Object.keys(entries).length, 10);
	end.writeUInt32LE(directory.length, 12);
	end.writeUInt32LE(offset, 16);
	return Buffer.concat([...locals, directory, end]);
}

function selfTest() {
	const problems = [];
	const random = (n) => crypto.randomBytes(n).toString('base64url');
	const seed = random(24);
	const env = { ARIVA_E2E_ACCOUNT_SEED: seed, Database__Password: `runtime-${random(12)}` };
	const source = fs.readFileSync(ACCOUNTS, 'utf8');
	const configSource = fs.readFileSync(path.join(E2E, 'playwright.config.ts'), 'utf8');
	for (const derivation of DERIVATIONS) if (!source.includes(derivation)) problems.push(`accounts.ts no longer derives with ${derivation}: update accountSecrets`);
	const accounts = accountSecrets(seed, source);
	const names = [...source.matchAll(/\baccount\(\s*'([A-Za-z0-9._-]+)'/g)].length;
	if (names < 20 || !source.includes("account('e2e.border'")) problems.push(`only ${names} accounts read from accounts.ts`);
	const password = accounts.find((a) => a.kind === 'an E2E account password').value;
	const totpSeed = accounts.find((a) => a.kind === 'an E2E account TOTP seed').value;
	const jwt = `eyJ${random(20)}.eyJ${random(60)}.${random(64)}`;
	// Each kind as it would reach an artifact: a step title, an answer in an error, a trace's recorded request or response, an
	// error context's page snapshot (Playwright writes input values there, password fields included).
	const samples = [
		['a device or display credential (ardk_, ardp_)', `{"title":"device registered","credential":"ardk_${random(32)}"}`],
		['an integration client secret (ics_)', `answer: {"clientSecret":"ics_${random(32)}"}`],
		['a JWT', `{"name":"authorization","value":"${jwt}"}`],
		['a bearer credential', `{"name":"Authorization","value":"Bearer sim-e2e-${random(24)}"}`],
		['an access_token value', `{"subtitle":"localhost:51001/hubs/live?access_token=${random(24)}"}`],
		['a TOTP seed (otpauth URI)', `"totpUri":"otpauth://totp/Ariva:e2e?issuer=Ariva&secret=${base32(crypto.randomBytes(20))}"`],
		['a TOTP seed (totpSecret member)', `{\\"totpSecret\\":\\"${base32(crypto.randomBytes(20))}\\"}`],
		['recovery codes', '{"recoveryCodes":["7K2QH-M9XAB","4RTPZ-0WQ5C"]}'],
		['a recovery code', '- list "Recovery codes":\n  - listitem: 7K2QH-M9XAB\n  - listitem: 4RTPZ-0WQ5C'],
		['a temporary password', '{"temporaryPassword":"abcde-FGHJK-mnpqr-23456"}'],
		['a temporary password', '- textbox "Temporary password" [ref=e7]: hjkmn-PQRST-uvwxy-23456'],
		['a TOTP code (a "code" member)', '{"userName":"e2e.x","code":"493817"}'],
		['a TOTP code in a code field', '- textbox "Authenticator code" [ref=e9]: 493817'],
		['a TOTP code in a code field', `- textbox "${'\u0631\u0645\u0632 \u062a\u0637\u0628\u064a\u0642 \u0627\u0644\u0645\u0635\u0627\u062f\u0642\u0629'}" [ref=e9]: 493817`],
		['a recovery code in a code field', '- textbox "Recovery code" [ref=e9]: WXYZK-MNPQR'],
		['a recovery code in a code field', `- textbox "${'\u0631\u0645\u0632 \u0627\u0644\u0627\u0633\u062a\u0631\u062f\u0627\u062f'}" [ref=e9]: 7k2qhm9xab`],
		['a TOTP code typed into a code field', `{"title":"Fill \\"493817\\"","subtitle":"locator('#step-up-code')"}`],
		['an E2E account password', `{"title":"Fill \\"${password}\\" getByLabel('Password', { exact: true })"}`],
		['an E2E account password', `- textbox "Password" [ref=e12]: ${password}`],
		['an E2E account password', `- textbox "Repeat the new password" [ref=e14]: ${password}x`],
		['an E2E account TOTP seed', `seed ${totpSeed}`],
		['the value of Database__Password', `connection failed for ${env.Database__Password}`]
	];
	const clean = [
		'{"title":"Expect \\"toBe\\"","steps":[{"title":"POST","subtitle":"localhost:51001/api/v1/admin/devices?siteCode=DMO"}]}',
		'"credential":"ardk_[redacted]" and ics_[redacted] and a prefix ardk_ABCDEFGH',
		'Bearer [redacted JWT], eyJhbGciOiJFUzI1NiJ9 alone, "code":"S-17", "code":"AR-05", 06:32:43.4938178',
		`{"title":"Evaluate"},{"title":"Fill \\"e2e.border\\" getByLabel('Username')"}`,
		'/ardk_[A-Za-z0-9_-]{43}/ and /^ics_/ in a code frame, otpauth://totp/Ariva:x?issuer=Ariva without a seed',
		'Bearer authentication, Authorization: `Bearer ${token}` in a code frame, access_token=[redacted]',
		'- textbox "Username" [ref=e3]: e2e.border\n- heading "where-these-three-teens" [level=1]\n- cell "ABCDE-FGHJK"',
		'trace 3f2b8c1e-9d4a-4b7e-8f00-1a2b3c4d5e6f, desk AR-05, display E2E-CANARY, 2026-10-20',
		'- textbox "IATA code" [ref=e3]: DMO\n- textbox "Authenticator code" [ref=e9]\n- textbox "Code" [ref=e4]: S-17',
		'- textbox "Recovery code" [ref=e9]\n- button "Use a recovery code instead"',
		'"snippet":"call(\'GET\', `${host.url}/hubs/live?access_token=${canary.jwt}`)" (a step\'s code snippet: source, not a value)'
	];
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-artifact-secrets-'));
	try {
		const secrets = runSecrets(env, path.join(dir, 'no-keys'));
		const report = (inner) => `<html><script>window.playwrightReportBase64 = "data:application/zip;base64,${zip({ 'report.json': '{}', 'abc.json': inner }).toString('base64')}";</script></html>`;
		for (const [kind, sample] of samples) {
			const forms = {
				'a plain file': Buffer.from(sample),
				"the HTML report's embedded zip": Buffer.from(report(sample)),
				'a trace zip': zip({ 'trace.trace': '{"type":"before"}', 'resources/abc.json': sample }),
				'a zip inside a zip': zip({ 'inner.zip': zip({ 'trace.network': sample }) }),
				'an error context': Buffer.from(`# Page snapshot\n\n\`\`\`yaml\n- main [ref=e2]:\n  ${sample}\n\`\`\`\n`)
			};
			for (const [form, buffer] of Object.entries(forms)) {
				const file = path.join(dir, 'fixture');
				fs.writeFileSync(file, buffer);
				const { findings } = scanArtifacts([file], secrets);
				if (!findings.some((f) => f.kinds.includes(kind))) problems.push(`${kind} not found in ${form}`);
			}
		}
		// The clean set: the same forms with no secret in them pass.
		const cleanDir = path.join(dir, 'clean');
		fs.mkdirSync(path.join(cleanDir, 'data'), { recursive: true });
		fs.writeFileSync(path.join(cleanDir, 'index.html'), report(clean.join('\n')));
		fs.writeFileSync(path.join(cleanDir, 'data', 'trace.zip'), zip({ 'trace.trace': clean.join('\n'), 'resources/x.json': clean[0] }));
		clean.forEach((text, i) => fs.writeFileSync(path.join(cleanDir, `error-context-${i}.md`), text));
		const quiet = scanArtifacts([cleanDir], secrets);
		for (const f of quiet.findings) problems.push(`clean ${path.basename(f.file)} flagged: ${f.kinds.join(', ')}`);
		if (quiet.files !== clean.length + 2) problems.push(`the clean set read ${quiet.files} files`);
		// A zip that cannot be read is a finding: its content cannot be shown to be clean.
		const broken = path.join(dir, 'broken.zip');
		fs.writeFileSync(broken, Buffer.concat([zip({ 'a.txt': 'x' }).subarray(0, 40), Buffer.alloc(30)]));
		if (!scanArtifacts([broken], secrets).findings.some((f) => f.kinds.includes('a zip that cannot be read'))) problems.push('an unreadable zip is not a finding');
		// --require-seed refuses a run without the seed; printing never shows a value.
		const printed = [];
		if (checkArtifacts([cleanDir], { requireSeed: true, env: {}, log: (l) => printed.push(l) })) problems.push('--require-seed passed without a seed');
		fs.writeFileSync(path.join(cleanDir, 'leak.txt'), samples.find(([kind]) => kind === 'an E2E account password')[1]);
		if (checkArtifacts([cleanDir], { env, log: (l) => printed.push(l) })) problems.push('a leak passed checkArtifacts');
		if (printed.some((l) => l.includes(password))) problems.push('a found value was printed');
		// The masking commands CI prints right after the seed: every derived value, the seed first.
		const masks = maskCommands('github', env);
		if (masks[0] !== `::add-mask::${seed}` || !masks.includes(`::add-mask::${password}`) || !masks.includes(`::add-mask::${totpSeed}`)) problems.push('a derived value is not masked (github)');
		if (!maskCommands('azure', env).includes(`##vso[task.setsecret]${password}`)) problems.push('a derived value is not masked (azure)');
		if (maskCommands('github', { ARIVA_E2E_ACCOUNT_SEED: '$(E2E_ACCOUNT_SEED)' }).length !== 0) problems.push('an unexpanded pipeline macro counts as a seed');
		// The derived variables: stable for a seed, in the config's formats, masked, and found in an artifact once given to a run.
		const derived = derivedVariables(seed);
		if (JSON.stringify(derived) !== JSON.stringify(derivedVariables(seed)) || JSON.stringify(derived) === JSON.stringify(derivedVariables(`${seed}x`))) problems.push('the derived variables are not stable per seed');
		for (const [name, prefix] of Object.entries(DERIVED_VARIABLES)) {
			const value = derived[name];
			if (prefix === null ? !/^[A-Z2-7]{32}$/.test(value) : !(value.startsWith(prefix) && /^[A-Za-z0-9_-]{32}$/.test(value.slice(prefix.length)))) problems.push(`${name} is not in the config's format`);
			if (!masks.includes(`::add-mask::${value}`)) problems.push(`${name} is not masked`);
			if (!configSource.includes(`process.env.${name} ||=`)) problems.push(`playwright.config.ts no longer keeps a given ${name}`);
		}
		const runEnv = { ...env, ...derived };
		fs.writeFileSync(path.join(cleanDir, 'leak.txt'), `the operator key was ${derived.ARIVA_E2E_SIMULATION_KEY}`);
		if (!scanArtifacts([cleanDir], runSecrets(runEnv, path.join(dir, 'no-keys'))).findings.some((f) => f.kinds.includes('the value of ARIVA_E2E_SIMULATION_KEY'))) problems.push('a derived variable given to a run is not found');
		if (checkArtifacts([cleanDir], { requireSeed: true, env: { ARIVA_E2E_ACCOUNT_SEED: '$(E2E_ACCOUNT_SEED)' }, log: () => {} })) problems.push('--require-seed passed on an unexpanded macro');
		// redactSecrets (tests/support/expect-status.ts quotes answers through it) clears every sample and keeps plain text.
		for (const sample of REDACTION_SAMPLES.cleared) {
			const secret = sample.split('|')[1].slice(0, 11);
			if (redactSecrets(sample.replace('|', '')).includes(secret)) problems.push(`redactSecrets leaves ${sample.split('|')[0]}...`);
		}
		for (const text of REDACTION_SAMPLES.kept) if (redactSecrets(text) !== text) problems.push(`redactSecrets changes ${text}`);
	} finally {
		fs.rmSync(dir, { recursive: true, force: true });
	}
	if (problems.length) {
		console.error(`artifact secret check self-test FAILED:\n${problems.join('\n')}`);
		return false;
	}
	console.log(`artifact secret check self-test: ${samples.length} samples of ${new Set(samples.map(([kind]) => kind)).size} kinds found in a plain file, a report's embedded zip, a trace zip, a nested zip and an error context; the clean set passes; an unreadable zip fails; no value printed; every derived value masked; redactSecrets clears ${REDACTION_SAMPLES.cleared.length} samples and keeps ${REDACTION_SAMPLES.kept.length}.`);
	return true;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
	const args = process.argv.slice(2);
	if (args.includes('--self-test')) process.exit(selfTest() ? 0 : 1);
	// The derived variables for the run's environment: `NAME=value` lines for GitHub's $GITHUB_ENV, or Azure DevOps commands
	// (plain variables, mapped into later steps' environment; --mask azure registers each value as a secret first).
	const environment = args.indexOf('--env');
	if (environment >= 0) {
		const system = args[environment + 1];
		const seed = valueOf(process.env, 'ARIVA_E2E_ACCOUNT_SEED');
		if (!['github', 'azure'].includes(system) || !seed) (console.error('--env github|azure needs ARIVA_E2E_ACCOUNT_SEED'), process.exit(2));
		const lines = Object.entries(derivedVariables(seed)).map(([name, value]) => (system === 'azure' ? `##vso[task.setvariable variable=${name}]${value}` : `${name}=${value}`));
		process.stdout.write(`${lines.join('\n')}\n`);
		process.exit(0);
	}
	const mask = args.indexOf('--mask');
	if (mask >= 0) {
		const system = args[mask + 1];
		if (!['github', 'azure'].includes(system)) (console.error('--mask github|azure'), process.exit(2));
		const commands = maskCommands(system);
		if (commands.length === 0) (console.error('--mask: ARIVA_E2E_ACCOUNT_SEED is not set'), process.exit(1));
		process.stdout.write(`${commands.join('\n')}\n`);
		process.exit(0);
	}
	const paths = args.filter((a) => !a.startsWith('--'));
	process.exit(checkArtifacts(paths.length ? paths.map((p) => path.resolve(p)) : DEFAULT_PATHS, { requireSeed: args.includes('--require-seed') }) ? 0 : 1);
}
