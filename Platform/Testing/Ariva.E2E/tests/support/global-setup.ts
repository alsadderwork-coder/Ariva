import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { breakGlassFile, databaseAvailable, integrationSeedsFile } from './accounts';
import { refuseADatabaseWithTheDevelopmentOnlySite } from './development-site';

// ARV-010c: the break-glass account exists only through the installer command, so the run creates it the way an
// installer does (Ariva.Api.Main --create-break-glass) once the hosts are up, writing the credential to a git-ignored
// file the break-glass test reads. When it already exists (a reused local database), the credential is rotated.

const here = path.dirname(fileURLToPath(import.meta.url));
const mainProject = path.resolve(here, '..', '..', '..', '..', 'Backplane', 'Ariva.Api.Main');

export default async function globalSetup() {
	if (!databaseAvailable) return;
	// ARV-139c (CWE-200): a database that holds the development-only site, or that this check cannot match to the hosts',
	// stops the run before any test (development-site.ts).
	await refuseADatabaseWithTheDevelopmentOnlySite();

	fs.mkdirSync(path.dirname(breakGlassFile), { recursive: true });
	// ARV-042: the run's integration client seeds, collected afresh for the log scan.
	fs.rmSync(integrationSeedsFile, { force: true });
	for (const flag of ['--create-break-glass', '--rotate-break-glass']) {
		fs.rmSync(breakGlassFile, { force: true });
		const result = spawnSync(
			'dotnet',
			['run', '--no-build', '--configuration', process.env.ARIVA_E2E_CONFIGURATION || 'Debug', '--project', mainProject, '--', flag, `--break-glass-output=${breakGlassFile}`],
			{ encoding: 'utf8', env: process.env, timeout: 120_000 }
		);
		if (result.status === 0 && fs.existsSync(breakGlassFile)) return;
		if (flag === '--rotate-break-glass') {
			throw new Error(`break-glass setup failed:\n${result.stdout}\n${result.stderr}`);
		}
	}
}

/** The credential the installer command wrote: username, password and recovery codes. */
export function readBreakGlass(): { userName: string; password: string; recoveryCodes: string[] } {
	const lines = fs.readFileSync(breakGlassFile, 'utf8').split(/\r?\n/);
	const value = (key: string) => lines.find((line) => line.startsWith(`${key}: `))!.slice(key.length + 2).trim();
	return { userName: value('username'), password: value('password'), recoveryCodes: lines.filter((line) => line.startsWith('  ')).map((line) => line.trim()) };
}
