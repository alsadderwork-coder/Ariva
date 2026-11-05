#!/usr/bin/env node
// ARV-152 (CWE-532, ASVS V13.3.1): no credential in the E2E artifacts. Playwright's HTML report (its data is an embedded
// base64 zip), the test output (error contexts, attachments, trace zips) and the run's log may hold whatever a step title,
// an error, an attachment or a trace recorded. This scans plain files, every zip (a trace, or a zip file of the report) and
// every zip a file embeds as a data URL (the HTML report), also zips inside zips, and names the file and the kind of secret
// found, never the value. scripts/verify.mjs runs it after every Playwright run it starts; CI and the Azure DevOps pipeline
// run it before they upload or publish the report or the test output, and fail instead of uploading. ARV-252: CI's backend
// job runs it on the unit and integration suites' output (unit.log and integration.log, which carry the in-process hosts'
// console logs) and their TRX files before any of it is printed, quoted or uploaded; backendLogWiring holds that wiring.
// ARV-271: the mutation workflow checks Stryker's log and report the same way before it uploads them (mutationLogWiring), and
// the E2E job's failure comment quotes no backtick from a log line (e2eCommentQuotes).
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
const CI_WORKFLOW = path.resolve(E2E, '..', '..', '..', '.github', 'workflows', 'ci.yml');
const MUTATION_WORKFLOW = path.resolve(E2E, '..', '..', '..', '.github', 'workflows', 'mutation.yml');
const WORKFLOWS = path.resolve(E2E, '..', '..', '..', '.github', 'workflows');
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

// ARV-271: a check step that goes on after an error: set +e, set +ex and the like, set +o errexit, or a shell of its own (a step's
// `shell: bash {0}` drops the default's -e).
const ERREXIT_OFF = /\bset\s+(?:\+[A-Za-z]*e[A-Za-z]*\b|\+o\s+errexit\b)|^\s*shell:/m;

/**
 * ARV-271a: a shell script with what its quotes hold blanked out (single, double and $'...' quotes, and escaped characters), so a
 * rule reads only what the shell runs: a # or a > inside a quote is text, not a comment or a redirection.
 */
function blankQuotes(script) {
	let out = '';
	let quote = null;
	for (let i = 0; i < script.length; i++) {
		const c = script[i];
		if (quote === "'") {
			out += c === "'" ? c : c === '\n' ? c : ' ';
			if (c === "'") quote = null;
			continue;
		}
		if (quote === "$'" || quote === '"') {
			if (c === '\\' && i + 1 < script.length) {
				out += script[i + 1] === '\n' ? ' \n' : '  ';
				i++;
				continue;
			}
			const end = quote === '"' ? '"' : "'";
			out += c === end ? c : c === '\n' ? c : ' ';
			if (c === end) quote = null;
			continue;
		}
		if (c === '\\' && i + 1 < script.length) {
			// A line continued by a backslash keeps its line end, so the blanked text keeps the script's lines.
			out += script[i + 1] === '\n' ? ' \n' : '  ';
			i++;
			continue;
		}
		if (c === '$' && script[i + 1] === "'") {
			quote = "$'";
			out += "$'";
			i++;
			continue;
		}
		if (c === "'" || c === '"') quote = c;
		out += c;
	}
	return out;
}

// ARV-271a: a check step's script that can leave its check line unrun (read with its quotes blanked): an exit, exec or return; a
// here-document (whose text is never run); a function defined (whose body runs only if called); an alias, hash, enable, shopt or
// PATH that can turn node into something else; a trap or kill that ends the script first; or a job left in the background (nohup, setsid, disown, coproc or a lone &), which
// goes on writing after the check.
const CHECK_SKIPPED = /\b(?:exit|exec|return|trap|kill|alias|hash|enable|shopt|nohup|setsid|disown|coproc)\b|<<|\bPATH=|^\s*(?:function\s+[\w-]+|[\w-]+\s*\(\s*\))/m;

/** True when a script (its quotes blanked) leaves a job in the background: a & that is not && nor part of a redirection. */
const backgrounds = (blank) => /&/.test(blank.replace(/&&|[0-9]?>&[0-9-]?|&>>?/g, ''));

// ARV-271a: a command that writes into checked/, read with its quotes blanked: a redirection into a path ending in it (>, >>, >|,
// &>), a tee, copy, move, link, unpack, in-place edit, truncation or mode change naming it, or a cd or pushd into it.
const WRITES_CHECKED = /(?:>{1,2}|>\||&>>?)\s*\S*checked\b|\b(?:tee|cp|mv|ln|rsync|install|tar|unzip|touch|mkdir|dd|truncate|chmod|cd|pushd)\b[^|;&\n]*checked\b|\b(?:sed|perl)\s+-[A-Za-z]*i[^|;&\n]*checked\b/;

/** A step's name, or its place when it has none. */
const nameOf = (step, i) => /^(?:name: |\s*name: )(.*)$/m.exec(step)?.[1] ?? `step ${i + 1}`;

/** The lines of a step's run script (a block's lines, or the one line of a plain run). */
function scriptOf(step) {
	// Spaces only: a \s would take the blank lines a removed comment leaves into the run key's indentation.
	const block = /^([ \t]*)run: [|>][-+]?[ \t]*\n/m.exec(step);
	if (block) {
		const lines = [];
		for (const line of step.slice(block.index + block[0].length).split('\n')) {
			if (line.trim() === '') continue;
			if (line.search(/\S/) <= block[1].length) break;
			lines.push(line);
		}
		return lines;
	}
	const plain = /^[ \t]*run: (.+)$/m.exec(step);
	return plain ? [plain[1]] : [];
}

/**
 * ARV-271a: what is wrong with a check step's check line: it must be the script's last line, at the script's own indentation (in
 * no loop, test or group), in a script without a function, an exit, exec or return, or a here-document, so the check always runs.
 */
function checkLineProblems(step, what) {
	const lines = scriptOf(step);
	const last = lines.at(-1) ?? '';
	const base = lines[0]?.search(/\S/) ?? -1;
	const problems = [];
	if (!/^\s*node \S*artifact-secrets\.mjs checked\s*$/.test(last) || last.search(/\S/) !== base) problems.push(`the ${what} does not end with its check of checked/, at the script's own level`);
	const blank = blankQuotes(lines.join('\n'));
	if (CHECK_SKIPPED.test(blank) || backgrounds(blank))
		problems.push(`the ${what} can leave its check unrun (an exit, exec or return, a here-document, a function, an alias or the like, or a job in the background)`);
	return problems;
}

/** An upload or read path that leaves the folder it starts with (a .. segment). */
const climbs = (p) => /(?:^|\/)\.\.(?:\/|$)/.test(p);

/**
 * ARV-252: the backend job's wiring in a CI workflow's text (.github/workflows/ci.yml), read step by step with comments left
 * out (whole lines and a comment after a value), so a step is read by what it runs:
 * - every step that runs a test suite (`dotnet ...Tests.dll`; `dotnet test` is refused) runs the unit or the integration
 *   executable, writes its output to unit.log or integration.log alone (never teed to the public job log), has a timeout of
 *   its own (so a hung suite still leaves the check its time) and comes before the check;
 * - the test log check (id test-logs, one step) runs after a failed suite too, cannot be told to continue on an error,
 *   copies unit.log, integration.log and TestResults to checked/ and checks that copy, its check the script's last line at
 *   its own level, so nothing a suite writes later reaches the steps after it (ARV-271a);
 * - before it no other step reads a log (by name, through a variable or a glob) or the test results (the count guards' --trx
 *   aside) or uploads anything; after it every log or result a step names is the checked copy (restore.log and build.log,
 *   the compiler's, aside), no step writes into checked/, every upload's path is under checked/ without a .. segment, a step
 *   that names the copy waits for the check's success (in its if, exactly `!cancelled() && steps.test-logs.outcome ==
 *   'success'`, or by LOGS_CHECKED taken from the check's outcome, every line naming a checked log inside its `if [
 *   "$LOGS_CHECKED" = success ]` block), and a step that prints with cat stops workflow commands around it with a random
 *   token and resumes on a line of its own.
 * Returns what is wrong, empty when the wiring holds.
 */
export function backendLogWiring(workflow) {
	const steps = jobSteps(workflow, 'backend');
	if (!steps) return ['no backend job'];
	const problems = [];
	const check = steps.findIndex((s) => /^\s*id: test-logs\s*$/m.test(s));
	if (check < 0 || steps.filter((s) => /^\s*id: test-logs\s*$/m.test(s)).length > 1) return ['not one test log check (id: test-logs)'];
	const logs = { 'Ariva.UnitTests.dll': 'unit.log', 'Ariva.IntegrationTests.dll': 'integration.log' };
	const ran = new Set();
	steps.forEach((step, i) => {
		const name = nameOf(step, i);
		if (/\bdotnet\s+test\b/.test(step)) problems.push(`"${name}" runs a suite through dotnet test, whose output the check does not read`);
		for (const [, dll] of step.matchAll(/\bdotnet\s+\S*?([\w.]+Tests\.dll)\b/g)) {
			const log = logs[dll];
			if (!log) {
				problems.push(`"${name}" runs ${dll}, whose output the check does not read`);
				continue;
			}
			ran.add(dll);
			if (!new RegExp(`> ${log.replace('.', '\\.')} 2>&1\\s*$`, 'm').test(step) || /\btee\b/.test(step)) problems.push(`"${name}" does not write ${dll}'s output to ${log} alone (the job log is public)`);
			if (!/^\s*timeout-minutes: \d+\s*$/m.test(step)) problems.push(`"${name}" has no timeout of its own`);
			if (i > check) problems.push(`"${name}" runs ${dll} after the test log check`);
		}
	});
	for (const dll of Object.keys(logs)) if (!ran.has(dll)) problems.push(`no step runs ${dll}`);
	const checkStep = steps[check];
	if (!gatedBy(checkStep, '${{ !cancelled() }}')) problems.push('the test log check does not run after a failed suite');
	if (/^\s*continue-on-error:/m.test(checkStep)) problems.push('the test log check may continue on an error');
	if (ERREXIT_OFF.test(checkStep)) problems.push('the test log check turns off stopping on an error (set +e or the like, or a shell of its own)');
	if (!/^\s*for item in unit\.log integration\.log TestResults\b[^\n]*; do\s*$/m.test(checkStep) || !/cp -RL "\$item" checked\//.test(checkStep)) problems.push('the test log check does not copy unit.log, integration.log and TestResults to checked/ with links followed (cp -RL)');
	for (const problem of checkLineProblems(checkStep, 'test log check')) problems.push(problem);
	const allowed = new Set(['checked/unit.log', 'checked/integration.log', 'restore.log', 'build.log']);
	steps.forEach((step, i) => {
		if (i === check || /\bdotnet\s+\S*Tests\.dll\b/.test(step)) return;
		const name = nameOf(step, i);
		const named = [...step.matchAll(/(?<![\w%])[\w./*$-]*[\w*]\.log\b/g)].map((m) => m[0]);
		// The count guards read the TRX files by --trx before the check (test-count-guard.mjs prints case names only).
		const guard = /\bnode scripts\/test-count-guard\.mjs\b/.test(step);
		const results = [...step.replace(guard ? /--trx TestResults\/[\w.-]+\.trx\b/g : /$^/, '').matchAll(/[\w./*${}-]*TestResults[\w./*-]*/g)].map((m) => m[0]);
		const uploads = /^\s*uses: actions\/upload-artifact@/m.test(step);
		if (i < check) {
			if (named.some((log) => /^(?:unit|integration)\.log$|[*$]/.test(log))) problems.push(`"${name}" reads a test log before the test log check`);
			if (results.length) problems.push(`"${name}" reads the test results before the test log check`);
			if (uploads) problems.push(`"${name}" uploads before the test log check`);
			return;
		}
		for (const log of named) if (!allowed.has(log)) problems.push(`"${name}" reads ${log}, not the checked copy`);
		for (const result of results) if (!result.startsWith('checked/TestResults') || climbs(result)) problems.push(`"${name}" reads ${result}, not the checked copy`);
		if (uploads) for (const [, path] of step.matchAll(/^\s*path: (.*)$/gm)) if (!path.trim().startsWith('checked/') || climbs(path.trim())) problems.push(`"${name}" uploads ${path.trim()}, not the checked copy`);
		if (WRITES_CHECKED.test(scriptOf(step).join('\n').replace(/["']/g, ''))) problems.push(`"${name}" writes into checked/ after the check`);
		if (nestedIf(step)) problems.push(`"${name}" has an if: under with: or env:, which is not its gate`);
		if (!step.includes('checked/')) return;
		// ARV-271a: the gate exactly as the workflow writes it (a negated or or-ed one does not wait for the check).
		const inIf = gatedBy(step, "${{ !cancelled() && steps.test-logs.outcome == 'success' }}");
		const inScript = /^ {10}LOGS_CHECKED: \$\{\{ steps\.test-logs\.outcome \}\}\s*$/m.test(step) && /\[ "\$LOGS_CHECKED" = success \]/.test(step);
		if (!inIf && !inScript) problems.push(`"${name}" reads the checked copy without waiting for the check's success`);
		if (!inIf && inScript) {
			const blank = blankQuotes(scriptOf(step).join('\n'));
			if (outsideGate(step).length) problems.push(`"${name}" reads the checked copy outside its LOGS_CHECKED block`);
			if (/\bLOGS_CHECKED\s*\+?=|\b(?:export|read|declare|local|typeset|readonly|unset)\b[^\n;|&]*\bLOGS_CHECKED\b|\bprintf\s+-v\s*LOGS_CHECKED\b/.test(blank))
				problems.push(`"${name}" sets LOGS_CHECKED itself`);
			if (/<</.test(blank)) problems.push(`"${name}" has a here-document, whose text the LOGS_CHECKED block cannot be read in`);
		}
		if (/\bcat\b/.test(step) && !(/token=\$\(openssl rand -hex (?:1[6-9]|[2-9]\d)\)/.test(step) && step.includes('echo "::stop-commands::$token"') && step.includes(`printf '\\n::%s::\\n' "$token"`)))
			problems.push(`"${name}" prints with workflow commands live (a random token, stopped before and resumed on a line of its own after)`);
	});
	return problems;
}

/**
 * ARV-271a: the pipelines of a step's script that name checked/ outside an `if [ "$LOGS_CHECKED" = success ]; then` block, its
 * nesting followed by the if and fi keywords as the shell reads them (not by indentation); the block ends at its own fi, else or
 * elif. Only the test results' counts (checked/TestResults/*.trx) are read outside it.
 */
function outsideGate(step) {
	const outside = [];
	let depth = 0;
	let gate = -1;
	for (const pipeline of pipelinesOf(scriptOf(step).join('\n'))) {
		const first = pipeline.split(/\s+/)[0];
		if (first === 'fi') {
			if (depth === gate) gate = -1;
			depth = Math.max(0, depth - 1);
			continue;
		}
		if ((first === 'else' || first === 'elif') && depth === gate) gate = -1;
		if (first === 'if' || first === 'elif') {
			if (first === 'if') depth++;
			if (gate < 0 && first === 'if' && /^if \[ "\$LOGS_CHECKED" = success \]$/.test(pipeline)) {
				gate = depth;
				continue;
			}
		}
		if (gate < 0 && /checked/.test(blankQuotes(pipeline).replace(/checked\/TestResults\/\*\.trx/g, ''))) outside.push(pipeline);
	}
	return outside;
}

/** The steps of one job of a workflow, comments taken out, as backendLogWiring reads them. */
function jobSteps(workflow, job) {
	const start = workflow.search(new RegExp(`^ {2}${job}:\\s*$`, 'm'));
	if (start < 0) return null;
	const rest = workflow.slice(start + 1);
	const end = rest.search(/^ {2}[A-Za-z][\w-]*:\s*$/m);
	return stripComments(end < 0 ? rest : rest.slice(0, end))
		.split(/^ {6}- /m)
		.slice(1);
}

/**
 * ARV-271a: a job's text with its comments taken out as YAML and the shell read them: outside a block scalar a whole-line comment
 * and a comment after a value; inside a run block (a key's | or > text) a whole-line comment and a # that starts a word outside
 * quotes, so a # inside a quoted string (echo "step #1") stays and what follows it is still read.
 */
function stripComments(text) {
	const out = [];
	let block = -1;
	let pending = [];
	// A run block's lines are read together, so a quote opened on one line is still open on the next (a # there is text).
	const flush = () => {
		if (pending.length === 0) return;
		const blank = blankQuotes(pending.join('\n')).split('\n');
		pending.forEach((line, i) => {
			const hash = blank[i].search(/(?:^|\s)#/);
			out.push(hash < 0 ? line : line.slice(0, hash).trimEnd());
		});
		pending = [];
	};
	for (const line of text.split('\n')) {
		const level = line.search(/\S/);
		if (block >= 0 && level >= 0 && level <= block) {
			flush();
			block = -1;
		}
		if (block >= 0) {
			pending.push(line);
			continue;
		}
		const opens = /^(\s*)(- )?[\w-]+: [|>][-+]?\s*(?:#.*)?$/.exec(line);
		out.push(line.replace(/^\s*#.*$/, '').replace(/[ \t]+#.*$/, ''));
		if (opens) block = opens[1].length + (opens[2] ? 2 : 0);
	}
	flush();
	return out.join('\n');
}

/** A step's own if (at its keys' level, or on its first line), as many as it has; an if under with: or env: is not one. */
const stepIfs = (step) => step.split('\n').filter((line, i) => (i === 0 ? /^if: / : /^ {8}if: /).test(line)).map((line) => line.trim().slice(4));

/** True when a step has an if: under one of its keys (with:, env:), which Actions takes for an input or a variable, not a gate. */
const nestedIf = (step) => step.split('\n').some((line) => /^ {9,}if:/.test(line));

/** True when a step's gate is exactly the one given (one step-level if, none nested). */
const gatedBy = (step, gate) => stepIfs(step).length === 1 && stepIfs(step)[0] === gate && !nestedIf(step);

/**
 * ARV-271 (CWE-532): the mutation workflow (.github/workflows/mutation.yml, public artifacts) checks Stryker's log and report before
 * it uploads them, as backendLogWiring holds CI's backend job: one check (id mutation-logs) that runs after a failed run, cannot be
 * told to go on after an error, copies .verify/mutation.log and .verify/stryker/reports to checked/ with links followed and checks
 * that copy alone, its check the script's last line at its own level (ARV-271a); no upload before it; every upload after it waits
 * for its success (an if no || can open) and takes its paths from checked/ only, without a .. segment. The run is the one command
 * of its step (node mutation-run.mjs --log, nothing before or after it on the line, so no inline variable overrides its quiet
 * setting and no read rides on it), and no other step names .verify (a glob read included) or checked/, so nothing reads the log
 * or the report in place and nothing writes into the copy after the check. Returns what is wrong, empty when the wiring holds.
 */
export function mutationLogWiring(workflow) {
	const steps = jobSteps(workflow, 'stryker');
	if (!steps) return ['no stryker job'];
	const problems = [];
	const ids = steps.map((s, i) => (/^\s*id: mutation-logs\s*$/m.test(s) ? i : -1)).filter((i) => i >= 0);
	if (ids.length !== 1) return ['not one mutation log check (id: mutation-logs)'];
	const check = ids[0];
	const checkStep = steps[check];
	if (!gatedBy(checkStep, '${{ !cancelled() }}')) problems.push('the mutation log check does not run after a failed run');
	if (/^\s*continue-on-error:/m.test(checkStep)) problems.push('the mutation log check may continue on an error');
	if (ERREXIT_OFF.test(checkStep)) problems.push('the mutation log check turns off stopping on an error (set +e or the like, or a shell of its own)');
	if (!/^\s*for item in \.verify\/mutation\.log \.verify\/stryker\/reports; do\s*$/m.test(checkStep) || !/cp -RL "\$item" checked\//.test(checkStep))
		problems.push('the mutation log check does not copy the log and the report to checked/ with links followed (cp -RL)');
	for (const problem of checkLineProblems(checkStep, 'mutation log check')) problems.push(problem);
	let uploads = 0;
	const uploadSteps = new Set();
	steps.forEach((step, i) => {
		if (!/^\s*uses: actions\/upload-artifact@/m.test(step)) return;
		uploads++;
		uploadSteps.add(i);
		const name = nameOf(step, i);
		if (i < check) problems.push(`"${name}" uploads before the mutation log check`);
		if (!gatedBy(step, "${{ !cancelled() && steps.mutation-logs.outcome == 'success' }}")) problems.push(`"${name}" uploads without waiting for the mutation log check's success (exactly its gate, at the step's own level)`);
		const block = /^\s*path: \|\s*\n((?:\s{12,}\S[^\n]*\n?)+)/m.exec(step);
		const paths = block ? block[1].split('\n').map((line) => line.trim()).filter(Boolean) : [...step.matchAll(/^\s*path: (.+)$/gm)].map((m) => m[1].trim());
		if (paths.length === 0) problems.push(`"${name}" names no path`);
		for (const p of paths) if (!p.startsWith('checked/') || climbs(p)) problems.push(`"${name}" uploads ${p}, not the checked copy`);
	});
	if (uploads === 0) problems.push('no step uploads the checked report');
	// Stryker's output stays out of the public job log: the run writes it to the log file only, and is the one command of its
	// step; no other step names the log, the report or .verify at all.
	// Every step that runs the gate or Stryker counts, with --log or without (its self-test aside).
	const runs = steps.filter((s) => scriptOf(s).some((line) => /\bdotnet[- ]stryker\b/.test(line) || (/mutation-run\.mjs/.test(line) && !/--self-test/.test(line))));
	if (runs.length !== 1) problems.push('not one mutation run (mutation-run.mjs --log)');
	else {
		const lines = scriptOf(runs[0]);
		const command = (lines[0] ?? '').trim().replaceAll('"$GITHUB_WORKSPACE/', '"');
		if (!/^\s*ARIVA_MUTATION_QUIET: 'true'\s*$/m.test(runs[0]) || /\btee\b/.test(runs[0])) problems.push("the mutation run prints Stryker's output to the job log (ARIVA_MUTATION_QUIET: 'true', no tee)");
		if (lines.length !== 1 || !/^node "scripts\/mutation-run\.mjs" --log /.test(command) || /[;&|<>`]|\$[({\w]/.test(command))
			problems.push('the mutation run is not the one command of its step (node mutation-run.mjs --log, nothing before or after it)');
	}
	steps.forEach((step, i) => {
		if (i === check || runs.includes(step)) return;
		const name = nameOf(step, i);
		if (/\.verify\b/.test(step)) problems.push(`"${name}" reads the mutation log or report in place (it names .verify), not the checked copy`);
		if (!uploadSteps.has(i) && /\bchecked\b/.test(step)) problems.push(`"${name}" names checked/ outside the check and the uploads (a write into the copy after the check)`);
	});
	return problems;
}

/** A shell script's pipelines: split at ;, &, &&, || and line ends outside quotes and escapes (a pipe's | keeps them together). */
function pipelinesOf(script) {
	const blank = blankQuotes(script);
	const pipelines = [];
	let from = 0;
	for (let i = 0; i < blank.length; i++) {
		const pair = blank.slice(i, i + 2);
		const c = blank[i];
		// A & of a redirection (2>&1, &>) joins nothing.
		const redirect = c === '&' && (blank[i - 1] === '>' || blank[i + 1] === '>');
		if (c === '\n' || c === ';' || pair === '&&' || pair === '||' || (c === '&' && !redirect)) {
			pipelines.push(script.slice(from, i));
			if (pair === '&&' || pair === '||') i++;
			from = i + 1;
		}
	}
	pipelines.push(script.slice(from));
	return pipelines.map((p) => p.trim()).filter(Boolean);
}

/** The parts of a pipeline between its pipes, outside quotes and escapes (an escaped \| is text, as the shell reads it). */
function segmentsOf(pipeline) {
	const blank = blankQuotes(pipeline);
	const segments = [];
	let from = 0;
	for (let i = 0; i < blank.length; i++) {
		if (blank[i] === '|' && blank[i - 1] !== '>') {
			segments.push(pipeline.slice(from, i).trim());
			from = i + 1;
		}
	}
	segments.push(pipeline.slice(from).trim());
	return segments;
}

/**
 * True when a script substitutes a command: a $( or a backtick outside single quotes and $'...' quotes (inside double quotes the
 * shell still substitutes), whose output would escape the rule.
 */
function substitutes(script) {
	let quote = null;
	for (let i = 0; i < script.length; i++) {
		const c = script[i];
		if (quote === "'") {
			if (c === "'") quote = null;
			continue;
		}
		if (quote === "$'") {
			if (c === '\\') i++;
			else if (c === "'") quote = null;
			continue;
		}
		if (c === '\\') {
			i++;
			continue;
		}
		if (c === '`' || (c === '$' && script[i + 1] === '(')) return true;
		if (!quote && c === '$' && script[i + 1] === "'") {
			quote = "$'";
			i++;
		} else if (c === '"') quote = quote ? null : '"';
		else if (c === "'" && !quote) quote = "'";
	}
	return false;
}

// Words that only open or close a shell construct, and commands that print nothing a file holds (echo and printf print their own
// arguments; a grep -q tests; gh posts the comment).
const KEYWORDS = new Set(['{', '}', '(', ')', '!', 'if', 'then', 'else', 'elif', 'do', 'while', 'until']);
const QUIET = new Set(['echo', 'printf', '[', '[[', 'test', 'true', 'false', 'continue', 'break', 'exit', 'shift', 'fi', 'done', 'esac', 'for']);

// ARV-271a: what a comment step's whole script must not hold: a function, an alias or the like (each can turn tr or echo into
// something else), a PATH change, a tee (a second copy of a log into the comment), or a here-document.
const COMMENT_FORBIDDEN = /^\s*(?:function\s+[\w-]+|[\w-]+\s*\(\s*\))|\b(?:alias|hash|enable|shopt|tee|exec)\b|\bPATH=|<</m;

// The workflow values a comment step may take into its environment (a pull request's title or body is the author's text).
const COMMENT_ENV = new Set(['${{ github.token }}', '${{ github.event.pull_request.number }}', '${{ github.event.pull_request.head.repo.full_name == github.repository }}', '${{ matrix.image }}', '${{ steps.test-logs.outcome }}']);

/**
 * ARV-271 and ARV-271a (CWE-532 and Markdown injection): what is wrong with a step that writes a pull request comment from text it
 * did not write (a log, a scanner's table, a registry's answer): every pipeline of its script that prints anything but its own
 * words (any command but echo, printf, a test, a grep -q, an assignment, a loop's head or gh) ends with tr -d '\140', so a line
 * holding three backticks cannot end the code block and have the rest read as Markdown (an @mention among it); and the script
 * substitutes no command ($( or a backtick), whose output would reach an echo unfiltered. Inline variables before a command (LC_ALL=C
 * grep) and the words of if, while and the like are looked through. Returns what is wrong, empty when the step holds it.
 */
export function commentQuotes(step, file) {
	const script = commentScript(step, file);
	if (script === null) return ['the comment posts a file its step does not write (the check cannot read it)'];
	const problems = [];
	if (substitutes(script)) problems.push('the comment substitutes a command ($( or a backtick), whose output it could quote unfiltered');
	// A step that posts the comment is read whole: what it runs before the comment's group can change what the group runs.
	const whole = scriptOf(step).join('\n');
	if (/\bgh\s+(?:pr|issue)\s+comment\b/.test(whole)) {
		const blank = blankQuotes(whole);
		if (COMMENT_FORBIDDEN.test(blank)) problems.push('the comment step defines a function, an alias or the like, changes PATH, tees, redirects the whole script or has a here-document');
		const file = /--body-file\s+"?([\w./-]+)"?/.exec(whole)?.[1];
		if (file && new RegExp(`\\b(?:cp|mv|ln|install|dd|rsync|sed\\s+-[A-Za-z]*i|perl\\s+-[A-Za-z]*i)\\b[^\\n;|&]*${file.replace(/[.]/g, '\\.')}`).test(whole.replace(/["']/g, '')))
			problems.push(`the comment step copies, moves or edits into ${file} (only its groups and redirections write it)`);
		if (substitutes(whole)) problems.push('the comment step substitutes a command ($( or a backtick)');
		const posts = [...whole.matchAll(/\bgh\s+(?:pr|issue)\s+comment\b[^\n;|&]*/g)].map((m) => m[0]);
		if (posts.length !== 1 || !/--body-file\s+"?[\w./-]+"?\s*$/.test(posts[0]) || /--body-file\s+-\s|\s(?:--body|-b|-F|--editor|-e)\s/.test(` ${posts[0]} `))
			problems.push('the comment step does not post exactly one comment from its body file (gh ... comment --body-file <file>, no --body, -b, -F or stdin)');
		for (const [, value] of step.matchAll(/^ {10}[A-Za-z_]\w*: (.*\$\{\{.*)$/gm)) if (!COMMENT_ENV.has(value.trim())) problems.push(`the comment step takes ${value.trim()} into its environment (only the token, the number, the repository check, the image and the check's outcome)`);
	}
	let quoted = 0;
	for (const pipeline of pipelinesOf(script)) {
		const segments = segmentsOf(pipeline);
		const words = segments[0].split(/\s+/);
		while (words.length && (KEYWORDS.has(words[0]) || /^[A-Za-z_]\w*=/.test(words[0]))) {
			if (/^[A-Za-z_]\w*=/.test(words[0]) && words.length === 1 && segments.length === 1) words.length = 0;
			else words.shift();
		}
		const command = words[0];
		if (command === undefined || command.startsWith('>')) continue;
		const grepTest = command === 'grep' && /^-[A-Za-z]*q[A-Za-z]*$/.test(words[1] ?? '');
		// set prints every variable (the token among them) without an option.
		const setOption = command === 'set' && /^[+-]/.test(words[1] ?? '');
		if ((QUIET.has(command) || grepTest || setOption) && segments.length === 1) continue;
		if (command === 'gh') {
			problems.push(`the comment runs "${pipeline.slice(0, 60)}" (gh reads what no check has read)`);
			continue;
		}
		quoted++;
		if (segments.length < 2 || segments.at(-1) !== "tr -d '\\140'") problems.push(`the comment quotes "${pipeline.slice(0, 60)}" with its backticks`);
		// A copy into a file from inside the pipe (a redirection before the filter) bypasses it.
		if (/>/.test(blankQuotes(segments.slice(0, -1).join(' | ')).replace(/[0-9]?>\s*\/dev\/null|[0-9]?>&[0-9]/g, ''))) problems.push(`the comment writes "${pipeline.slice(0, 60)}" to a file before its filter`);
	}
	if (quoted === 0) problems.push('the comment quotes nothing (the check reads nothing)');
	return problems;
}

/**
 * The part of a comment step's script that writes the comment's body file (gh's --body-file): the commands of each { ... } group
 * redirected into it, on one line or many, and each command redirected into it on its own. What the step prints to its job log
 * (a cat after the comment) is not part of it. The file is gh's --body-file in the step, or the one given (a comment another step
 * posts). Null when the step writes no part of the file.
 */
function commentScript(step, given) {
	const file = given ?? /--body-file\s+"?([\w./-]+)"?/.exec(step)?.[1];
	if (!file) return null;
	// The file as a path may name it: ./file, "$GITHUB_WORKSPACE/file".
	const into = `>{1,2}\\s*"?(?:\\./|\\$GITHUB_WORKSPACE/)?${file.replace(/[.]/g, '\\.')}"?\\s*$`;
	const lines = scriptOf(step);
	const parts = [];
	const taken = new Set();
	// A construct over many lines redirected into the file at its close (a group, a subshell, an if, a loop, a case): its lines
	// from the opener at the closer's level.
	lines.forEach((line, end) => {
		if (!new RegExp(`^\\s*(?:\\}|\\)|fi|done|esac)\\s*${into}`).test(line)) return;
		const level = line.search(/\S/);
		for (let start = end - 1; start >= 0; start--) {
			if (lines[start].search(/\S/) !== level || !/^\s*(?:\{|\(|if\b|for\b|while\b|until\b|case\b)/.test(lines[start])) continue;
			parts.push(lines[start].trim().replace(/^[{(]/, ''), ...lines.slice(start + 1, end));
			for (let i = start; i <= end; i++) taken.add(i);
			break;
		}
	});
	lines.forEach((raw, i) => {
		if (taken.has(i)) return;
		const line = raw.trim();
		const oneLine = new RegExp(`^\\{(.*)\\}\\s*${into}`).exec(line);
		if (oneLine) {
			parts.push(oneLine[1]);
			return;
		}
		const single = new RegExp(`^(.*?)\\s*${into}`).exec(line);
		if (single) parts.push(single[1]);
	});
	return parts.length ? parts.join('\n') : null;
}

/**
 * ARV-271: the E2E job's failure comment (its "Report failures on the pull request" step) quotes no backtick from a log line, by
 * commentQuotes (ARV-271a: every reader, not only the lines starting with grep, sed or tail). Returns what is wrong, empty when
 * the comment holds it.
 */
export function e2eCommentQuotes(workflow) {
	return failureCommentQuotes(workflow, 'e2e');
}

/** ARV-271a: a job's "Report failures on the pull request" step, by commentQuotes (the backend job's as the E2E job's). */
export function failureCommentQuotes(workflow, job) {
	const steps = jobSteps(workflow, job);
	if (!steps) return [`no ${job} job`];
	const step = steps.find((s) => /^(?:name: |\s*name: )Report failures on the pull request\s*$/m.test(s));
	if (!step) return [`no "Report failures on the pull request" step in the ${job} job`];
	const others = steps.filter((s) => s !== step && /\bfailure\.md\b/.test(s)).map((s, i) => `"${nameOf(s, i)}" names failure.md (only the failure comment's step writes and posts it)`);
	return [...commentQuotes(step), ...others];
}

/**
 * ARV-271a: the backend job's test counts comment: its "Post test counts on the pull request" step posts test-summary.md, which the
 * "Test counts" step writes, so that step's lines into the file are read by commentQuotes (the counts and the suite's name come
 * from fixed patterns of the TRX's own attributes, read by grep -o). Returns what is wrong, empty when it holds.
 */
export function summaryCommentQuotes(workflow) {
	const steps = jobSteps(workflow, 'backend');
	if (!steps) return ['no backend job'];
	const post = steps.find((s) => /--body-file test-summary\.md\b/.test(s));
	const write = steps.find((s) => /^(?:name: |\s*name: )Test counts \(fails when a suite ran no tests\)\s*$/m.test(s));
	if (!post || !write) return ['no test counts comment (a step writing test-summary.md and one posting it)'];
	const others = steps.filter((s) => s !== post && s !== write && /\btest-summary\.md\b/.test(s)).map((s, i) => `"${nameOf(s, i)}" names test-summary.md (only Test counts writes it)`);
	const posting = scriptOf(post).join('\n').trim() === "'[ -f test-summary.md ] && gh pr comment \"$PR\" --repo \"$GITHUB_REPOSITORY\" --body-file test-summary.md || true'" ? [] : ['the test counts comment\'s post step does more than post test-summary.md'];
	return [...commentQuotes(write, 'test-summary.md'), ...others, ...posting];
}

/**
 * ARV-271a: every other workflow's pull request comments (a step that runs gh pr comment or gh issue comment) quote what they did
 * not write by commentQuotes. CI's own are held apart: its failure comments by failureCommentQuotes and its test counts comment by
 * summaryCommentQuotes. Takes [file name, text] pairs; returns what is wrong, each naming the file and the step.
 */
export function pullRequestCommentQuotes(workflows) {
	const problems = [];
	let comments = 0;
	for (const [file, text] of workflows) {
		if (file === 'ci.yml') continue;
		for (const job of workflowJobs(text)) {
			for (const step of job.steps) {
				if (!/\bgh\s+(?:pr|issue)\s+comment\b/.test(scriptOf(step.text).join('\n'))) continue;
				comments++;
				for (const problem of commentQuotes(step.text)) problems.push(`${file}: "${step.name}": ${problem}`);
			}
		}
	}
	if (comments === 0) problems.push('no workflow but ci.yml posts a pull request comment (the check reads nothing)');
	return problems;
}

// ARV-271a: the workflow text these checks guard, each pinned by a hash of its text (trailing blanks off each line, an action's
// commit SHA and an image's digest as placeholders, so the bots' pin bumps pass; an action's SHA is left to Dependabot and
// review). The checks above read the shell as text and can be worded around (review remains the control for an edit made to
// pass them); a pin makes any edit of a guarded text fail the self-test until its hash here is changed with it, so the change
// shows in this file's diff for review. Pinned whole: every job whose token can write to pull requests or issues (pull-requests
// or issues write, write-all, or no permissions at all, the repository's default), the backend and mutation jobs whose logs the
// checks hold, and the security gate that runs this self-test; with each, its workflow's text outside jobs: (on, permissions,
// env, defaults). A pinned job may use no local action (its file is not pinned), and a route to a pull request comment found in a
// job that is not pinned fails as well. `node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs --pins` prints the hashes
// as they are: update one only after reading the change against the checks.
const PINS = {
	'ci.yml header': '40bc804bd779d6b746554a4a0f937149659408e3baa391b5a6ba5679dfcb97da',
	'ci.yml security-gate': 'e49b3c293c8f4af57a5f2558b615a2a1527ec30ab465c4ae0dde071a61247837',
	'ci.yml backend': 'b29c38001541aea9952d991b6ce22cf58f759b4b6ef5cee16468226c37b0c7d8',
	'ci.yml e2e': '2216433c743748719f49fa257d0dd388959887523015d5d3be18a8d85bce445a',
	'mutation.yml header': 'b69f30202bc5fdb6671d666ea25d1e67a1c6fb61369debb46956e39c34f920c0',
	'mutation.yml stryker': 'd0831436cd7a735a5148e512b12225cce4f9c92ecd948469714e5133a4a083aa',
	'images.yml header': 'e7b29c927fcb01d42bf67ba71ef7482976648e30f94e53cf65f3da4509d208e3',
	'images.yml build': '592a3c41fc076bfa9b57c929b70f241c062289b4644e3778e34a8b786ce1627a',
	'security-scan.yml header': '97cd4fc964ca55ae45806adebe649ccd20be5ed2ac094abf845c7aae6c566d45',
	'security-scan.yml trivy': 'ae43f83e31412f90e7fe6b03753116927ae9b267a6527adb14d2d9330989189d',
	'base-images.yml header': '77ad007ca3c2793c276beed353ca261a9dec88d112753682eba86b7d92f8542a',
	'base-images.yml resolve': '988d3b5e430cbfa6e954fc37806452097dd7a2d9373dd03625aa33a159e214c7',
	'claude-review.yml header': '5683c33c153fccd4d20efb015598d5f8e1966f9b7062b952c726c035c23cebbc',
	'claude-review.yml security-review': '0a4eba8290e1bdd926961bbba0d64fbc7dfe4e80691ae7117104e659386f228e',
	'claude-review.yml mention': '9e40b9e79dd04d068aa3fc13f89f3ce8bd48679e7a3f7b0a7490da8418b597a5'
};

/**
 * ARV-271a: a workflow read as the pins need it, failing closed on what this reader cannot read (it is not a YAML parser): its
 * header (the text before jobs:), and each job (a plain key at two spaces under jobs:, a comment after it allowed) with its raw
 * text and its steps' raw text. What is wrong: no plain jobs: key, a line under jobs: at two spaces that is not a plain job key (a
 * quoted key, a flow mapping, an anchor or a merge), a key at column 0 or 1 after jobs: (top-level settings go before it, in the
 * pinned header), a job twice, a YAML anchor, alias or merge key outside a run block, a steps: that is not a plain block, and a
 * job that calls a reusable workflow (whose jobs this reader does not follow).
 */
function readWorkflow(text) {
	const lines = text.replace(/\r\n/g, '\n').split('\n');
	const problems = [];
	const jobsAt = lines.findIndex((line) => /^jobs:\s*(?:#.*)?$/.test(line));
	if (jobsAt < 0) return { header: text, jobs: [], problems: ['no jobs: key this reader can read (a plain jobs: at column 0)'] };
	const jobs = [];
	let current = null;
	let block = -1;
	lines.forEach((line, i) => {
		const level = line.search(/\S/);
		if (block >= 0 && level >= 0 && level <= block) block = -1;
		const note = level < 0 || /^\s*#/.test(line);
		if (block < 0 && !note) {
			if (/:\s+[&*][\w-]|^\s*-\s+[&*][\w-]|^\s*<<\s*:/.test(line)) problems.push(`line ${i + 1}: a YAML anchor, alias or merge key, which this reader does not follow`);
			const opens = /^(\s*)(- )?[\w-]+: [|>][-+]?\s*(?:#.*)?$/.exec(line);
			if (opens) block = opens[1].length + (opens[2] ? 2 : 0);
		}
		if (i <= jobsAt) return;
		if (note) {
			if (current) current.lines.push(line);
			return;
		}
		if (level < 2) {
			problems.push(`line ${i + 1}: a key at column ${level} after jobs: (a workflow's settings go before jobs:, where its header is pinned)`);
			current = null;
			return;
		}
		if (level === 2) {
			const key = /^ {2}([A-Za-z_][\w-]*):\s*(?:#.*)?$/.exec(line);
			if (!key) {
				problems.push(`line ${i + 1}: under jobs:, not a plain job key (a quoted key, a flow mapping and the like are not read)`);
				current = null;
				return;
			}
			if (jobs.some((job) => job.name === key[1])) problems.push(`the job ${key[1]} twice`);
			current = { name: key[1], lines: [line] };
			jobs.push(current);
			return;
		}
		if (current) current.lines.push(line);
	});
	return {
		header: lines.slice(0, jobsAt).join('\n'),
		problems,
		jobs: jobs.map((job) => {
			const raw = `${job.lines.join('\n')}\n`;
			const parts = raw.split(/^(?= {6}- )/m);
			const steps = parts.slice(1).map((part, i) => {
				const stripped = stripComments(part).replace(/^ {6}- /, '');
				return { name: nameOf(stripped, i), raw: part, text: stripped };
			});
			// The job's own keys at four spaces, each a plain key; its steps a block whose items start at six spaces with their
			// keys at eight (review round 4: keys at another depth, or a complex key, hid its permissions and steps).
			let inSteps = false;
			for (const line of job.lines.slice(1)) {
				const level = line.search(/\S/);
				if (level < 0 || /^\s*#/.test(line)) continue;
				if (level < 4) problems.push(`the ${job.name} job has a line at ${level} spaces (its keys at four)`);
				else if (level === 4) {
					if (!/^ {4}[A-Za-z_][\w-]*:(?:\s|$)/.test(line)) problems.push(`the ${job.name} job has a key at four spaces that is not a plain key (a quoted or complex key, a list)`);
					inSteps = /^ {4}steps:/.test(line);
				} else if (inSteps && (level === 5 || level === 7 || (level === 6 && !/^ {6}- /.test(line)))) problems.push(`the ${job.name} job's steps are not items at six spaces with their keys at eight`);
				if (/^ {4}steps:/.test(line) && !/^ {4}steps:\s*(?:#.*)?$/.test(line)) problems.push(`the ${job.name} job's steps: is not a plain block`);
				if (/^ {4}uses:/.test(line)) problems.push(`the ${job.name} job calls a reusable workflow, which this reader does not follow`);
			}
			if (!job.lines.some((line) => /^ {4}steps:/.test(line))) problems.push(`the ${job.name} job has no steps: at four spaces`);
			return { name: job.name, raw, steps, declaresSteps: job.lines.some((line) => /^ {4}steps:/.test(line)) };
		})
	};
}

/** The jobs of a workflow (see readWorkflow). */
const workflowJobs = (text) => readWorkflow(text).jobs;

/** The text a pin hashes: its lines as they are (a blank after a line's \ changes what the shell runs), an action's commit SHA and an image's digest as placeholders. */
const pinText = (text) => text.replace(/\r\n/g, '\n').replace(/\n+$/, '').replace(/@[0-9a-f]{40}(?: # v[\w.+-]+)?/g, '@<sha>').replace(/@sha256:[0-9a-f]{64}/g, '@sha256:<digest>');

// A step's route to a pull request comment, read with its comments out (a tripwire for a job without write permissions that
// posts with a token of its own): gh pr|issue comment, review, edit or create, gh api on comments or reviews, GitHub's API by
// URL or GraphQL mutation, a comment, script or Claude action, and a local action.
const COMMENT_ROUTE = /\bgh"?\s+"?(?:pr|issue)"?\s+"?(?:comment|review|edit|create)\b|\bgh\s+api\b[^\n]*\b(?:comments|reviews|graphql)\b|api\.github\.com|\/(?:comments|reviews)\b|\b(?:addComment|addPullRequestReview)\b|^\s*uses:\s*(?:\.\/|[\w.-]+\/[\w.-]*(?:comment|github-script|claude-code-action)[\w.-]*@)/im;

// The scopes whose write lets a token post where people read it: pull request and issue comments, commit comments (contents),
// discussions.
const WRITER_SCOPES = new Set(['pull-requests', 'issues', 'contents', 'discussions']);

/**
 * ARV-271a: what permissions at an indentation (0 for the workflow's, 4 for a job's) let the token do, read failing closed:
 * null when the key is absent; true when it can write to a writer scope, or is spelled otherwise than this reader reads
 * (read-all, write-all, {} or a block of `<scope>: read|write|none` lines, a comment after each allowed), or appears twice.
 */
function permissionsOf(text, indent) {
	const lines = text.split('\n');
	const keys = lines.map((line, i) => (new RegExp(`^ {${indent}}permissions:`).test(line) ? i : -1)).filter((i) => i >= 0);
	if (keys.length === 0) return null;
	if (keys.length > 1) return true;
	const value = lines[keys[0]].slice(indent + 'permissions:'.length).replace(/\s+#.*$/, '').trim();
	if (value === 'read-all' || value === '{}') return false;
	if (value !== '') return true;
	let writes = false;
	for (const line of lines.slice(keys[0] + 1)) {
		if (line.trim() === '' || /^\s*#/.test(line)) continue;
		if (line.search(/\S/) <= indent) break;
		const scope = /^\s*([a-z-]+): (read|write|none)\s*(?:#.*)?$/.exec(line);
		if (!scope) return true;
		if (scope[2] === 'write' && WRITER_SCOPES.has(scope[1])) writes = true;
	}
	return writes;
}

/** True when permissions (a job's, else its workflow's) let the token post; absent at both levels is the default, so it may. */
const writesComments = (permissions) => permissions !== false;

/**
 * The pins' current hashes from [file name, text] pairs (a target not found is null), and what is wrong: a job that can write to
 * pull requests or issues and is not pinned, a pinned job using a local action, a route to a pull request comment in a job that
 * is not pinned, a job that declares steps none of which is read (its steps not at six spaces), a workflow with jobs: but no
 * job read, and a pinned job whose workflow's header is not pinned.
 */
export function workflowPins(workflows) {
	const hashes = Object.fromEntries(Object.keys(PINS).map((key) => [key, null]));
	const problems = [];
	const hash = (text) => crypto.createHash('sha256').update(pinText(text)).digest('hex');
	for (const [file, text] of workflows) {
		const { header, jobs, problems: unread } = readWorkflow(text);
		for (const problem of unread) problems.push(`${file}: ${problem}`);
		if (jobs.length === 0) problems.push(`${file}: no job read under jobs: (jobs at two spaces)`);
		if (`${file} header` in hashes) hashes[`${file} header`] = hash(header);
		const workflowPermissions = permissionsOf(header, 0);
		for (const job of jobs) {
			const key = `${file} ${job.name}`;
			if (job.declaresSteps && job.steps.length === 0) problems.push(`${file}: the ${job.name} job's steps are not read (steps at six spaces)`);
			const pinned = key in PINS;
			if (pinned) {
				hashes[key] = hash(job.raw);
				if (!(`${file} header` in PINS)) problems.push(`${file}: the ${job.name} job is pinned but its workflow's header is not`);
				if (job.steps.some((step) => /^\s*uses:\s*\.\//m.test(step.text))) problems.push(`${key}: uses a local action, whose file is not pinned`);
				continue;
			}
			const permissions = permissionsOf(job.raw, 4) ?? workflowPermissions;
			if (writesComments(permissions)) problems.push(`${key}: its token can write to pull requests or issues, and it is not pinned`);
			for (const step of job.steps) if (COMMENT_ROUTE.test(step.text)) problems.push(`${file}: "${job.name}: ${step.name}" posts to the pull request outside the pinned jobs`);
		}
	}
	for (const [key, value] of Object.entries(hashes)) {
		if (value === null) problems.push(`${key}: the pinned job or header is no longer there`);
		else if (value !== PINS[key]) problems.push(`${key}: changed since it was pinned; read it against the checks, then update its hash in PINS (--pins prints them)`);
	}
	return { hashes, problems };
}

/** The repository's workflows as [file name, text] pairs (.yml and .yaml). */
const readWorkflows = () => fs.readdirSync(WORKFLOWS).filter((f) => /\.ya?ml$/.test(f)).sort().map((f) => [f, fs.readFileSync(path.join(WORKFLOWS, f), 'utf8')]);

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
		// ARV-252: CI's unit and integration suites write the in-process hosts' console logs (Serilog's JsonFormatter with the
		// message rendered, as ArivaLogging configures the console) to unit.log and integration.log, and the TRX keeps a test's
		// output. A credential planted in a host line, or in a TRX's output, fails the check on that file and is never printed;
		// the hosts' usual lines, password prose included, pass.
		const hostLine = (level, template, properties = {}, exception) => {
			const rendered = template.replace(/\{(\w+)\}/g, (_, key) => JSON.stringify(properties[key]));
			return JSON.stringify({ Timestamp: '2026-10-27T23:55:30.3720616+00:00', Level: level, MessageTemplate: template, RenderedMessage: rendered, ...(exception ? { Exception: exception } : {}), Properties: { ...properties, SourceContext: 'Ariva.Infra.Sensing.DeviceRegistry', Application: 'api-ingest', Environment: 'vm-local' } });
		};
		const hostLog = [
			hostLine('Information', 'Application started. Press Ctrl+C to shut down.'),
			hostLine('Error', 'An error occurred while reading the key ring.', {}, 'Npgsql.PostgresException (0x80004005): 28P01: password authentication failed for user "ariva_runtime"'),
			hostLine('Warning', 'Device {DeviceId} refused: {Reason}', { DeviceId: 'DMO-T1-S01', Reason: 'credential ardk_[redacted] is retired' }),
			'passed Ariva.IntegrationTests.Sensing.DeviceHealthTests.Health_Should_BeLimitedToTheCallersSites (412ms)'
		].join('\n');
		const xml = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');
		const trx = (output) => `<TestRun><Results><UnitTestResult testName="Ariva.UnitTests.Security.TotpTests.Normalize(typed: &quot;ABCDE-FGHJK&quot;)" outcome="Passed"><Output><StdOut>${xml(output)}</StdOut></Output></UnitTestResult></Results></TestRun>`;
		const backendDir = path.join(dir, 'checked');
		fs.mkdirSync(path.join(backendDir, 'TestResults'), { recursive: true });
		const planted = `ardk_${random(32)}`;
		const plantedLine = hostLine('Information', 'Device {DeviceId} registered with {Credential}', { DeviceId: 'DMO-T1-S02', Credential: planted });
		const unitLog = path.join(backendDir, 'unit.log');
		const integrationLog = path.join(backendDir, 'integration.log');
		const results = path.join(backendDir, 'TestResults', 'integration-tests.trx');
		const plants = [
			['integration.log', () => fs.writeFileSync(integrationLog, `${hostLog}\n${plantedLine}\n`)],
			['integration-tests.trx', () => fs.writeFileSync(results, trx(`${hostLog}\n${plantedLine}`))]
		];
		for (const [file, plant] of plants) {
			fs.writeFileSync(unitLog, hostLog);
			fs.writeFileSync(integrationLog, hostLog);
			fs.writeFileSync(results, trx(hostLog));
			plant();
			const logged = [];
			if (checkArtifacts([backendDir], { env: {}, log: (l) => logged.push(l) })) problems.push(`a credential planted in ${file} passed the check`);
			const found = logged.filter((l) => l.startsWith('SECRET '));
			if (!found.some((l) => l.startsWith('SECRET a device or display credential') && l.endsWith(file))) problems.push(`the check does not name ${file} and the kind of the planted credential`);
			if (found.some((l) => !l.endsWith(file))) problems.push(`a clean host log or result was flagged: ${found.join('; ')}`);
			if (logged.some((l) => l.includes(planted))) problems.push('the planted credential was printed');
		}
		fs.writeFileSync(integrationLog, hostLog);
		fs.writeFileSync(results, trx(hostLog));
		if (!checkArtifacts([backendDir], { env: {}, log: () => {} })) problems.push("the hosts' usual log lines failed the check");
		// The workflow's own wiring, and the wiring check firing on each way to break it: a suite teed to the job log, run again
		// after the check or through dotnet test, or with no timeout of its own; the check before a suite, skipped by a failed
		// suite, allowed to continue on an error, or reading the logs in place; a log read in place, quoted unchecked or
		// printed with workflow commands live; a result uploaded unchecked or from outside the copy; and a gate written only
		// in a comment.
		const workflow = fs.readFileSync(CI_WORKFLOW, 'utf8');
		for (const problem of backendLogWiring(workflow)) problems.push(`ci.yml: ${problem}`);
		const stepAt = (text, name) => {
			const start = text.indexOf(`      - name: ${name}\n`);
			const next = text.slice(start + 1).search(/^ {6}- /m);
			return start < 0 || next < 0 ? null : text.slice(start, start + 1 + next);
		};
		const checkStep = stepAt(workflow, 'Test logs hold no secret (CWE-532)');
		const integrationStep = stepAt(workflow, 'Integration tests (Testcontainers, Docker on the runner)');
		const unitStep = stepAt(workflow, 'Unit, architecture and security tests');
		// The replacement as a function: in a string, $' and the like are replacement patterns, not text.
		const replace = (from, to) => (text) => (text.includes(from) ? text.replace(from, () => to) : null);
		const miswired = {
			'a suite teed to the job log': replace('> unit.log 2>&1', '2>&1 | tee unit.log'),
			'a suite run again after the check': (text) => (checkStep && unitStep ? text.replace(checkStep, checkStep + unitStep.replace('> unit.log 2>&1', '2>&1 | tee unit.log')) : null),
			'a suite run through dotnet test': replace('timeout -k 30s 14m dotnet Platform/Backplane/Ariva.UnitTests/bin/Release/net10.0/Ariva.UnitTests.dll', 'dotnet test Platform/Backplane/Ariva.UnitTests/Ariva.UnitTests.csproj'),
			'a suite without a timeout of its own': replace('        timeout-minutes: 28\n', ''),
			'the check before the integration suite': (text) => (checkStep && integrationStep ? text.replace(checkStep, '').replace(integrationStep, checkStep + integrationStep) : null),
			'a check a failed suite skips': replace('id: test-logs\n        if: ${{ !cancelled() }}', 'id: test-logs\n        if: success()'),
			'a check that continues on an error': replace('id: test-logs\n', 'id: test-logs\n        continue-on-error: true\n'),
			'a check of the logs in place': replace('artifact-secrets.mjs checked\n', 'artifact-secrets.mjs unit.log integration.log TestResults\n'),
			'a log read in place': replace('for log in checked/unit.log checked/integration.log; do', 'for log in unit.log integration.log; do'),
			'a log quoted unchecked': replace('if [ "$LOGS_CHECKED" = success ]; then', 'if true; then'),
			'a log printed with workflow commands live': replace('echo "::stop-commands::$token"', 'echo "commands live"'),
			'commands resumed on a log line': replace(`printf '\\n::%s::\\n' "$token"`, 'echo "::$token::"'),
			'a result uploaded unchecked': replace("if: ${{ !cancelled() && steps.test-logs.outcome == 'success' }}\n        uses: actions/upload-artifact", 'if: always()\n        uses: actions/upload-artifact'),
			'a result uploaded from outside the copy': replace('path: checked/TestResults\n', 'path: TestResults\n'),
			'a gate in a comment': replace("if: ${{ !cancelled() && steps.test-logs.outcome == 'success' }}\n        uses: actions/upload-artifact", "if: always() # steps.test-logs.outcome == 'success'\n        uses: actions/upload-artifact")
		};
		miswired['a copy that keeps links'] = replace('cp -RL "$item" checked/', 'cp -R "$item" checked/');
		miswired['a check that goes on after an error'] = replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068', '          set +e\n          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068');
		miswired['a check that turns errexit off by name'] = replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068', '          set +o errexit\n          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068');
		miswired['a check that turns errexit off among other flags'] = replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068', '          set +eu\n          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068');
		miswired['a check with a shell of its own'] = replace('id: test-logs\n', 'id: test-logs\n        shell: bash {0}\n');
		miswired['a gate or-ed away'] = replace("if: ${{ !cancelled() && steps.test-logs.outcome == 'success' }}\n        uses: actions/upload-artifact", "if: ${{ always() || steps.test-logs.outcome == 'success' }}\n        uses: actions/upload-artifact");
		for (const [label, change] of Object.entries(miswired)) {
			const changed = change(workflow);
			if (changed === null || changed === workflow) problems.push(`ci.yml no longer has the text that ${label} changes: update the wiring cases`);
			else if (backendLogWiring(changed).length === 0) problems.push(`the wiring check passes ${label}`);
		}
		// ARV-271: the E2E failure comment and the mutation workflow, each with the ways to break it.
		for (const problem of e2eCommentQuotes(workflow)) problems.push(`ci.yml: ${problem}`);
		const quotes = {
			'a failing test quoted with its backticks': replace("| sort -u | head -60 | cut -c1-300 | tr -d '\\140'\n", '| sort -u | head -60 | cut -c1-300\n'),
			"a host log's errors quoted with their backticks": replace(`"$log" | head -8 | cut -c1-600 | tr -d '\\140'; echo`, '"$log" | head -8 | cut -c1-600; echo')
		};
		for (const [label, change] of Object.entries(quotes)) {
			const changed = change(workflow);
			if (changed === null || changed === workflow) problems.push(`ci.yml no longer has the text that ${label} changes: update the comment cases`);
			else if (e2eCommentQuotes(changed).length === 0) problems.push(`the comment check passes ${label}`);
		}
		const mutation = fs.readFileSync(MUTATION_WORKFLOW, 'utf8');
		for (const problem of mutationLogWiring(mutation)) problems.push(`mutation.yml: ${problem}`);
		const mutationMiswired = {
			'a report uploaded unchecked': replace("if: ${{ !cancelled() && steps.mutation-logs.outcome == 'success' }}", 'if: always()'),
			'a gate or-ed away': replace("if: ${{ !cancelled() && steps.mutation-logs.outcome == 'success' }}", "if: ${{ always() || steps.mutation-logs.outcome == 'success' }}"),
			'the log uploaded from outside the copy': replace('            checked/mutation.log\n', '            .verify/mutation.log\n'),
			'the report uploaded from outside the copy': replace('            checked/reports/\n', '            .verify/stryker/reports/\n'),
			'no check': replace('        id: mutation-logs\n', ''),
			'a check a failed run skips': replace('id: mutation-logs\n        if: ${{ !cancelled() }}', 'id: mutation-logs\n        if: success()'),
			'a check that continues on an error': replace('id: mutation-logs\n', 'id: mutation-logs\n        continue-on-error: true\n'),
			'a copy that keeps links': replace('cp -RL "$item" checked/', 'cp -R "$item" checked/'),
			'a check of the files in place': replace('artifact-secrets.mjs checked\n', 'artifact-secrets.mjs .verify\n'),
			'a check that goes on after an error': replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      - name: Mutation report', '          set +e\n          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      - name: Mutation report'),
			'a check with a shell of its own': replace('id: mutation-logs\n', 'id: mutation-logs\n        shell: bash {0}\n'),
			"a run that prints Stryker's output": replace("ARIVA_MUTATION_QUIET: 'true'", "ARIVA_MUTATION_QUIET: 'false'"),
			'the log printed in place': replace('      - name: Mutation report\n', '      - name: Print the log\n        run: tail -n 50 .verify/mutation.log\n      - name: Mutation report\n')
		};
		for (const [label, change] of Object.entries(mutationMiswired)) {
			const changed = change(mutation);
			if (changed === null || changed === mutation) problems.push(`mutation.yml no longer has the text that ${label} changes: update the wiring cases`);
			else if (mutationLogWiring(changed).length === 0) problems.push(`the mutation wiring check passes ${label}`);
		}
		// ARV-271a: the edits that passed the wiring checks before: the test results read before the check, a log read outside a
		// step's LOGS_CHECKED block (before it, or in its else), a log named through a variable, a negated gate, the check line
		// in a function, after an exit or in a here-document, an upload through checked/../ and a write into checked/ after the
		// check.
		const backendMore = {
			'the test results read before the check': replace('      - name: Test logs hold no secret (CWE-532)\n', '      - name: Peek at the results\n        run: head -c 200 TestResults/unit-tests.trx\n      - name: Test logs hold no secret (CWE-532)\n'),
			'a log read before its LOGS_CHECKED block': replace('          if [ "$LOGS_CHECKED" = success ]; then\n            for log in', '          tail -n 3 checked/unit.log >> test-summary.md\n          if [ "$LOGS_CHECKED" = success ]; then\n            for log in'),
			"a log read in its LOGS_CHECKED block's else": replace('              echo "#### Test logs: not quoted', '              tail -n 3 checked/unit.log\n              echo "#### Test logs: not quoted'),
			'a log named through a variable before the check': replace('      - name: Test logs hold no secret (CWE-532)\n', '      - name: Peek at a log\n        run: suite=unit; tail -n 3 "$suite.log"\n      - name: Test logs hold no secret (CWE-532)\n'),
			'a negated gate': replace("if: ${{ !cancelled() && steps.test-logs.outcome == 'success' }}\n        uses: actions/upload-artifact", "if: ${{ !cancelled() && !(steps.test-logs.outcome == 'success') }}\n        uses: actions/upload-artifact"),
			'the check in a function never called': replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068', '          check() {\n            node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n          }\n      # ARV-068'),
			'the check after an exit': replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068', '          exit 0\n          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068'),
			'the check in a here-document': replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068', "          cat > /dev/null <<'END'\n          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      # ARV-068"),
			'an upload through checked/../': replace('path: checked/TestResults\n', 'path: checked/../TestResults\n'),
			'a write into checked/ after the check': replace('          token=$(openssl rand -hex 16)\n          for log in checked/unit.log', '          cp unit.log checked/unit.log\n          token=$(openssl rand -hex 16)\n          for log in checked/unit.log')
		};
		for (const [label, change] of Object.entries(backendMore)) {
			const changed = change(workflow);
			if (changed === null || changed === workflow) problems.push(`ci.yml no longer has the text that ${label} changes: update the wiring cases`);
			else if (backendLogWiring(changed).length === 0) problems.push(`the wiring check passes ${label}`);
		}
		const mutationMore = {
			'an upload through checked/../': replace('            checked/mutation.log\n', '            checked/../mutation.log\n'),
			'a write into the copy after the check, in the check step': replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      - name: Mutation report', '          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n          cp .verify/mutation.log checked/late.log\n      - name: Mutation report'),
			'a write into the copy in a step before the upload': replace('      - name: Mutation report\n', '      - name: Late copy\n        run: date > checked/late.txt\n      - name: Mutation report\n'),
			'a read on the run line': replace('--output "$GITHUB_WORKSPACE/.verify/stryker"\n', '--output "$GITHUB_WORKSPACE/.verify/stryker"; tail -n 5 .verify/mutation.log\n'),
			'an inline quiet setting before the run': replace('run: node "$GITHUB_WORKSPACE/scripts/mutation-run.mjs"', 'run: ARIVA_MUTATION_QUIET=false node "$GITHUB_WORKSPACE/scripts/mutation-run.mjs"'),
			'a glob read of the log': replace('      - name: Mutation report\n', '      - name: Print the logs\n        run: cat .verify/*.log\n      - name: Mutation report\n'),
			'the check in a function never called': replace('          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n      - name: Mutation report', '          check() {\n            node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n          }\n      - name: Mutation report')
		};
		for (const [label, change] of Object.entries(mutationMore)) {
			const changed = change(mutation);
			if (changed === null || changed === mutation) problems.push(`mutation.yml no longer has the text that ${label} changes: update the wiring cases`);
			else if (mutationLogWiring(changed).length === 0) problems.push(`the mutation wiring check passes ${label}`);
		}
		// Every reader of a failure comment, not only grep, sed or tail lines: head, cat, awk, a grep behind an inline variable, a
		// grep joined by && or after a grep -q, and a substitution in an echo; and the backend job's failure comment as the E2E one.
		const webServer = "              grep '\\[WebServer\\]' e2e.log | tail -n 40 | cut -c1-300 | tr -d '\\140'\n";
		const e2eMore = {
			'a head line': replace(webServer, '              head -n 40 e2e.log\n'),
			'a cat line': replace(webServer, '              cat e2e.log\n'),
			'an awk line': replace(webServer, "              awk 'NR < 40' e2e.log\n"),
			'a grep behind an inline variable': replace(webServer, "              LC_ALL=C grep '\\[WebServer\\]' e2e.log | tail -n 40\n"),
			'a grep joined by && to a filtered echo': replace(webServer, "              grep '\\[WebServer\\]' e2e.log && echo done | tr -d '\\140'\n"),
			'a grep after a grep -q': replace(webServer, "              grep -q x e2e.log; grep '\\[WebServer\\]' e2e.log | tail -n 40\n"),
			'a substitution in an echo': replace(webServer, '              echo "$(tail -n 40 e2e.log)"\n')
		};
		for (const [label, change] of Object.entries(e2eMore)) {
			const changed = change(workflow);
			if (changed === null || changed === workflow) problems.push(`ci.yml no longer has the text that ${label} changes: update the comment cases`);
			else if (e2eCommentQuotes(changed).length === 0) problems.push(`the comment check passes ${label}`);
		}
		for (const problem of failureCommentQuotes(workflow, 'backend')) problems.push(`ci.yml: the backend failure comment: ${problem}`);
		for (const problem of summaryCommentQuotes(workflow)) problems.push(`ci.yml: the test counts comment: ${problem}`);
		const summaryUnfiltered = replace("| tail -n 8 | cut -c1-300 | tr -d '\\140'", '| tail -n 8 | cut -c1-300')(workflow);
		if (summaryUnfiltered === null) problems.push('ci.yml no longer has the text the test counts comment case changes: update it');
		else if (summaryCommentQuotes(summaryUnfiltered).length === 0) problems.push('the test counts comment check passes a log quoted with its backticks');
		const backendQuotes = {
			'a failing test quoted with its backticks': replace("| sort -u | head -100 | cut -c1-300 | tr -d '\\140'\n", '| sort -u | head -100 | cut -c1-300\n'),
			'a restore error quoted with its backticks': replace("grep ' error ' restore.log | sort -u | head -40 | cut -c1-400 | tr -d '\\140'", "grep ' error ' restore.log | sort -u | head -40 | cut -c1-400")
		};
		for (const [label, change] of Object.entries(backendQuotes)) {
			const changed = change(workflow);
			if (changed === null || changed === workflow) problems.push(`ci.yml no longer has the text that ${label} changes: update the backend comment cases`);
			else if (failureCommentQuotes(changed, 'backend').length === 0) problems.push(`the backend comment check passes ${label}`);
		}
		// The other workflows' pull request comments (Trivy's tables, the registries' answers), each with its filter taken out.
		const workflows = readWorkflows();
		for (const problem of pullRequestCommentQuotes(workflows)) problems.push(problem);
		const unfiltered = {
			'images.yml': ["head -c 50000 | tr -d '\\140';", 'head -c 50000;'],
			'security-scan.yml': ["head -c 60000 trivy-report.txt | tr -d '\\140';", 'head -c 60000 trivy-report.txt;'],
			'base-images.yml': ["cat resolved.txt | tr -d '\\140'\n", 'cat resolved.txt\n']
		};
		for (const [file, [from, to]] of Object.entries(unfiltered)) {
			const changed = workflows.map(([f, text]) => [f, f === file ? text.replace(from, () => to) : text]);
			if (!workflows.find(([f]) => f === file)?.[1].includes(from)) problems.push(`${file} no longer has the text the comment case changes: update it`);
			else if (pullRequestCommentQuotes(changed).length === 0) problems.push(`the pull request comment check passes ${file}'s comment unfiltered`);
		}
		// ARV-271a review round 1: the variants that passed the round's own cases, each refused now (a # in a quoted string, a
		// gate under with:, a writer left in the background, LOGS_CHECKED set in the step, a fi closing the block early, a write by
		// a quoted path or an in-place edit; a second run without --log; an escaped pipe, a $'...' quote, a tee or a redirection
		// inside a filtered pipe, gh reading the run's log, a bare set, tr redefined, a pull request title in the environment, a
		// second comment by --body).
		const nodeLine = '          node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs checked\n';
		const uploadGate = "if: ${{ !cancelled() && steps.test-logs.outcome == 'success' }}\n        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1\n        with:\n";
		const gateAnchor = '          if [ "$LOGS_CHECKED" = success ]; then\n            for log in';
		const testLogsAnchor = '          token=$(openssl rand -hex 16)\n          for log in checked/unit.log';
		const backendRound2 = {
			'an exit after a # in a quoted string': replace(`${nodeLine}      # ARV-068`, `          echo "step #1"; exit 0\n${nodeLine}      # ARV-068`),
			'a gate under with:': replace(uploadGate, `if: always()\n        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1\n        with:\n          if: \${{ !cancelled() && steps.test-logs.outcome == 'success' }}\n`),
			'a writer left in the background by the check': replace(`${nodeLine}      # ARV-068`, `          ( sleep 30; cp unit.log checked/late.log ) &\n${nodeLine}      # ARV-068`),
			'LOGS_CHECKED set in the step': replace(gateAnchor, `          LOGS_CHECKED=success\n${gateAnchor}`),
			'a fi that closes the block early': replace(gateAnchor, '          if [ "$LOGS_CHECKED" = success ]; then\n            fi; tail -n 3 checked/unit.log >> test-summary.md\n          if true; then\n            for log in'),
			'a write into checked/ by a quoted path': replace(testLogsAnchor, `          printenv > "$GITHUB_WORKSPACE/checked/env.txt"\n${testLogsAnchor}`),
			'an in-place edit of a checked log': replace(testLogsAnchor, `          sed -i 's/x/y/' checked/unit.log\n${testLogsAnchor}`)
		};
		for (const [label, change] of Object.entries(backendRound2)) {
			const changed = change(workflow);
			if (changed === null || changed === workflow) problems.push(`ci.yml no longer has the text that ${label} changes: update the wiring cases`);
			else if (backendLogWiring(changed).length === 0) problems.push(`the wiring check passes ${label}`);
		}
		const mutationGate = "if: ${{ !cancelled() && steps.mutation-logs.outcome == 'success' }}\n        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1\n        with:\n";
		const mutationRound2 = {
			'a gate under with:': replace(mutationGate, `if: always()\n        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1\n        with:\n          if: \${{ !cancelled() && steps.mutation-logs.outcome == 'success' }}\n`),
			'a second run without --log': replace('      - name: Mutation log and report hold no secret (CWE-532)\n', '      - name: Again\n        working-directory: Platform/Backplane/Ariva.UnitTests\n        run: node "$GITHUB_WORKSPACE/scripts/mutation-run.mjs" -- dotnet-stryker\n      - name: Mutation log and report hold no secret (CWE-532)\n'),
			'an exit after a # in a quoted string': replace(`${nodeLine}      - name: Mutation report`, `          echo "step #1"; exit 0\n${nodeLine}      - name: Mutation report`)
		};
		for (const [label, change] of Object.entries(mutationRound2)) {
			const changed = change(mutation);
			if (changed === null || changed === mutation) problems.push(`mutation.yml no longer has the text that ${label} changes: update the wiring cases`);
			else if (mutationLogWiring(changed).length === 0) problems.push(`the mutation wiring check passes ${label}`);
		}
		const e2eStart = '          set +o pipefail # head closes pipes early on purpose here\n          # ARV-271: every quoted line';
		const e2ePost = '          gh pr comment "$PR" --repo "$GITHUB_REPOSITORY" --body-file failure.md\n      - name: Playwright report';
		const e2eRound2 = {
			'an escaped pipe': replace(webServer, "              if cat -- e2e.log \\| tr -d '\\140'; then true; fi\n"),
			"a $'...' quote": replace(webServer, "              echo $'\\''; cat e2e.log; echo $'\\''\n"),
			'a tee inside a filtered pipe': replace(webServer, "              grep x e2e.log | tee -a failure.md | tr -d '\\140'\n"),
			'a redirection before the filter': replace(webServer, "              cat e2e.log >> failure.md | tr -d '\\140'\n"),
			"gh reading the run's log": replace(webServer, "              gh run view \"$GITHUB_RUN_ID\" --log-failed | tr -d '\\140'\n"),
			'a bare set': replace(webServer, '              set\n'),
			'tr redefined before the group': replace(e2eStart, e2eStart.replace('\n', '\n          tr() { command cat; }\n')),
			'a pull request title in the environment': replace('          PR: ${{ github.event.pull_request.number }}\n        run: |\n' + e2eStart, '          PR: ${{ github.event.pull_request.number }}\n          TITLE: ${{ github.event.pull_request.title }}\n        run: |\n' + e2eStart),
			'a second comment by --body': replace(e2ePost, e2ePost.replace('\n', '\n          gh pr comment "$PR" --body "see the log"\n'))
		};
		for (const [label, change] of Object.entries(e2eRound2)) {
			const changed = change(workflow);
			if (changed === null || changed === workflow) problems.push(`ci.yml no longer has the text that ${label} changes: update the comment cases`);
			else if (e2eCommentQuotes(changed).length === 0) problems.push(`the comment check passes ${label}`);
		}
		// The pins: the workflows as they are match them; a one-character edit of a pinned job, an edit of a pinned step and a
		// comment step added outside them each fail.
		for (const problem of workflowPins(workflows).problems) problems.push(problem);
		const pinCases = {
			'a one-character edit of the backend job': ['ci.yml', 'timeout-minutes: 28\n', 'timeout-minutes: 29\n'],
			"an edit of the E2E comment's step": ['ci.yml', "| tail -n 15 | cut -c1-300 | tr -d '\\140'", "| tail -n 16 | cut -c1-300 | tr -d '\\140'"],
			'a comment step added to the web job': ['ci.yml', '      - name: npm audit, web, dev dependencies included (fails on high or critical)\n', '      - name: Comment\n        run: gh pr comment "$PR" --body-file x.md\n      - name: npm audit, web, dev dependencies included (fails on high or critical)\n'],
			'an edit of the mutation job': ['mutation.yml', '--concurrency 4', '--concurrency 5']
		};
		for (const [label, [file, from, to]] of Object.entries(pinCases)) {
			const original = workflows.find(([f]) => f === file)?.[1] ?? '';
			if (!original.includes(from)) problems.push(`${file} no longer has the text that the pin case ${label} changes: update it`);
			else if (workflowPins(workflows.map(([f, text]) => [f, f === file ? text.replace(from, () => to) : text])).problems.length === 0) problems.push(`the pins pass ${label}`);
		}
		const added = {
			'a comment through gh api in a new workflow': ['lint.yml', 'on: pull_request\njobs:\n  lint:\n    runs-on: ubuntu-24.04\n    steps:\n      - name: Lint comment\n        run: gh api repos/o/r/issues/1/comments -F body=@lint.log\n'],
			'a workflow whose jobs are not at two spaces': ['lint.yml', 'on: pull_request\njobs:\n    lint:\n        runs-on: ubuntu-24.04\n        steps:\n            - name: Lint\n              run: gh pr comment 1 --body-file lint.md\n'],
			'a comment action in a new workflow': ['lint.yml', 'on: pull_request\njobs:\n  lint:\n    runs-on: ubuntu-24.04\n    steps:\n      - uses: example/sticky-pull-request-comment@0123456789abcdef0123456789abcdef01234567\n']
		};
		for (const [label, entry] of Object.entries(added)) if (workflowPins([...workflows, entry]).problems.length === 0) problems.push(`the pins pass ${label}`);
		// ARV-271a review round 2: the pins hold whole jobs and their workflows' headers, and every job that can write to a pull
		// request; each of the round's bypasses fails (a same-named copy of a comment step, a PATH or environment change before
		// it, a workflow-level variable, a step between the E2E check and its comment, the self-test's own step removed, a job
		// that can write in a new workflow or one without permissions, a call to the API by URL, a local action in a pinned job).
		const scanStep = '      - name: Report findings on the pull request and fail on high or critical\n';
		const scan = workflows.find(([f]) => f === 'security-scan.yml')?.[1] ?? '';
		const scanCopy = scan.slice(scan.indexOf(scanStep), scan.indexOf('\n      - ', scan.indexOf(scanStep) + 1) + 1);
		const newWorkflow = (body) => ['lint.yml', `on: pull_request\n${body}`];
		const round3 = {
			'a same-named copy of a pinned comment step': ['security-scan.yml', scanStep, scanCopy + scanStep],
			'a PATH change before a pinned comment step': ['images.yml', scanStep, '      - name: Tools\n        run: echo "$RUNNER_TEMP/bin" >> "$GITHUB_PATH"\n' + scanStep],
			'an environment file before a pinned comment step': ['images.yml', scanStep, '      - name: Shell setup\n        run: echo "BASH_ENV=$RUNNER_TEMP/setup.sh" >> "$GITHUB_ENV"\n' + scanStep],
			'a workflow-level variable': ['mutation.yml', '\njobs:\n', '\nenv:\n  BASH_ENV: scripts/setup.sh\njobs:\n'],
			'a step between the E2E artifact check and its comment': ['ci.yml', '      - name: Report failures on the pull request\n        if: failure() && steps.artifacts', '      - name: More log\n        run: printenv >> e2e.log\n      - name: Report failures on the pull request\n        if: failure() && steps.artifacts'],
			"the self-test's own step removed": ['ci.yml', '      - name: E2E artifact secret check self-test\n        run: node Platform/Testing/Ariva.E2E/scripts/artifact-secrets.mjs --self-test\n', ''],
			'a local action in a pinned job': ['security-scan.yml', scanStep, '      - uses: ./.github/actions/post\n' + scanStep]
		};
		for (const [label, [file, from, to]] of Object.entries(round3)) {
			const original = workflows.find(([f]) => f === file)?.[1] ?? '';
			if (!original.includes(from) || from === to) problems.push(`${file} no longer has the text that the pin case ${label} changes: update it`);
			else if (workflowPins(workflows.map(([f, text]) => [f, f === file ? text.replace(from, () => to) : text])).problems.length === 0) problems.push(`the pins pass ${label}`);
		}
		const round3New = {
			'a job given pull-requests: write in a new workflow': newWorkflow('permissions:\n  contents: read\njobs:\n  lint:\n    runs-on: ubuntu-24.04\n    permissions:\n      pull-requests: write\n    steps:\n      - name: Lint\n        run: echo lint\n'),
			'a workflow without permissions': newWorkflow('jobs:\n  lint:\n    runs-on: ubuntu-24.04\n    steps:\n      - name: Lint\n        run: echo lint\n'),
			'a call to the API by URL from a job that cannot write': newWorkflow('permissions:\n  contents: read\njobs:\n  lint:\n    runs-on: ubuntu-24.04\n    steps:\n      - name: Lint\n        run: curl -X POST https://api.github.com/repos/o/r/issues/1/comments -d @lint.json\n')
		};
		for (const [label, entry] of Object.entries(round3New)) if (workflowPins([...workflows, entry]).problems.length === 0) problems.push(`the pins pass ${label}`);
		// A quoted string over several lines keeps a # in it as text, so the exit after it is read (review round 2, advice 7).
		const multiLine = replace(`${nodeLine}      # ARV-068`, `          echo "x\n          #"; exit 0; echo "\n          echo "\n${nodeLine}      # ARV-068`)(workflow);
		if (multiLine === null) problems.push('ci.yml no longer has the text the multi-line quote case changes: update it');
		else if (backendLogWiring(multiLine).length === 0) problems.push('the wiring check passes an exit hidden by a quote over several lines');
		// ARV-271a review round 3: the workflow is read failing closed, so the YAML spellings that hid a job that can write, or a
		// setting from the pinned header, each fail (jobs: with a comment, a job key with a comment, a job id starting with _, a
		// quoted job key, flow-style or quoted permissions, contents: write, a setting after jobs:, a job twice, an anchor, a
		// reusable workflow, steps not in a block), and a blank after a line's \ in a pinned job changes its hash.
		const postJob = '    runs-on: ubuntu-24.04\n    steps:\n      - name: Post\n        run: node scripts/post-summary.mjs\n';
		const readOnly = 'permissions:\n  contents: read\n';
		const writes = '    permissions:\n      pull-requests: write\n';
		const post = (text) => ['post.yml', `on: pull_request\n${text}`];
		const round4New = {
			'jobs: with a comment after it': post(`${readOnly}jobs: # the jobs\n  post:\n${writes}${postJob}`),
			'a job key with a comment after it': post(`${readOnly}jobs:\n  lint:\n${postJob}  post: # posts the summary\n${writes}${postJob}`),
			'a job id starting with _': post(`${readOnly}jobs:\n  lint:\n${postJob}  _post:\n${writes}${postJob}`),
			'a quoted job key': post(`${readOnly}jobs:\n  lint:\n${postJob}  "post":\n${writes}${postJob}`),
			'flow-style job permissions': post(`${readOnly}jobs:\n  post:\n    permissions: { pull-requests: write }\n${postJob}`),
			'flow-style workflow permissions': post(`permissions: { issues: write, contents: read }\njobs:\n  post:\n${postJob}`),
			'a quoted permission value': post(`${readOnly}jobs:\n  post:\n    permissions:\n      pull-requests: 'write'\n${postJob}`),
			'a quoted permission scope': post(`${readOnly}jobs:\n  post:\n    permissions:\n      "pull-requests": write\n${postJob}`),
			'contents: write': post(`${readOnly}jobs:\n  post:\n    permissions:\n      contents: write\n${postJob}`),
			'an anchor and an alias': post(`${readOnly}jobs:\n  lint:\n    runs-on: ubuntu-24.04\n    env: &shared\n      A: b\n    steps:\n      - name: Lint\n        run: echo lint\n  post:\n    env: *shared\n${postJob}`),
			'a reusable workflow called': post(`${readOnly}jobs:\n  post:\n    uses: ./.github/workflows/poster.yml\n`),
			'steps not in a block': post(`${readOnly}jobs:\n  post:\n    runs-on: ubuntu-24.04\n    steps: [{ run: echo }]\n`)
		};
		for (const [label, entry] of Object.entries(round4New)) if (workflowPins([...workflows, entry]).problems.length === 0) problems.push(`the pins pass ${label}`);
		const round4 = {
			'a setting after jobs: (env)': ['images.yml', null, '\nenv:\n  BASH_ENV: scripts/setup.sh\n'],
			'a setting after jobs: (defaults)': ['security-scan.yml', null, '\ndefaults:\n  run:\n    shell: bash --noprofile --norc {0}\n'],
			'a pinned job twice': ['base-images.yml', null, '\n  resolve:\n    runs-on: ubuntu-24.04\n    steps:\n      - name: Again\n        run: echo again\n'],
			"a blank after a line's \\ in a pinned job": ['images.yml', ' \\\n', ' \\ \n']
		};
		for (const [label, [file, from, to]] of Object.entries(round4)) {
			const original = workflows.find(([f]) => f === file)?.[1] ?? '';
			const changed = from === null ? original.replace(/\n*$/, '\n') + to : original.includes(from) ? original.replace(from, () => to) : null;
			if (changed === null) problems.push(`${file} no longer has the text that the pin case ${label} changes: update it`);
			else if (workflowPins(workflows.map(([f, text]) => [f, f === file ? changed : text])).problems.length === 0) problems.push(`the pins pass ${label}`);
		}
		// Review round 4: a job whose keys sit at another depth, or use a complex key, hid its permissions and steps.
		const deeper = (n) => post(`${readOnly}jobs:\n  post:\n${' '.repeat(n)}permissions:\n${' '.repeat(n + 2)}pull-requests: write\n${' '.repeat(n)}runs-on: ubuntu-24.04\n${' '.repeat(n)}steps:\n${' '.repeat(n + 2)}- name: Post\n${' '.repeat(n + 4)}run: node scripts/post-summary.mjs\n`);
		const round5New = {
			"a job's keys at three spaces": deeper(3),
			"a job's keys at five spaces": deeper(5),
			"a job's keys at six spaces": deeper(6),
			'a complex key for permissions': post(`${readOnly}jobs:\n  post:\n    ? permissions\n    : pull-requests: write\n${postJob}`),
			'steps at four spaces': post(`${readOnly}jobs:\n  post:\n${writes}    runs-on: ubuntu-24.04\n    steps:\n    - name: Post\n      run: node scripts/post-summary.mjs\n`)
		};
		for (const [label, entry] of Object.entries(round5New)) if (workflowPins([...workflows, entry]).problems.length === 0) problems.push(`the pins pass ${label}`);
		const bumped = workflows.map(([f, text]) => [f, text.replace(/upload-artifact@[0-9a-f]{40} # v7\.0\.1/g, 'upload-artifact@0123456789abcdef0123456789abcdef01234567 # v7.0.2')]);
		if (workflowPins(bumped).problems.length) problems.push("the pins fail on an action's pin bump (the bots' updates must pass)");
	} finally {
		fs.rmSync(dir, { recursive: true, force: true });
	}
	if (problems.length) {
		console.error(`artifact secret check self-test FAILED:\n${problems.join('\n')}`);
		return false;
	}
	console.log(`artifact secret check self-test: ${samples.length} samples of ${new Set(samples.map(([kind]) => kind)).size} kinds found in a plain file, a report's embedded zip, a trace zip, a nested zip and an error context; the clean set passes; an unreadable zip fails; no value printed; every derived value masked; redactSecrets clears ${REDACTION_SAMPLES.cleared.length} samples and keeps ${REDACTION_SAMPLES.kept.length}; a credential planted in a host log line fails the backend logs' check; CI's backend job checks its test logs before it prints, quotes or uploads them; the mutation workflow checks its log and report before it uploads them; the E2E and backend failure comments and the other workflows' pull request comments quote no backtick; each way to break that wiring is refused.`);
	return true;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
	const args = process.argv.slice(2);
	if (args.includes('--self-test')) process.exit(selfTest() ? 0 : 1);
	if (args.includes('--pins')) {
		const { hashes } = workflowPins(readWorkflows());
		for (const [key, hash] of Object.entries(hashes)) console.log(`\t'${key}': '${hash ?? 'NOT FOUND'}',`);
		process.exit(0);
	}
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
