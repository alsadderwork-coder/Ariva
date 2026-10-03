import fs from 'node:fs';
import path from 'node:path';
import { accounts, breakGlassFile, changedPassword, integrationSeedsFile } from './accounts';
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

export default async function globalTeardown() {
	const directory = process.env.ARIVA_E2E_LOG_DIR;
	if (!directory || !fs.existsSync(directory)) {
		throw new Error(`log scan: no host logs at ${directory}; the hosts must write LogFile__Path for this check`);
	}

	const files = fs.readdirSync(directory).filter((name) => name.endsWith('.log'));
	if (files.length === 0) throw new Error(`log scan: ${directory} has no .log files`);

	const secrets: string[] = [...Object.values(canary)];
	if (process.env.ARIVA_E2E_ACCOUNT_SEED) {
		secrets.push(...Object.values(accounts()).map((entry) => entry.password), changedPassword());
		secrets.push(...Object.values(accounts()).flatMap((entry) => (entry.totpSecret ? [entry.totpSecret] : [])));
	}
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

	const findings: string[] = [];
	let lines = 0;
	for (const file of files) {
		const content = fs.readFileSync(path.join(directory, file), 'utf8');
		content.split(/\r?\n/).forEach((line, index) => {
			if (!line) return;
			lines++;
			for (const value of secrets) {
				if (line.includes(value)) findings.push(`${file}:${index + 1} contains a canary credential or an E2E account password`);
			}
			for (const { name, re } of patterns) {
				if (re.test(line)) findings.push(`${file}:${index + 1} contains a ${name}`);
			}
		});
	}

	if (findings.length > 0) {
		throw new Error(`log scan: credentials reached the host logs (CWE-532):\n${findings.slice(0, 50).join('\n')}`);
	}
	console.log(`log scan: ${files.length} host logs, ${lines} lines, no credentials.`);
}
