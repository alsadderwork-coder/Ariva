import fs from 'node:fs';
import path from 'node:path';
import { canary } from './log-canary';

// ARV-007 (CWE-532): after the run, no host log line may contain a credential. The API suite sends the canary values
// in log-redaction.spec.ts; the generic patterns catch any other token that reached a log unredacted.
const patterns: { name: string; re: RegExp }[] = [
	// Token-like only (16+ characters with a digit or a dot), so prose such as "Bearer authentication" is not a finding.
	{ name: 'bearer credential', re: /\bBearer\s+(?!\[REDACTED\])(?=[A-Za-z0-9\-._~+/]*[0-9.])[A-Za-z0-9\-._~+/]{16,}/i },
	{ name: 'JWT', re: /\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*/ },
	{ name: 'access_token value', re: /access_token=(?!\[REDACTED\])[^&\s"\\]+/i }
];

export default async function globalTeardown() {
	const directory = process.env.ARIVA_E2E_LOG_DIR;
	if (!directory || !fs.existsSync(directory)) {
		throw new Error(`log scan: no host logs at ${directory}; the hosts must write LogFile__Path for this check`);
	}

	const files = fs.readdirSync(directory).filter((name) => name.endsWith('.log'));
	if (files.length === 0) throw new Error(`log scan: ${directory} has no .log files`);

	const findings: string[] = [];
	let lines = 0;
	for (const file of files) {
		const content = fs.readFileSync(path.join(directory, file), 'utf8');
		content.split(/\r?\n/).forEach((line, index) => {
			if (!line) return;
			lines++;
			for (const value of Object.values(canary)) {
				if (line.includes(value)) findings.push(`${file}:${index + 1} contains a canary credential`);
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
