import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import type { FullConfig, FullResult, Reporter } from '@playwright/test/reporter';
import { secretValues, secretVariables, variable } from './target';

// ARV-128 (CWE-532): after the run, and after the HTML report is written (this reporter comes after it), the run's
// artifacts (the HTML report, with its embedded zip, and the output folder, with any trace zip or error context) must not
// hold a smoke credential. A finding fails the run and names the file and the variable, never the value; delete the
// folder before sharing anything from it. With `patterns`, any access token, device or display key, bearer credential or
// access_token value fails it too (the smoke config; the E2E run's other suites keep failure traces that may hold theirs).

interface Options {
	/** Folders (and files) to scan besides each project's output folder: the HTML report's, the JSON results. */
	folders?: string[];
	/** Also look for credential shapes, not only the run's smoke values. */
	patterns?: boolean;
}

const shapes: { name: string; re: RegExp }[] = [
	{ name: 'an access token (JWT)', re: /\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*/ },
	{ name: 'a device or display key', re: /(?<![A-Za-z0-9_-])ard[kp]_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])/ },
	{ name: 'a bearer credential', re: /\bBearer\s+(?=[A-Za-z0-9\-._~+/]*[0-9.])[A-Za-z0-9\-._~+/]{16,}/ },
	{ name: 'an access_token value', re: /access_token=(?!\[redacted\])[^&\s"\\]{8,}/i }
];

/** The texts of a zip's entries (stored or deflated), read from its central directory. */
function zipEntries(buffer: Buffer): string[] {
	const texts: string[] = [];
	let end = buffer.length - 22;
	while (end >= 0 && buffer.readUInt32LE(end) !== 0x06054b50) end--;
	if (end < 0) return texts;
	let at = buffer.readUInt32LE(end + 16);
	for (let count = buffer.readUInt16LE(end + 10); count > 0 && buffer.readUInt32LE(at) === 0x02014b50; count--) {
		const method = buffer.readUInt16LE(at + 10);
		const size = buffer.readUInt32LE(at + 20);
		const local = buffer.readUInt32LE(at + 42);
		const start = local + 30 + buffer.readUInt16LE(local + 26) + buffer.readUInt16LE(local + 28);
		const data = buffer.subarray(start, start + size);
		texts.push((method === 8 ? zlib.inflateRawSync(data) : data).toString('latin1'));
		at += 46 + buffer.readUInt16LE(at + 28) + buffer.readUInt16LE(at + 30) + buffer.readUInt16LE(at + 32);
	}
	return texts;
}

/** The texts to search in one file: the file itself and, for a zip or an HTML report, every embedded zip entry. */
function textsOf(buffer: Buffer): string[] {
	const text = buffer.toString('latin1');
	const zips = [...text.matchAll(/data:application\/zip;base64,([A-Za-z0-9+/=]+)/g)].map((match) => Buffer.from(match[1], 'base64'));
	if (buffer.length >= 4 && buffer.readUInt32LE(0) === 0x04034b50) zips.push(buffer);
	const texts = [text];
	for (const zip of zips) {
		try {
			texts.push(...zipEntries(zip));
		} catch {
			// What a zip that cannot be read holds cannot be shown to be clean.
			texts.push('\u0000unreadable zip\u0000');
		}
	}
	return texts;
}

/** Findings in the folders: "<file> holds <what>", never the value. */
export function scanArtifacts(folders: string[], patterns: boolean): string[] {
	const values = secretVariables
		.map((name) => ({ name, value: variable(name) }))
		.filter((entry): entry is { name: (typeof secretVariables)[number]; value: string } => entry.value !== undefined && entry.value.length >= 6);
	// Secrets also show URL-encoded and JSON-escaped in a report.
	const forms = values.flatMap(({ name, value }) => [...new Set([value, encodeURIComponent(value), JSON.stringify(value).slice(1, -1)])].map((form) => ({ name, form })));
	const findings: string[] = [];
	const scan = (file: string): void => {
		const texts = textsOf(fs.readFileSync(file));
		if (texts.some((text) => text.includes('\u0000unreadable zip\u0000'))) findings.push(`${file} holds a zip that cannot be read`);
		for (const { name, form } of forms) if (texts.some((text) => text.includes(form))) findings.push(`${file} holds the value of ${name}`);
		if (patterns) for (const { name, re } of shapes) if (texts.some((text) => re.test(text))) findings.push(`${file} holds ${name}`);
	};
	// A folder is searched through; a file (the JSON results) is searched itself.
	const walk = (folder: string): void => {
		if (!fs.existsSync(folder)) return;
		if (fs.statSync(folder).isFile()) return scan(folder);
		for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
			const file = path.join(folder, entry.name);
			if (entry.isDirectory()) walk(file);
			else if (entry.isFile()) scan(file);
		}
	};
	for (const folder of new Set(folders)) walk(folder);
	return [...new Set(findings)];
}

export default class ArtifactReporter implements Reporter {
	private folders: string[] = [];

	constructor(private readonly options: Options = {}) {}

	onBegin(config: FullConfig): void {
		this.folders = [...new Set([...(this.options.folders ?? []), ...config.projects.map((project) => project.outputDir)])];
	}

	printsToStdio(): boolean {
		return false;
	}

	async onEnd(result: FullResult): Promise<{ status?: FullResult['status'] } | undefined> {
		// Nothing to look for without a secret or a pattern.
		if (secretValues().length === 0 && !this.options.patterns) return undefined;
		const findings = scanArtifacts(this.folders, this.options.patterns === true);
		if (findings.length === 0) {
			console.log(`smoke artifacts: no credential in ${this.folders.length} folders.`);
			return undefined;
		}
		console.error(`smoke artifacts: credentials reached the run's artifacts (CWE-532); delete these files before sharing anything:\n${findings.slice(0, 50).join('\n')}`);
		return { status: result.status === 'interrupted' ? 'interrupted' : 'failed' };
	}
}
