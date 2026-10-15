import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// ARV-128: Playwright runs in a child process, for the smoke guard's tests (api/smoke-guard.spec.ts) and the deployed
// smoke config's run against this stack (api/smoke-config.spec.ts). Each child has its own output, report and JSON
// results in a folder of test-results-smoke-runs (git-ignored), removed afterwards, so it never touches this run's
// test-results or report.

const e2e = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

export interface ChildRun {
	status: number | null;
	output: string;
	passed: number;
	failed: number;
	skipped: number;
}

/** This process's environment without its Playwright worker variables, its smoke values and colours, plus the given ones. */
function childEnvironment(extra: Record<string, string>): NodeJS.ProcessEnv {
	const env: NodeJS.ProcessEnv = {};
	for (const [name, value] of Object.entries(process.env)) {
		if (/^(TEST_|PW_|PWTEST_|PLAYWRIGHT_(HTML|JSON|BLOB)_|ARIVA_SMOKE_|FORCE_COLOR$)/.test(name)) continue;
		env[name] = value;
	}
	return { ...env, NO_COLOR: '1', ...extra };
}

export function playwrightChild(config: string, extra: Record<string, string>, args: string[] = [], timeout = 180_000): ChildRun {
	// Inside the project's git-ignored smoke runs folder: the smoke config refuses report and output folders anywhere else.
	const root = path.join(e2e, 'test-results-smoke-runs');
	fs.mkdirSync(root, { recursive: true });
	const folder = fs.mkdtempSync(path.join(root, 'run-'));
	try {
		const json = path.join(folder, 'results.json');
		// Node and Playwright's own CLI script, with no shell between (the same on Windows, where npx is a .cmd shim).
		const cli = path.join(e2e, 'node_modules', '@playwright', 'test', 'cli.js');
		const result = spawnSync(process.execPath, [cli, 'test', `--config=${config}`, `--output=${path.join(folder, 'output')}`, ...args], {
			cwd: e2e,
			env: childEnvironment({ ARIVA_SMOKE_REPORT_DIR: path.join(folder, 'report'), ARIVA_SMOKE_JSON_FILE: json, ...extra }),
			encoding: 'utf8',
			timeout
		});
		const stats = fs.existsSync(json) ? (JSON.parse(fs.readFileSync(json, 'utf8')).stats as { expected: number; unexpected: number; skipped: number }) : undefined;
		return { status: result.status, output: `${result.stdout}\n${result.stderr}`, passed: stats?.expected ?? 0, failed: stats?.unexpected ?? 0, skipped: stats?.skipped ?? 0 };
	} finally {
		fs.rmSync(folder, { recursive: true, force: true });
	}
}
