// ARV-186 (CWE-1395; the ARV-184 review's item 3): the NuGet audit reads `dotnet list package --vulnerable --format json`, not
// the table's severity words. It fails on any advisory that is not Low or Moderate (High, Critical, or a severity it does not know), and when the answer cannot be trusted to be complete:
// output that is not the JSON report, a report that read no source (the vulnerability data comes from the sources), or a
// project that reports a problem (a source that could not be reached, for instance). Moderate and Low advisories are listed
// and do not fail the gate. scripts/verify.mjs runs this self-test before every audit, so a change that breaks a rule fails there.
//   node scripts/security/nuget-audit.mjs --self-test
import { fileURLToPath } from 'node:url';

/** The severities that do not fail the gate; any other (High, Critical, a misspelt or unknown one, none) does. */
const MILD = new Set(['low', 'moderate']);

/**
 * The audit's verdict on one `dotnet list package --vulnerable --format json` output: severe (High or Critical) and other
 * advisories as `<project> <framework> <package> <version> <severity> <advisory url>` lines, and problems that make the
 * report untrustworthy. Package and project names come from the report and are printed as lines of their own only.
 */
export function nugetAuditFindings(text) {
	const severe = [];
	const other = [];
	const problems = [];
	let report;
	try {
		report = JSON.parse(text);
	} catch {
		return { severe, other, problems: ['the output is not the JSON report (dotnet list package --vulnerable --format json)'] };
	}
	if (report?.version !== 1) problems.push(`the report is not format version 1 (found ${JSON.stringify(report?.version)})`);
	if (!Array.isArray(report?.sources) || report.sources.length === 0) problems.push('the report read no package source, so no vulnerability data was read');
	if (!Array.isArray(report?.projects) || report.projects.length === 0) problems.push('the report lists no project');
	for (const problem of report?.problems ?? []) problems.push(`the report has a problem: ${String(problem?.text ?? problem).slice(0, 300)}`);
	for (const project of Array.isArray(report?.projects) ? report.projects : []) {
		const where = String(project?.path ?? 'a project').split(/[\\/]/).pop();
		for (const problem of project?.problems ?? []) problems.push(`${where} has a problem: ${String(problem?.text ?? problem).slice(0, 300)}`);
		for (const framework of project?.frameworks ?? []) {
			for (const pkg of [...(framework?.topLevelPackages ?? []), ...(framework?.transitivePackages ?? [])]) {
				for (const vulnerability of pkg?.vulnerabilities ?? []) {
					const severity = String(vulnerability?.severity ?? 'unknown').trim();
					const line = `${where} ${framework?.framework ?? '?'} ${pkg?.id ?? '?'} ${pkg?.resolvedVersion ?? '?'} ${severity} ${vulnerability?.advisoryurl ?? ''}`.trim();
					// ARV-186 review: only Low and Moderate pass; an advisory whose severity is missing, misspelt or new is severe, since
					// the gate cannot tell it is not.
					(MILD.has(severity.toLowerCase()) ? other : severe).push(line);
				}
			}
		}
	}
	return { severe, other, problems };
}

/** The rules on recorded reports: each case gives exactly its severe advisories and problems. */
export function selfTest() {
	const project = (vulnerabilities, extra = {}) => ({
		path: '/src/Platform/Backplane/Ariva.Api.Main/Ariva.Api.Main.csproj',
		frameworks: [{ framework: 'net10.0', topLevelPackages: [], transitivePackages: [{ id: 'Some.Package', resolvedVersion: '1.2.3', vulnerabilities }] }],
		...extra
	});
	const report = (projects, extra = {}) => JSON.stringify({ version: 1, parameters: '--vulnerable --include-transitive', sources: ['https://api.nuget.org/v3/index.json'], projects, ...extra });
	const advisory = (severity) => ({ severity, advisoryurl: 'https://github.com/advisories/GHSA-0000-0000-0000' });
	const cases = [
		['clean', report([{ path: '/src/a.csproj' }]), 0, 0, 0],
		['high', report([project([advisory('High')])]), 1, 0, 0],
		['critical', report([project([advisory('Critical')])]), 1, 0, 0],
		['moderate and low only', report([project([advisory('Moderate'), advisory('Low')])]), 0, 2, 0],
		['no severity', report([project([{ advisoryurl: 'https://github.com/advisories/GHSA-x' }])]), 1, 0, 0],
		['a severity it does not know', report([project([advisory('Important')])]), 1, 0, 0],
		['a severity in other case and padded', report([project([advisory(' HIGH '), advisory(' moderate ')])]), 1, 1, 0],
		['a severity that is not text', report([project([advisory(3)])]), 1, 0, 0],
		['no source read', report([{ path: '/src/a.csproj' }], { sources: [] }), 0, 0, 1],
		['the table, not JSON', 'Project `Ariva.Api.Main` has the following vulnerable packages\n   > Some.Package   1.2.3   High', 0, 0, 1],
		['a project that could not be checked', report([project([], { problems: [{ level: 'error', text: 'Unable to load the service index for source https://api.nuget.org/v3/index.json.' }] })]), 0, 0, 1],
		['a problem of the report', report([{ path: '/src/a.csproj' }], { problems: [{ level: 'warning', text: 'Vulnerability data could not be read.' }] }), 0, 0, 1],
		['no project', report([]), 0, 0, 1]
	];
	const failures = [];
	for (const [name, text, severe, other, problems] of cases) {
		const found = nugetAuditFindings(text);
		if (found.severe.length !== severe || found.other.length !== other || found.problems.length !== problems)
			failures.push(`${name}: expected ${severe} severe, ${other} other and ${problems} problems; got ${found.severe.length}, ${found.other.length} and ${found.problems.length}`);
	}
	return { count: cases.length, failures };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1] && process.argv.includes('--self-test')) {
	const { count, failures } = selfTest();
	if (failures.length) {
		console.error(`nuget audit self-test failed:\n  ${failures.join('\n  ')}`);
		process.exit(1);
	}
	console.log(`nuget audit self-test: ${count} recorded reports each give exactly their advisories and problems`);
}
