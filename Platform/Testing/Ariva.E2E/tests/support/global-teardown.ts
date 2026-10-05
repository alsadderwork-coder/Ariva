import fs from 'node:fs';
import path from 'node:path';
import { accounts, breakGlassFile, changedPassword, integrationSeedsFile, webFirstPassword } from './accounts';
import { canary } from './log-canary';

// ARV-007 (CWE-532): after the run, no host log line may contain a credential. The API suite sends the canary values
// in log-redaction.spec.ts; the generic patterns catch any other token that reached a log unredacted (the JWT pattern
// covers every access token Ariva.Api.Main issued in the run). ARV-010a adds the E2E account passwords.
const patterns: { name: string; re: RegExp }[] = [
	// Token-like only (16+ characters with a digit or a dot), so prose such as "Bearer authentication" is not a finding.
	{ name: 'bearer credential', re: /\bBearer\s+(?!\[REDACTED\])(?=[A-Za-z0-9\-._~+/]*[0-9.])[A-Za-z0-9\-._~+/]{16,}/i },
	{ name: 'JWT', re: /\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*/ },
	{ name: 'access_token value', re: /access_token=(?!\[REDACTED\])[^&\s"\\]+/i },
	// ARV-042: an integration client secret (ics_ and 43 base64url characters) never reaches a log.
	{ name: 'integration client secret', re: /\bics_[A-Za-z0-9_-]{43}\b/ }
];

/**
 * Where a finding came from, without the value: the line's logger and message template (Serilog's JSON lines), so a
 * leak seen only in CI (where the host logs are not uploaded, since they may hold the secret) can be traced.
 */
function origin(line: string, values: string[]): string {
	try {
		const entry = JSON.parse(line) as { Level?: string; MessageTemplate?: string; Properties?: { SourceContext?: string } };
		// A template built by interpolation can hold the value itself: masked before it is printed.
		let template = entry.MessageTemplate ?? '';
		// Longer values first, so a secret that contains another leaves no fragment; then every credential pattern.
		for (const value of [...values].sort((x, y) => y.length - x.length)) template = template.split(value).join('[secret]');
		for (const { name, re } of patterns) template = template.replace(new RegExp(re.source, re.flags.includes('g') ? re.flags : re.flags + 'g'), `[${name}]`);
		template = template.replace(/eyJ[A-Za-z0-9_.-]+/g, '[jwt]').slice(0, 160);
		return ` (${entry.Level ?? '?'} ${entry.Properties?.SourceContext ?? 'no logger'}: "${template}")`;
	} catch {
		return ' (not a JSON log line)';
	}
}

/**
 * Whether the line holds the value. A value of digits only (the TOTP canary) counts only as a number of its own: as
 * part of a longer run of digits it is a coincidence, such as the fraction of a second in a log timestamp
 * ("06:32:43.4938178" holds 493817), not a code.
 */
export function holds(line: string, value: string): boolean {
	if (!/^\d+$/.test(value)) return line.includes(value);
	for (let at = line.indexOf(value); at !== -1; at = line.indexOf(value, at + 1)) {
		if (!/\d/.test(line[at - 1] ?? '') && !/\d/.test(line[at + value.length] ?? '')) return true;
	}
	return false;
}

export default async function globalTeardown() {
	// The matching rule itself, checked on every run (CWE-532): a code inside a longer run of digits is a coincidence, a
	// code anywhere else is a leak.
	const cases: [string, string, boolean][] = [
		['"Timestamp":"2026-10-05T06:32:43.4938178+04:00"', '493817', false],
		['{"code":"493817"}', '493817', true],
		['code=493817&next=1', '493817', true],
		['493817', '493817', true],
		['x4938170 and 493817', '493817', true],
		['password: canary-Pass word', 'canary-Pass word', true]
	];
	for (const [line, value, expected] of cases) {
		if (holds(line, value) !== expected) throw new Error(`log scan self-check: holds(${JSON.stringify(line)}) should be ${expected}`);
	}

	const directory = process.env.ARIVA_E2E_LOG_DIR;
	if (!directory || !fs.existsSync(directory)) {
		throw new Error(`log scan: no host logs at ${directory}; the hosts must write LogFile__Path for this check`);
	}

	const files = fs.readdirSync(directory).filter((name) => name.endsWith('.log'));
	if (files.length === 0) throw new Error(`log scan: ${directory} has no .log files`);

	// Each secret with a label, so a finding says which credential leaked (never the value itself).
	const labelled: { label: string; value: string }[] = Object.entries(canary).map(([key, value]) => ({ label: `canary ${key}`, value }));
	if (process.env.ARIVA_E2E_ACCOUNT_SEED) {
		for (const entry of Object.values(accounts())) {
			labelled.push({ label: `password of ${entry.userName}`, value: entry.password });
			if (entry.totpSecret) labelled.push({ label: `TOTP secret of ${entry.userName}`, value: entry.totpSecret });
		}
		labelled.push({ label: 'changed password', value: changedPassword() }, { label: 'first-sign-in password', value: webFirstPassword() });
	}
	const secrets: string[] = [];
	// The simulator's operator key and the mock partners' secrets (ARV-027 to ARV-029) never reach Ariva's host logs either.
	for (const name of ['ARIVA_E2E_SIMULATION_KEY', 'ARIVA_E2E_MOCK_AMAN_SECRET', 'ARIVA_E2E_MOCK_AMAN_SEED', 'ARIVA_E2E_MOCK_AMAN_PULL_SECRET', 'ARIVA_E2E_MOCK_AMAN_PULL_SEED', 'ARIVA_E2E_ACRIS_KEY']) {
		if (process.env[name]) secrets.push(process.env[name]!);
	}
	// The break-glass credential printed by the installer command (ARV-010c) must never reach a host log either.
	if (fs.existsSync(breakGlassFile)) {
		const lines = fs.readFileSync(breakGlassFile, 'utf8').split(/\r?\n/);
		secrets.push(...lines.filter((line) => line.startsWith('password: ')).map((line) => line.slice('password: '.length).trim()));
		secrets.push(...lines.filter((line) => line.startsWith('  ')).map((line) => line.trim()));
	}

	// The TOTP seeds of the integration clients registered in the run (integration-auth.spec.ts).
	if (fs.existsSync(integrationSeedsFile)) {
		secrets.push(...fs.readFileSync(integrationSeedsFile, 'utf8').split(/\r?\n/).map((line) => line.trim()).filter((line) => line.length >= 16));
	}

	const all = [...labelled, ...secrets.filter(Boolean).map((value) => ({ label: 'a configured secret', value }))];
	const values = all.map((entry) => entry.value).filter(Boolean);
	const findings: string[] = [];
	let lines = 0;
	for (const file of files) {
		const content = fs.readFileSync(path.join(directory, file), 'utf8');
		content.split(/\r?\n/).forEach((line, index) => {
			if (!line) return;
			lines++;
			for (const { label, value } of all) {
				if (value && holds(line, value)) findings.push(`${file}:${index + 1} contains ${label}${origin(line, values)}`);
			}
			for (const { name, re } of patterns) {
				if (re.test(line)) findings.push(`${file}:${index + 1} contains a ${name}${origin(line, values)}`);
			}
		});
	}

	if (findings.length > 0) {
		throw new Error(`log scan: credentials reached the host logs (CWE-532):\n${findings.slice(0, 50).join('\n')}`);
	}
	console.log(`log scan: ${files.length} host logs, ${lines} lines, no credentials.`);
}
