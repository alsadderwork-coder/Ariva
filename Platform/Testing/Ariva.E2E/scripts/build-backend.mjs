// Builds the .NET hosts the end-to-end tests run, once, before Playwright starts them with `dotnet run --no-build`.
// Called by the first webServer entry in playwright.config.ts. Arguments are fixed; no shell is involved.
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const platform = path.resolve(here, '..', '..', '..');
const configuration = process.env.ARIVA_E2E_CONFIGURATION || 'Debug';

const projects = [
	'Backplane/Ariva.Api.Main/Ariva.Api.Main.csproj',
	'Backplane/Ariva.Api.Integration/Ariva.Api.Integration.csproj',
	'Backplane/Ariva.Api.Ingest/Ariva.Api.Ingest.csproj',
	'Simulation/Ariva.Simulation.Api/Ariva.Simulation.Api.csproj'
];

for (const project of projects) {
	const projectPath = path.join(platform, project);
	console.error(`build-backend: dotnet build ${project} (${configuration})`);
	// Build output goes to stderr, which Playwright shows for this web server; the host logs on stdout are ignored.
	const result = spawnSync(
		'dotnet',
		['build', projectPath, '--configuration', configuration, '--nologo', '--verbosity', 'quiet'],
		{ stdio: ['ignore', 2, 2], shell: false }
	);
	if (result.status !== 0) {
		console.error(`build-backend: build failed for ${project}`);
		process.exit(result.status ?? 1);
	}
}
