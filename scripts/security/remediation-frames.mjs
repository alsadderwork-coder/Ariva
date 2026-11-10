// ARV-096a (ASVS 5.0 V15.2.1; wiki 13 section 9): the remediation time frames, enforced on the dependency and image findings.
// It reads the NuGet audit (dotnet list package --vulnerable --include-transitive --format json), the npm audits (npm audit
// --json, report version 2) and Trivy's JSON results (schema version 2), takes each finding's age from its advisory (the GitHub
// Advisory Database's publication date for a GHSA advisory, Trivy's PublishedDate for its own; the date a fix was published is
// not recorded by either, so the publication date stands for it, which can only count a finding older, never younger; a
// publication date after the day of the run leaves the age unknown), and fails when an open finding with a fix is older than its
// severity's frame (security/remediation-frames.json; only low may be null, no age frame: by the next planned release).
// Whether a finding has a fix: Trivy's FixedVersion. npm's fixAvailable, the narrower test: npm reports no fix when the patched
// version is out of the tree's reach (a parent's range blocks it), so such a finding reads as no fix yet and does not fail.
// NuGet's report names no fixed version: a fix exists when the GitHub advisory names a first patched version for that NuGet
// package (ecosystem nuget, the same id in any case), and is unknown when the advisory was not read or does not name it.
// A finding without a fix is listed in the summary and never fails. A finding may stay open past its frame only under an
// exception in security/remediation-exceptions.json (the finding, the package, the reason, the compensating control, who
// proposed it, the owner's approval and an expiry); an expired exception, or one whose approvedBy is empty or PENDING, excuses
// nothing and fails the step once the frames are approved.
// No agent ever approves a frame or an exception: approvedBy is the owner's (as for security/allowlist.json). The code refuses a
// malformed approval and fails the step on it in either mode: approvedBy not a string or not one of OWNERS; approvedOn missing,
// not a real day or after the day of the run; for an exception, approved by its proposer, or expiring before approvedOn or more
// than MAX_EXCEPTION_DAYS after it. It cannot tell a well-formed approval naming the owner from the owner's own, so it compares
// both files with references (approvalDrift; --frames-at and --exceptions-at): HEAD, so that an uncommitted edit is caught, and
// the newest of main and origin/main that is an ancestor of HEAD, or an explicit reference (approvalReferences chooses them;
// scripts/verify.mjs gathers the facts). Drift from any reference fails the step in either mode: any change to frames it holds
// approved, and an approved exception it does not hold unchanged (a reference without the exceptions file counts as an empty
// list). Left to review and to the hook proposed in docs/product/decisions.md: approving the frames while every reference holds
// them PENDING (which can only make the gate stricter than its report, though with frames of the approver's choosing), and
// anything committed on a reference itself.
// While the frames are not approved the step only reports: it prints what would fail and passes. Once they are, it fails
// closed: a finding whose age, fix or severity cannot be read fails it, and so does an input not read: one named by --missing,
// one that cannot be parsed, and one that is not its tool's complete report (a NuGet report not of version 1, without sources
// or projects, or with a problem in the report or in a project, such as a source it could not reach; an npm report not of
// version 2, without its vulnerabilities object or with an error; a Trivy report not of schema version 2 or without its Results
// list, which Trivy also leaves out when it found nothing to scan).
//   node scripts/security/remediation-frames.mjs [--nuget <file>] [--npm <name>=<file> ...] [--trivy <file> ...]
//        [--missing <name> ...] [--advisories <file>] [--offline] [--today YYYY-MM-DD] [--summary <file>]
//        [--frames-at <reference>=<file> ...] [--exceptions-at <reference>=<file> ...] [--note <text> ...]
//   node scripts/security/remediation-frames.mjs --self-test
// --advisories names a JSON object of recorded advisories, each { "GHSA-...": { "published": "YYYY-MM-DD", "patched":
// { "<NuGet package id in lower case>": true } } } (whether the advisory names a patched version of that NuGet package);
// otherwise each GHSA advisory is read from https://api.github.com/advisories/<id> (advisoryUrl: a full GHSA id and nothing
// else; GITHUB_TOKEN, when set, is sent and never printed; a redirect is refused). A date that cannot be read leaves the
// finding's age unknown: listed while the frames are Proposed, a failure once they are approved.
// --frames-at and --exceptions-at name a reference's copy of each file, labelled with the reference (for example "HEAD at
// 1a2b3c4d (2026-11-10)"); a copy that is named but cannot be read fails the step. --note adds a line to the summary (the
// reference choice's warnings). The summary escapes what the audits report (Markdown, HTML, the pipelines' logging command
// prefixes, and every character SEC-130 refuses as hidden, with tabs and line breaks), and names the inputs it read and the
// references it compared the approvals with.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { HIDDEN_CHARS } from './scan.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const SEVERITIES = ['critical', 'high', 'moderate', 'low'];
const DAY = 24 * 60 * 60 * 1000;
const GHSA = /^GHSA(-[23456789cfghjmpqrvwx]{4}){3}$/;
const ID = /^(GHSA(-[23456789cfghjmpqrvwx]{4}){3}|CVE-\d{4}-\d{4,7})$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
/**
 * What the summary and the log never pass on, each made a space: every character SEC-130 refuses as hidden (HIDDEN_CHARS in
 * scripts/security/scan.mjs: the C0 and C1 controls, DEL, the Arabic letter mark, the zero-width, direction and bidirectional
 * marks, embeddings, overrides and isolates, the separators, the tag characters, variation selectors, fillers and the
 * noncharacters among them), and the tab and line feed it allows in files.
 */
const CONTROLS = new RegExp(`[${[[0x09, 0x0A], ...HIDDEN_CHARS].map(([from, to = from]) => (from === to ? `\\u{${from.toString(16)}}` : `\\u{${from.toString(16)}}-\\u{${to.toString(16)}}`)).join('')}]`, 'gu');
/** Who may approve the frames and an exception: the owner (CLAUDE.md rule 1, as for security/allowlist.json). */
export const OWNERS = ['Ahmad Al-Sadder'];
/** The longest an exception may run from its approval. */
export const MAX_EXCEPTION_DAYS = 90;
const daysBetween = (from, to) => Math.floor((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / DAY);
const isRecord = (value) => typeof value === 'object' && value !== null && !Array.isArray(value);

/** A real calendar day written YYYY-MM-DD: it must read back as itself, so 2026-02-30 (which Date.parse rolls over) is none. */
export function isDate(value) {
	if (typeof value !== 'string' || !DATE.test(value)) return false;
	const ms = Date.parse(`${value}T00:00:00Z`);
	return !Number.isNaN(ms) && new Date(ms).toISOString().slice(0, 10) === value;
}

/** Text an audit reported, safe in a Markdown table and a pipeline log: no hidden character, table or line break, HTML, link, code or logging command. */
export const safe = (value, length = 120) => String(value ?? '').replace(CONTROLS, ' ').replace(/\|/g, ' ').replace(/[<>[\]`\\]/g, (c) => `\\${c}`)
	.replace(/#{2,}/g, (run) => run.split('').join(' ')).replace(/:{2,}/g, (run) => run.split('').join(' ')).slice(0, length);
/** Text for the log alone (stderr): no hidden character or logging command, and no Markdown escaping, so text made safe() before is not escaped twice. */
const logSafe = (value, length = 300) => String(value ?? '').replace(CONTROLS, ' ')
	.replace(/#{2,}/g, (run) => run.split('').join(' ')).replace(/:{2,}/g, (run) => run.split('').join(' ')).slice(0, length);

/** A severity word of any of the three tools as one of SEVERITIES (Trivy's MEDIUM is moderate, npm's info is low), or unknown. */
export function severityOf(word) {
	const value = String(word ?? '').toLowerCase();
	if (value === 'medium') return 'moderate';
	if (value === 'info') return 'low';
	return SEVERITIES.includes(value) ? value : 'unknown';
}

/** The advisory id in an advisory URL or a Trivy id (GHSA-... or CVE-...), or null. */
function idOf(text) {
	const match = String(text ?? '').match(/(GHSA(?:-[23456789cfghjmpqrvwx]{4}){3}|CVE-\d{4}-\d{4,7})/);
	return match ? match[1] : null;
}

/** Whether a report's problems list holds anything (a value there that is not a list counts as problems). */
const hasProblems = (value) => value !== undefined && (!Array.isArray(value) || value.length > 0);

/**
 * Why one NuGet audit report is not complete, or nothing: it must be report version 1 with the sources it asked and the projects
 * it read, and hold no problem, in the report or in a project. A source it could not reach is such a problem ("Unable to load
 * the service index ..."): the report then holds no vulnerability data, which must not read as nothing found. The reasons hold
 * the report's own text (a project's path): a caller makes them safe for where it prints them.
 */
export function nugetReportProblems(report) {
	if (!isRecord(report)) return ['not a JSON object'];
	const problems = [];
	if (report.version !== 1) problems.push('not report version 1');
	if (!Array.isArray(report.sources) || report.sources.length === 0) problems.push('no sources');
	if (!Array.isArray(report.projects) || report.projects.length === 0) problems.push('no projects');
	if (hasProblems(report.problems)) problems.push('problems in the report');
	for (const project of Array.isArray(report.projects) ? report.projects : []) {
		if (!isRecord(project)) problems.push('a project that is not an object');
		else if (hasProblems(project.problems)) problems.push(`problems in ${String(project.path ?? 'a project').slice(0, 200)}`);
	}
	return problems;
}

/**
 * The findings of one NuGet audit report: package, version, advisory id and severity. It throws, so the input counts as not
 * read, unless the report is complete (nugetReportProblems). dotnet list gives no fixed version; whether the advisory names a
 * patched version of the package comes from the advisory (advisoryRecord), unknown otherwise.
 */
export function nugetFindings(text, source = 'nuget') {
	const report = JSON.parse(text);
	const problems = nugetReportProblems(report);
	if (problems.length) throw new Error(`${source}: not a complete NuGet audit report (${problems.join('; ')})`);
	const findings = [];
	for (const project of report.projects) {
		for (const framework of project.frameworks ?? []) {
			for (const pkg of [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])]) {
				for (const vulnerability of pkg.vulnerabilities ?? []) {
					findings.push({ source, package: pkg.id, version: pkg.resolvedVersion, id: idOf(vulnerability.advisoryurl), severity: severityOf(vulnerability.severity), fix: 'unknown', published: null });
				}
			}
		}
	}
	return findings;
}

/**
 * The findings of one npm audit report: each advisory a package's via names, with npm's fixAvailable (a patched version the tree
 * cannot reach reads as no fix). It throws, so the input counts as not read, unless the report is of version 2 with its
 * vulnerabilities object and no error (npm writes one when it cannot reach the registry or read the lock file).
 */
export function npmFindings(text, source = 'npm') {
	const report = JSON.parse(text);
	if (!isRecord(report) || report.auditReportVersion !== 2 || !isRecord(report.vulnerabilities) || Object.hasOwn(report, 'error')) {
		throw new Error(`${source}: not a complete npm audit report of version 2`);
	}
	const findings = [];
	for (const vulnerability of Object.values(report.vulnerabilities)) {
		for (const via of vulnerability.via ?? []) {
			if (typeof via !== 'object' || via === null) continue; // a dependency through which another package's advisory arrives
			findings.push({ source, package: via.name ?? vulnerability.name, version: via.range ?? '', id: idOf(via.url), severity: severityOf(via.severity),
				fix: vulnerability.fixAvailable ? 'yes' : 'no', published: null });
		}
	}
	return findings;
}

/**
 * The findings of one Trivy JSON report: its id, package, installed version, severity, whether a fixed version exists and its
 * publication date (a real day, or none). It throws, so the input counts as not read, unless the report is of schema version 2
 * with its Results list.
 */
export function trivyFindings(text, source = 'trivy') {
	const report = JSON.parse(text);
	if (!isRecord(report) || report.SchemaVersion !== 2 || !Array.isArray(report.Results)) throw new Error(`${source}: not a Trivy JSON report of schema version 2 with its Results list`);
	const findings = [];
	for (const result of report.Results) {
		for (const vulnerability of result.Vulnerabilities ?? []) {
			const published = String(vulnerability.PublishedDate ?? '').slice(0, 10);
			findings.push({ source, package: vulnerability.PkgName, version: vulnerability.InstalledVersion, id: idOf(vulnerability.VulnerabilityID), severity: severityOf(vulnerability.Severity),
				fix: vulnerability.FixedVersion ? 'yes' : 'no', published: isDate(published) ? published : null });
		}
	}
	return findings;
}

/**
 * Each input read by its tool's reader ({ name, read }, read returning the findings). One that cannot be read or parsed, or is
 * not its tool's complete report, is one not read, with the reason; missing lists those not read before (--missing).
 */
export function readInputs(list, missing = []) {
	const findings = [];
	const inputs = [];
	const unread = [...missing];
	const reasons = [];
	for (const { name, read } of list) {
		try {
			const found = read();
			findings.push(...found);
			inputs.push(`${name} (${found.length} findings)`);
		} catch (error) {
			const message = String(error?.message ?? error);
			unread.push(name);
			reasons.push(message.startsWith(`${name}: `) ? message : `${name}: ${message}`);
		}
	}
	return { findings, inputs, missing: unread, reasons };
}

/**
 * The frames file: approved or not, each severity's days (null, low only: no age frame, the next planned release), and problems
 * that fail the step in either mode (a bad frame; an approvedBy that is not a string; an approval by someone not in OWNERS, or
 * without its date, with one that is not a real day, or with one in the future).
 */
export function readFrames(config, today) {
	const problems = [];
	const frames = {};
	for (const severity of SEVERITIES) {
		const days = config?.frames?.[severity];
		if (days === null && severity === 'low') frames[severity] = null;
		else if (Number.isInteger(days) && days >= 1 && days <= 365) frames[severity] = days;
		else if (severity === 'low') problems.push('frames.low must be a whole number of days from 1 to 365, or null (the next planned release)');
		else problems.push(`frames.${severity} must be a whole number of days from 1 to 365 (only low may be null)`);
	}
	if (typeof config?.approvedBy !== 'string') {
		problems.push('the frames\' approvedBy must be a string (PENDING until the owner approves them)');
		return { frames, approved: false, problems };
	}
	const approvedBy = config.approvedBy.trim();
	const pending = approvedBy === '' || /^PENDING/i.test(approvedBy);
	if (pending) return { frames, approved: false, problems };
	const approvedOn = config.approvedOn;
	if (!OWNERS.includes(approvedBy)) problems.push(`the frames' approvedBy (${safe(approvedBy, 40)}) is not the owner: only ${OWNERS.join(', ')} approves them`);
	else if (!isDate(approvedOn)) problems.push('the frames are approved without approvedOn (a real day, YYYY-MM-DD)');
	else if (approvedOn > today) problems.push(`the frames' approvedOn ${approvedOn} is in the future`);
	return { frames, approved: problems.length === 0 && OWNERS.includes(approvedBy), problems };
}

/**
 * The exceptions file's entries, each checked. Malformed approvals (by someone not in OWNERS, by the proposer, without approvedOn
 * or with one that is not a real day or is in the future, expiring before approvedOn or past MAX_EXCEPTION_DAYS after it) fail the
 * step in either mode; a pending, expired or incomplete entry excuses nothing and fails the step once the frames are approved.
 */
export function readExceptions(config, today) {
	const entries = [];
	const problems = [];
	const forged = [];
	const list = config?.exceptions;
	if (!Array.isArray(list)) return { entries, problems, forged: ['the exceptions file must hold an exceptions list'] };
	for (const [index, entry] of list.entries()) {
		const where = `exception ${index + 1} (${safe(entry?.finding ?? '?', 40)})`;
		const missing = ['finding', 'package', 'reason', 'compensatingControl', 'proposedBy', 'approvedBy', 'expires'].filter((key) => typeof entry?.[key] !== 'string' || entry[key].trim() === '');
		if (missing.length) { problems.push(`${where} lacks ${missing.join(', ')}`); continue; }
		if (!ID.test(entry.finding)) { problems.push(`${where}: finding must be a GHSA or CVE id`); continue; }
		if (!isDate(entry.expires)) { problems.push(`${where}: expires must be a real day (YYYY-MM-DD)`); continue; }
		const approvedBy = entry.approvedBy.trim();
		if (/^PENDING/i.test(approvedBy)) {
			problems.push(`${where} is not approved by the owner (approvedBy ${safe(approvedBy, 40)}): it excuses nothing`);
			entries.push({ ...entry, valid: false });
			continue;
		}
		const before = forged.length;
		if (!OWNERS.includes(approvedBy)) forged.push(`${where}: approvedBy (${safe(approvedBy, 40)}) is not the owner: only ${OWNERS.join(', ')} approves an exception`);
		else if (approvedBy === entry.proposedBy.trim()) forged.push(`${where}: approved by its own proposer`);
		else if (!isDate(entry.approvedOn)) forged.push(`${where}: approved without approvedOn (a real day, YYYY-MM-DD)`);
		else if (entry.approvedOn > today) forged.push(`${where}: approvedOn ${entry.approvedOn} is in the future`);
		else if (entry.expires < entry.approvedOn || daysBetween(entry.approvedOn, entry.expires) > MAX_EXCEPTION_DAYS) forged.push(`${where}: expires ${entry.expires} must be within ${MAX_EXCEPTION_DAYS} days after approvedOn ${entry.approvedOn}`);
		const wellFormed = forged.length === before;
		if (wellFormed && entry.expires < today) problems.push(`${where} expired on ${entry.expires}: the finding counts against its frame again`);
		entries.push({ ...entry, valid: wellFormed && entry.expires >= today });
	}
	return { entries, problems, forged };
}

/** A JSON value with its object keys sorted, so that two copies compare by content and not by key order. */
function canonical(value) {
	if (Array.isArray(value)) return value.map(canonical);
	if (isRecord(value)) return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonical(value[key])]));
	return value;
}
const sameJson = (a, b) => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));
const isApproval = (approvedBy) => {
	const value = String(approvedBy ?? '').trim();
	return value !== '' && !/^PENDING/i.test(value);
};

/**
 * The agent-side hold on approvals (the ARV-096a review): the code cannot tell a well-formed approval naming the owner from the
 * owner's own, so it compares with a reference's copies (label names it in each problem). Once the reference holds the frames
 * approved, any change to the frames file at all (a frame, approvedBy, approvedOn, a return to PENDING, its comment) is the
 * owner's, made on main; an exception approved here must be one the reference holds, unchanged in every field. A pending
 * exception may be added or changed and any exception removed. A copy that is undefined is a file not compared (for the frames:
 * the reference does not hold it, so nothing approved to drift from). Each problem fails the step in either mode.
 */
export function approvalDrift(mainFrames, mainExceptions, frames, exceptions, label = 'main') {
	const problems = [];
	const where = safe(label, 80);
	if (mainFrames !== undefined && isApproval(mainFrames?.approvedBy) && !sameJson(mainFrames, frames)) {
		problems.push(`security/remediation-frames.json differs from ${where}, where the frames are approved: only the owner changes them, on main (rebase on main if it moved)`);
	}
	if (mainExceptions !== undefined) {
		const held = new Set((Array.isArray(mainExceptions?.exceptions) ? mainExceptions.exceptions : []).map((entry) => JSON.stringify(canonical(entry))));
		for (const [index, entry] of (Array.isArray(exceptions?.exceptions) ? exceptions.exceptions : []).entries()) {
			if (!isApproval(entry?.approvedBy) || held.has(JSON.stringify(canonical(entry)))) continue;
			problems.push(`exception ${index + 1} (${safe(entry?.finding ?? '?', 40)}) is approved but new or changed relative to ${where}: only the owner approves an exception, on main`);
		}
	}
	return problems;
}

/**
 * The references the approvals are compared with (the ARV-096a review, round 3), from facts scripts/verify.mjs gathers with git.
 * HEAD always, so an uncommitted edit is caught whatever the branches say. Then an explicit reference when one is given
 * (verify.mjs story --base, or ARIVA_APPROVALS_BASE), otherwise the newest of the candidates (main and origin/main) that is an
 * ancestor of HEAD: local main is never moved here (CLAUDE.md rule 8 moves the remote's), and a stale reference would miss a
 * change to frames approved since and read every exception approved since as new. A candidate that is not an ancestor of HEAD is
 * not compared (the branch is rebased on it first); two candidates at one commit are one reference, two ancestors of HEAD neither
 * of which contains the other are both compared, and a reference at HEAD's own commit is HEAD.
 * In: head { name, commit, date }; candidates [{ name, commit (null: names no commit), date, ancestorOfHead, ancestorOf (the
 * names of the other candidates it is an ancestor of) }]; explicit (whether the candidates are one explicit reference).
 * Out: { references [{ name, commit, date, label }], warnings, problems (an explicit reference that names no commit) }.
 */
export function approvalReferences({ head, candidates = [], explicit = false }) {
	const short = (commit) => String(commit).slice(0, 8);
	const label = (name, commit, date) => `${name} at ${short(commit)} (${isDate(date) ? date : 'date unknown'})`;
	const references = [];
	const warnings = [];
	const problems = [];
	const headName = head?.name ?? 'HEAD';
	if (head?.commit) references.push({ name: headName, commit: head.commit, date: head.date, label: label(headName, head.commit, head.date) });
	else warnings.push(`${headName} names no commit, so the working tree is compared with no commit of its own`);
	for (const candidate of candidates.filter((c) => !c.commit)) {
		if (explicit) problems.push(`${candidate.name} names no commit here, so the approvals cannot be compared with it`);
		else warnings.push(`${candidate.name} names no commit here, so the approvals are not compared with it`);
	}
	const present = candidates.filter((c) => c.commit);
	let chosen;
	if (explicit) {
		chosen = present;
		for (const c of present.filter((c) => !c.ancestorOfHead)) warnings.push(`${c.name} at ${short(c.commit)} is not an ancestor of HEAD, so what changed since it may read as drift`);
		for (const c of present.filter((c) => c.commit === head?.commit)) warnings.push(`${c.name} is HEAD's own commit, so only uncommitted changes are compared`);
	} else {
		for (const c of present.filter((c) => !c.ancestorOfHead)) warnings.push(`${c.name} at ${short(c.commit)} is not an ancestor of HEAD, so it is not compared (rebase on it to compare with it)`);
		const eligible = present.filter((c) => c.ancestorOfHead);
		chosen = eligible.filter((c) => !eligible.some((other) => other.commit !== c.commit && (c.ancestorOf ?? []).includes(other.name)));
		if (!eligible.length && candidates.length) warnings.push(`none of ${candidates.map((c) => c.name).join(' and ')} is an ancestor of HEAD, so the approvals are compared with HEAD alone`);
		const local = present.find((c) => c.name === 'main');
		const remote = present.find((c) => c.name === 'origin/main');
		if (local && remote && local.commit !== remote.commit) warnings.push(`local main at ${short(local.commit)} and origin/main at ${short(remote.commit)} differ (local main is never moved here, CLAUDE.md rule 8): only the newest of them that is an ancestor of HEAD is compared`);
	}
	const byCommit = new Map();
	for (const c of chosen) {
		if (c.commit === head?.commit) continue; // compared already, as HEAD
		const known = byCommit.get(c.commit);
		if (known) known.names.push(c.name);
		else byCommit.set(c.commit, { names: [c.name], commit: c.commit, date: c.date });
	}
	for (const { names, commit, date } of byCommit.values()) {
		const name = names.join(' and ');
		references.push({ name, commit, date, label: label(name, commit, date) });
	}
	return { references, warnings, problems };
}

/**
 * The verdict: every finding with its age, frame and state (overdue, within, no fix, age unknown, excepted, no frame), the
 * problems that fail the step (hard: a bad frames or exceptions file, a malformed approval, drift from a reference; then an
 * overdue finding without a valid exception, an invalid exception, a finding or an input that cannot be judged) and whether the
 * step fails (on a hard problem in either mode, on any problem once the frames are approved). references: [{ label, frames,
 * exceptions }], each copy undefined when not compared.
 */
export function evaluate(findings, { frames: framesConfig, exceptions: exceptionsConfig, advisoryDates = {}, today, inputs = [], missing = [], references = [], drift = [], notes = [] }) {
	const { frames, approved, problems: frameProblems } = readFrames(framesConfig, today);
	const { entries, problems: exceptionProblems, forged } = readExceptions(exceptionsConfig, today);
	const drifted = [...drift, ...references.flatMap((reference) => approvalDrift(reference.frames, reference.exceptions, framesConfig, exceptionsConfig, reference.label))];
	const compared = references.map((reference) => reference.label);
	const advisories = isRecord(advisoryDates) ? advisoryDates : {};
	const unjudged = [];
	const overdue = [];
	const rows = [];
	const seen = new Set();
	for (const finding of findings) {
		const key = JSON.stringify([finding.source, finding.package, finding.version, finding.id]);
		if (seen.has(key)) continue;
		seen.add(key);
		const recorded = finding.id && Object.hasOwn(advisories, finding.id) && isRecord(advisories[finding.id]) ? advisories[finding.id] : undefined;
		const published = isDate(finding.published) ? finding.published : isDate(recorded?.published) ? recorded.published : null;
		const name = typeof finding.package === 'string' ? finding.package.toLowerCase() : '';
		const patched = isRecord(recorded?.patched) && Object.hasOwn(recorded.patched, name) ? recorded.patched[name] : undefined;
		const fix = finding.fix === 'unknown' && typeof patched === 'boolean' ? (patched ? 'yes' : 'no') : finding.fix;
		// A publication date after the day of the run is no age: the finding cannot be judged, and is never within its frame.
		const age = published !== null && published <= today ? daysBetween(published, today) : null;
		const frame = Object.hasOwn(frames, finding.severity) ? frames[finding.severity] : undefined;
		const exception = entries.find((entry) => entry.finding === finding.id && entry.package === finding.package);
		let state;
		const what = `${safe(finding.source, 40)}: ${safe(finding.package, 80)} ${safe(finding.version, 40)} ${finding.id ?? '(no id)'}`;
		if (fix === 'no') state = 'no fix yet';
		else if (finding.severity === 'unknown') {
			state = 'severity unknown';
			unjudged.push(`${what}: its severity cannot be read, so it cannot be held to a frame`);
		} else if (frame === undefined) state = 'its frame is not valid';
		else if (frame === null) state = 'no age frame (the next planned release)';
		else if (age === null || fix === 'unknown') {
			state = age === null ? 'age unknown' : 'fix unknown';
			const why = age === null ? 'age cannot be read (no publication date from its advisory or its report, or one after today)' : 'fix cannot be read (its advisory was not read or does not name the package)';
			if (!exception?.valid) unjudged.push(`${what} ${finding.severity}: its ${why}, so it cannot be held to its frame`);
		}
		else if (age <= frame) state = 'within its frame';
		else if (exception?.valid) state = `past its frame, excepted until ${exception.expires}`;
		else {
			state = 'past its frame';
			overdue.push(`${what} ${finding.severity}, ${age} days against ${frame}`);
		}
		rows.push({ ...finding, fix, published, age, frame: frame ?? null, state });
	}
	const unread = missing.map((name) => `the ${safe(name, 40)} audit could not be read, or read no vulnerability data, so its findings are not judged`);
	const hard = [...frameProblems, ...forged, ...drifted];
	const soft = [...exceptionProblems, ...overdue, ...unjudged, ...unread];
	return { rows, hard, soft, problems: [...hard, ...soft], approved, inputs, missing, compared, notes, fails: hard.length > 0 || (approved && soft.length > 0) };
}

/** Labels joined as words: A; A and B; A, B and C. */
const joined = (items) => (items.length < 2 ? items.join('') : `${items.slice(0, -1).join(', ')} and ${items.at(-1)}`);

/** The job summary in Markdown: the mode, the inputs, the references compared, every finding with its state, then what fails (or would fail). */
export function summary(verdict) {
	const compared = (verdict.compared ?? []).map((label) => safe(label, 80));
	const lines = ['## Remediation time frames (ARV-096a)', '',
		verdict.approved ? 'The frames are approved: a finding past its frame without a valid exception, or one that cannot be judged, fails the step.' : 'The frames are Proposed (security/remediation-frames.json, approvedBy PENDING): the step reports and does not fail, except on a malformed approval or drift from an approval a reference holds.', '',
		`Inputs read: ${(verdict.inputs ?? []).map((input) => safe(input, 60)).join('; ') || 'none'}${verdict.missing?.length ? `; not read: ${verdict.missing.map((name) => safe(name, 40)).join(', ')}` : ''}.`, '',
		`Approvals compared with ${compared.length ? joined(compared) : 'nothing (no reference given, so no approval drift is checked)'}.`, '',
		...(verdict.notes ?? []).flatMap((note) => [`Note: ${safe(note, 300)}`, ''])];
	if (!verdict.rows.length) lines.push('No open finding.');
	else {
		lines.push('| Source | Package | Version | Advisory | Severity | Fix | Published | Age (days) | Frame (days) | State |', '|---|---|---|---|---|---|---|---|---|---|');
		const cell = (value) => safe(value, 80);
		for (const row of verdict.rows) lines.push(`| ${cell(row.source)} | ${cell(row.package)} | ${cell(row.version)} | ${cell(row.id ?? '')} | ${row.severity} | ${row.fix} | ${row.published ?? 'unknown'} | ${row.age ?? 'unknown'} | ${row.frame ?? 'none'} | ${row.state} |`);
	}
	const bullet = (problem) => `- ${String(problem).replace(CONTROLS, ' ')}`;
	const hard = verdict.hard ?? [];
	const soft = verdict.soft ?? verdict.problems.slice(hard.length);
	if (verdict.approved) {
		if (verdict.problems.length) lines.push('', '### Fails the step', '', ...verdict.problems.map(bullet));
	} else {
		if (hard.length) lines.push('', '### Fails the step (in either mode)', '', ...hard.map(bullet));
		if (soft.length) lines.push('', '### Would fail the step once the frames are approved', '', ...soft.map(bullet));
	}
	return `${lines.join('\n')}\n`;
}

/** The GitHub advisory database's API URL for an id that is exactly a full GHSA id; null for anything else (no other text ever becomes a path). */
export function advisoryUrl(id) {
	return typeof id === 'string' && GHSA.test(id) ? `https://api.github.com/advisories/${id}` : null;
}

/**
 * One advisory of GitHub's advisory database as the step records it: its publication date and, for each NuGet package it names
 * (ecosystem nuget, the id in lower case), whether any of its vulnerable ranges for that package names a first patched version;
 * null without a publication date that is a real day. Another ecosystem's package never decides a NuGet finding's fix.
 */
export function advisoryRecord(advisory) {
	const published = String(advisory?.published_at ?? '').slice(0, 10);
	if (!isDate(published)) return null;
	const patched = Object.create(null);
	for (const vulnerability of Array.isArray(advisory.vulnerabilities) ? advisory.vulnerabilities : []) {
		const pkg = vulnerability?.package;
		if (pkg?.ecosystem !== 'nuget' || typeof pkg.name !== 'string') continue;
		const name = pkg.name.toLowerCase();
		patched[name] = patched[name] === true || Boolean(vulnerability.first_patched_version);
	}
	return { published, patched };
}

/** Each GHSA advisory's record (advisoryRecord), from GitHub's advisory database. */
async function readAdvisories(ids) {
	const advisories = {};
	const headers = { accept: 'application/vnd.github+json', 'user-agent': 'ariva-remediation-frames' };
	if (process.env.GITHUB_TOKEN) headers.authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
	for (const id of ids) {
		const url = advisoryUrl(id);
		if (url === null) continue;
		try {
			const response = await fetch(url, { headers, redirect: 'error', signal: AbortSignal.timeout(15_000) });
			if (!response.ok) continue;
			const record = advisoryRecord(await response.json());
			if (record) advisories[id] = record;
		} catch {
			// The date stays unknown, which the summary says: listed while the frames are Proposed, a failure once they are approved.
		}
	}
	return advisories;
}

/** The rules on recorded audit outputs: each severity, a finding without a fix, inputs not read, exceptions, approvals and their drift from references, the reference choice, both modes, and the summary. */
export function selfTest() {
	const today = '2026-11-09';
	const proposed = { approvedBy: 'PENDING: the owner', frames: { critical: 7, high: 30, moderate: 90, low: null } };
	const approved = { ...proposed, approvedBy: OWNERS[0], approvedOn: '2026-11-01' };
	const npm = (severity, url, fixAvailable = true) => JSON.stringify({ auditReportVersion: 2, vulnerabilities: { pkg: { name: 'pkg', severity, via: [{ name: 'pkg', url, severity, range: '<2.0.0' }], fixAvailable } } });
	const nugetReport = (projects, extra = {}) => JSON.stringify({ version: 1, parameters: '--vulnerable --include-transitive', sources: ['https://api.nuget.org/v3/index.json'], projects, ...extra });
	const nuget = (severity, url) => nugetReport([{ path: '/src/A.csproj', frameworks: [{ framework: 'net10.0', topLevelPackages: [{ id: 'Some.Package', resolvedVersion: '1.0.0', vulnerabilities: [{ severity, advisoryurl: url }] }] }] }]);
	const trivy = (severity, published, fixed) => JSON.stringify({ SchemaVersion: 2, ArtifactName: 'an-image', Results: [{ Target: 'an-image (ubuntu 24.04)', Class: 'os-pkgs',
		Vulnerabilities: [{ VulnerabilityID: 'CVE-2026-12345', PkgName: 'openssl', InstalledVersion: '3.0.0', FixedVersion: fixed, Severity: severity, PublishedDate: `${published}T00:00:00Z` }] }] });
	const ghsa = 'GHSA-2222-3333-4444';
	const url = `https://github.com/advisories/${ghsa}`;
	const dates = (published, patched = { 'some.package': true }) => ({ [ghsa]: { published, patched } });
	const exception = (fields) => ({ exceptions: [{ finding: ghsa, package: 'pkg', reason: 'no exposure', compensatingControl: 'not reachable', proposedBy: 'an agent', approvedBy: OWNERS[0], approvedOn: '2026-11-01', expires: '2026-12-31', ...fields }] });
	const none = { exceptions: [] };
	// A reference's copies, as verify.mjs passes them; an exceptions file the reference does not hold is an empty list.
	const HEAD = 'HEAD at 1a2b3c4d (2026-11-09)';
	const ORIGIN = 'origin/main at 5e6f7a8b (2026-11-06)';
	const at = (label, frames, exceptions = none) => ({ label, frames, exceptions });
	const run = (findings, frames, extra = {}) => evaluate(findings, { frames, exceptions: extra.exceptions ?? none, advisoryDates: extra.dates ?? {}, today, references: extra.references ?? [] });
	// An input as main() reads it: one that is not its tool's complete report counts as not read.
	const input = (parse, text, frames) => {
		const read = readInputs([{ name: 'the audit', read: () => parse(text) }]);
		return evaluate(read.findings, { frames, exceptions: none, today, inputs: read.inputs, missing: read.missing });
	};
	const unreachable = 'Unable to load the service index for source https://api.nuget.org/v3/index.json.';
	const notReports = [
		['a NuGet report whose source was unreachable (a project problem)', nugetFindings, nugetReport([{ path: '/src/A.csproj', problems: [{ level: 'error', text: unreachable }] }])],
		['a NuGet report whose source was unreachable (a report problem)', nugetFindings, nugetReport([{ path: '/src/A.csproj' }], { problems: [{ project: '/src/A.csproj', level: 'error', text: unreachable }] })],
		['a NuGet report without projects', nugetFindings, nugetReport([])],
		['a NuGet report without sources', nugetFindings, JSON.stringify({ version: 1, sources: [], projects: [{ path: '/src/A.csproj' }] })],
		['{} as the NuGet report', nugetFindings, '{}'],
		['[] as the NuGet report', nugetFindings, '[]'],
		['{} as an npm report', npmFindings, '{}'],
		['[] as an npm report', npmFindings, '[]'],
		['an npm report with an error', npmFindings, JSON.stringify({ auditReportVersion: 2, vulnerabilities: {}, error: { code: 'ENOLOCK', summary: 'This command requires an existing lockfile.' } })],
		['{} as a Trivy report', trivyFindings, '{}'],
		['[] as a Trivy report', trivyFindings, '[]'],
		['a Trivy report of schema version 1', trivyFindings, JSON.stringify({ SchemaVersion: 1, Results: [] })]
	];
	const clean = [
		['a complete NuGet report with nothing vulnerable', nugetFindings, nugetReport([{ path: '/src/A.csproj' }])],
		['a complete npm report with nothing vulnerable', npmFindings, JSON.stringify({ auditReportVersion: 2, vulnerabilities: {}, metadata: { vulnerabilities: { total: 0 } } })],
		['a complete Trivy report with nothing vulnerable', trivyFindings, JSON.stringify({ SchemaVersion: 2, Results: [{ Target: 'an-image (ubuntu 24.04)', Class: 'os-pkgs' }] })]
	];
	const cases = [
		['critical within 7 days', run(npmFindings(npm('critical', url)), approved, { dates: dates('2026-11-05') }), false],
		['critical past 7 days', run(npmFindings(npm('critical', url)), approved, { dates: dates('2026-10-30') }), true],
		['high past 30 days', run(nugetFindings(nuget('High', url)), approved, { dates: dates('2026-09-01') }), true],
		['high within 30 days', run(nugetFindings(nuget('High', url)), approved, { dates: dates('2026-10-20') }), false],
		['moderate past 90 days (Trivy MEDIUM)', run(trivyFindings(trivy('MEDIUM', '2026-07-01', '3.0.1')), approved), true],
		['moderate within 90 days', run(trivyFindings(trivy('MEDIUM', '2026-10-01', '3.0.1')), approved), false],
		['low has no age frame', run(trivyFindings(trivy('LOW', '2020-01-01', '3.0.1')), approved), false],
		['no fix yet never fails', run(trivyFindings(trivy('CRITICAL', '2020-01-01', '')), approved), false],
		['a NuGet advisory without a patched version is no fix', run(nugetFindings(nuget('Critical', url)), approved, { dates: dates('2020-01-01', { 'some.package': false }) }), false],
		['age unknown fails once approved', run(nugetFindings(nuget('Critical', url)), approved), true],
		['age unknown only reports while Proposed', run(nugetFindings(nuget('Critical', url)), proposed), false],
		['severity unknown fails once approved', run(trivyFindings(trivy('NEGLIGIBLE', '2026-11-01', '3.0.1')), approved), true],
		['an audit not read fails once approved', evaluate([], { frames: approved, exceptions: none, today, missing: ['nuget'] }), true],
		['an audit not read only reports while Proposed', evaluate([], { frames: proposed, exceptions: none, today, missing: ['nuget'] }), false],
		['excepted until its expiry', run(npmFindings(npm('critical', url)), approved, { dates: dates('2026-10-01'), exceptions: exception({}) }), false],
		['an expired exception fails', run(npmFindings(npm('critical', url)), approved, { dates: dates('2026-10-01'), exceptions: exception({ expires: '2026-11-01' }) }), true],
		['an unapproved exception fails', run(npmFindings(npm('critical', url)), approved, { dates: dates('2026-10-01'), exceptions: exception({ approvedBy: 'PENDING: the owner' }) }), true],
		['an unapproved exception fails without a finding', run([], approved, { exceptions: exception({ approvedBy: 'PENDING: the owner' }) }), true],
		['an exception without its reason fails', run([], approved, { exceptions: exception({ reason: '' }) }), true],
		['Proposed frames only report', run(npmFindings(npm('critical', url)), proposed, { dates: dates('2026-10-01') }), false],
		['a frame out of range fails', run([], { ...approved, frames: { ...approved.frames, high: 0 } }), true],
		// The ARV-096a review: malformed approvals fail in either mode.
		['frames approved by someone else fail', run([], { ...approved, approvedBy: 'An agent' }), true],
		['frames approved without a date fail', run([], { ...approved, approvedOn: '' }), true],
		['frames approved in the future fail', run([], { ...approved, approvedOn: '2026-12-01' }), true],
		['frames whose approvedBy is a list holding the owner fail', run([], { ...approved, approvedBy: [OWNERS[0]] }), true],
		['frames whose approvedBy is a number fail while Proposed', run([], { ...proposed, approvedBy: 42 }), true],
		['an exception approved by someone else fails while Proposed', run([], proposed, { exceptions: exception({ approvedBy: 'An agent' }) }), true],
		['an exception approved by its proposer fails', run([], proposed, { exceptions: exception({ proposedBy: OWNERS[0] }) }), true],
		['an exception approved without a date fails', run([], proposed, { exceptions: exception({ approvedOn: undefined }) }), true],
		['an exception past its cap fails', run([], proposed, { exceptions: exception({ expires: '2027-06-01' }) }), true],
		['an exception for another package excuses nothing', run(npmFindings(npm('critical', url)), approved, { dates: dates('2026-10-01'), exceptions: exception({ package: 'other' }) }), true],
		['recorded advisories of the wrong shape are no dates', run(npmFindings(npm('critical', url)), approved, { dates: { [ghsa]: '2026-10-01' } }), true],
		// Round 2 of the ARV-096a review. Only low may have no age frame.
		['a null frame for critical fails while Proposed', run([], { ...proposed, frames: { ...proposed.frames, critical: null } }), true],
		['a null frame for high fails once approved', run([], { ...approved, frames: { ...approved.frames, high: null } }), true],
		['a null frame for moderate fails', run([], { ...approved, frames: { ...approved.frames, moderate: null } }), true],
		// Approvals compared with references (approvalDrift), failing in either mode.
		['frames identical to the approved ones of every reference pass', run([], approved, { references: [at(HEAD, approved), at(ORIGIN, approved)] }), false],
		['frames returned to PENDING from approved at a reference fail while Proposed', run([], proposed, { references: [at(ORIGIN, approved)] }), true],
		['a frame changed from approved at a reference fails', run([], { ...approved, frames: { ...approved.frames, critical: 30 } }, { references: [at(ORIGIN, approved)] }), true],
		['approvedOn changed from approved at a reference fails', run([], { ...approved, approvedOn: '2026-11-02' }, { references: [at(ORIGIN, approved)] }), true],
		['frames changed while Proposed at every reference are no drift', run([], { ...proposed, frames: { ...proposed.frames, high: 20 } }, { references: [at(HEAD, proposed), at(ORIGIN, proposed)] }), false],
		['an uncommitted edit of frames HEAD holds approved fails, whatever the other reference holds', run([], { ...approved, frames: { ...approved.frames, high: 60 } }, { references: [at(HEAD, approved), at(ORIGIN, undefined)] }), true],
		['frames HEAD holds as the tree does but changed from approved at origin/main fail', run([], { ...approved, frames: { ...approved.frames, high: 60 } }, { references: [at(HEAD, { ...approved, frames: { ...approved.frames, high: 60 } }), at(ORIGIN, approved)] }), true],
		['an approved exception new relative to a reference fails while Proposed', run([], proposed, { exceptions: exception({}), references: [at(ORIGIN, undefined)] }), true],
		['an approved exception added with the file (the reference has none) fails while Proposed', run([], proposed, { exceptions: exception({}), references: [at(HEAD, undefined, none)] }), true],
		['an approved exception every reference holds unchanged excuses its finding', run(npmFindings(npm('critical', url)), approved, { dates: dates('2026-10-01'), exceptions: exception({}), references: [at(HEAD, approved, exception({})), at(ORIGIN, approved, exception({}))] }), false],
		['an approved exception committed on the branch but not on origin/main fails', run([], proposed, { exceptions: exception({}), references: [at(HEAD, undefined, exception({})), at(ORIGIN, undefined)] }), true],
		['an approved exception changed from a reference fails', run([], proposed, { exceptions: exception({ expires: '2027-01-15' }), references: [at(ORIGIN, undefined, exception({}))] }), true],
		['a pending exception new relative to a reference is no drift', run([], proposed, { exceptions: exception({ approvedBy: 'PENDING: the owner' }), references: [at(ORIGIN, undefined)] }), false],
		['a reference copy that could not be read fails while Proposed', evaluate([], { frames: proposed, exceptions: none, today, drift: ['the copy at origin/main could not be read'] }), true],
		// Only real days, and none after today.
		['frames approved on 2026-02-30 fail', run([], { ...approved, approvedOn: '2026-02-30' }), true],
		['an exception approved in the future fails while Proposed', run([], proposed, { exceptions: exception({ approvedOn: '2026-12-01' }) }), true],
		['an exception expiring before its approval fails while Proposed', run([], proposed, { exceptions: exception({ approvedOn: '2026-11-01', expires: '2026-10-20' }) }), true],
		['an exception approved on 2026-13-45 fails while Proposed', run([], proposed, { exceptions: exception({ approvedOn: '2026-13-45' }) }), true],
		['an exception approved on 2026-02-30 fails while Proposed', run([], proposed, { exceptions: exception({ approvedOn: '2026-02-30', expires: '2026-04-30' }) }), true],
		['a critical finding published in the future is age unknown and fails once approved', run(trivyFindings(trivy('CRITICAL', '2026-12-01', '3.0.1')), approved), true],
		['a critical finding published in the future only reports while Proposed', run(trivyFindings(trivy('CRITICAL', '2026-12-01', '3.0.1')), proposed), false],
		['an advisory recorded as published in the future is age unknown', run(npmFindings(npm('critical', url)), approved, { dates: dates('2026-12-01') }), true],
		['a Trivy date that is no real day (2026-09-31) is age unknown', run(trivyFindings(trivy('MEDIUM', '2026-09-31', '3.0.1')), approved), true],
		// A NuGet fix read for that NuGet package only; npm's info is low.
		['a NuGet advisory patched only for another package leaves the fix unknown', run(nugetFindings(nuget('Critical', url)), approved, { dates: dates('2026-11-05', { 'other.package': true }) }), true],
		['a NuGet advisory patched for another package but not this one is no fix', run(nugetFindings(nuget('Critical', url)), approved, { dates: dates('2020-01-01', { 'some.package': false, 'other.package': true }) }), false],
		['an npm info finding is low (no age frame)', run(npmFindings(npm('info', url)), approved, { dates: dates('2020-01-01') }), false],
		// An input that read no vulnerability data is one not read; a complete one with nothing vulnerable is read.
		...notReports.flatMap(([name, parse, text]) => [
			[`${name} counts as not read: fails once approved`, input(parse, text, approved), true],
			[`${name} counts as not read: only reported while Proposed`, input(parse, text, proposed), false]
		]),
		...clean.map(([name, parse, text]) => [`${name} is read and passes once approved`, input(parse, text, approved), false])
	];
	const failures = [];
	for (const [name, verdict, fails] of cases) if (verdict.fails !== fails) failures.push(`${name}: expected ${fails ? 'a failure' : 'a pass'}, got ${verdict.fails ? 'a failure' : 'a pass'} (${verdict.problems.join('; ') || 'no problem'})`);
	const throws = (action) => { try { action(); return false; } catch { return true; } };
	const controls = new RegExp(CONTROLS.source, 'u');
	const reported = run(npmFindings(npm('critical', url)), proposed, { dates: dates('2026-10-01') });
	const hostileText = '[x](https://example.test) <img src=x> `y` ##vso[task.setvariable variable=A]1 ::set-output \u{1b}[31mred\u{1b}[0m \u{202e}gnp.exe \u{200b}hidden \u{85}next \u{2028}line \u{2066}isolate \u{feff}mark \u{7f}del \u{9b}31m \u{61c}arabic-mark \u{e0041}\u{e007f}tag \u{ad}soft \u{3164}filler \u{fe0f}selector \u{fffb}annotation \u{180e}mongolian';
	const hostile = summary(run([{ source: 'npm web\u{202e}', package: hostileText, version: '|1\u{1b}]8;;x', id: null, severity: 'high', fix: 'yes', published: '2026-01-01' }], proposed));
	const record = advisoryRecord({ published_at: '2026-10-13T18:22:11Z', vulnerabilities: [
		{ package: { ecosystem: 'npm', name: 'Some.Package' }, first_patched_version: '2.0.0' },
		{ package: { ecosystem: 'nuget', name: 'Some.Package' }, first_patched_version: null },
		{ package: { ecosystem: 'nuget', name: 'Other.Package' }, first_patched_version: '1.2.3' }] });
	const lone = (source, pkg) => ({ source, package: pkg, version: '1', id: null, severity: 'low', fix: 'yes', published: null });
	const reordered = { exceptions: [{ expires: '2026-12-31', ...exception({}).exceptions[0] }] };
	// The reference choice (approvalReferences), on facts as verify.mjs gathers them.
	const sha = (c) => c.repeat(40);
	const head = { name: 'HEAD', commit: sha('1'), date: '2026-11-09' };
	const candidate = (name, commit, ancestorOfHead, ancestorOf = []) => ({ name, commit, date: '2026-11-01', ancestorOfHead, ancestorOf });
	const choose = (candidates, explicit = false) => approvalReferences({ head, candidates, explicit });
	const names = (choice) => choice.references.map((reference) => reference.name).join(', ');
	const staleMain = choose([candidate('main', sha('a'), true, ['origin/main']), candidate('origin/main', sha('b'), true)]);
	const checks = [
		['Proposed frames: the summary says what would fail', reported.problems.length === 1 && summary(reported).includes('Would fail the step once the frames are approved')],
		['the summary of a clean run says so', summary(run([], approved)).includes('No open finding.')],
		['the summary escapes what an audit reported', !/##vso\[|::|(?<!\\)<img|(?<!\\)\]\(|(?<!\\)`/.test(hostile)],
		['the summary passes on no hidden, bidirectional or control character (U+061C and the tag characters among them)', !hostile.split('\n').some((line) => controls.test(line))],
		['every character SEC-130 refuses is in the summary\'s set', HIDDEN_CHARS.every(([from, to = from]) => controls.test(String.fromCodePoint(from)) && controls.test(String.fromCodePoint(to)))],
		['the summary keeps Arabic text', safe('\u{627}\u{644}\u{639}\u{631}\u{628}\u{64a}\u{629}') === '\u{627}\u{644}\u{639}\u{631}\u{628}\u{64a}\u{629}'],
		['the summary names its inputs', summary(evaluate([], { frames: proposed, exceptions: none, today, inputs: ['nuget (0 findings)'], missing: ['npm web'] })).includes('Inputs read: nuget (0 findings); not read: npm web.')],
		['the summary names the references compared', summary(run([], approved, { references: [at(HEAD, approved), at(ORIGIN, approved)] })).includes(`Approvals compared with ${HEAD} and ${ORIGIN}.`)],
		['the summary says when nothing is compared', summary(run([], approved)).includes('Approvals compared with nothing')],
		['the summary carries the notes', summary(evaluate([], { frames: proposed, exceptions: none, today, notes: ['local main and origin/main differ'] })).includes('Note: local main and origin/main differ')],
		['a drift problem names its reference', run([], proposed, { references: [at(ORIGIN, approved)] }).hard.some((problem) => problem.includes(ORIGIN))],
		['a hard problem while Proposed is under Fails the step', summary(run([], proposed, { references: [at(ORIGIN, approved)] })).includes('### Fails the step (in either mode)')],
		['every input that is not a report is listed as not read', notReports.every(([, parse, text]) => { const read = readInputs([{ name: 'x', read: () => parse(text) }]); return read.missing.length === 1 && read.findings.length === 0 && read.reasons.length === 1; })],
		...[['NuGet', nugetFindings], ['npm', npmFindings], ['Trivy', trivyFindings]].flatMap(([tool, parse]) => ['"a report"', '42', 'null', 'true'].map((text) => [`${tool}: ${text} is not a report`, throws(() => parse(text))])),
		['a critical finding published in the future reads as age unknown', run(trivyFindings(trivy('CRITICAL', '2026-12-01', '3.0.1')), approved).rows[0]?.state === 'age unknown'],
		['a NuGet advisory patched only for another package reads as fix unknown', run(nugetFindings(nuget('Critical', url)), approved, { dates: dates('2026-11-05', { 'other.package': true }) }).rows[0]?.state === 'fix unknown'],
		['npm info reads as low', run(npmFindings(npm('info', url)), approved).rows[0]?.severity === 'low'],
		['advisoryRecord: another ecosystem never patches a NuGet package', record?.published === '2026-10-13' && record.patched['some.package'] === false && record.patched['other.package'] === true],
		['advisoryRecord: no real publication date is no record', advisoryRecord({ published_at: '2026-02-30T00:00:00Z' }) === null && advisoryRecord(null) === null && advisoryRecord('x') === null],
		['advisoryUrl: a full GHSA id', advisoryUrl(ghsa) === `https://api.github.com/advisories/${ghsa}`],
		...['CVE-2026-12345', '../x', `${ghsa}/../../x`, `${ghsa}\n`, ` ${ghsa}`, 'ghsa-2222-3333-4444', 'GHSA-2222-3333-444a', 42, null].map((id) => [`advisoryUrl: ${JSON.stringify(id)} is no URL`, advisoryUrl(id) === null]),
		['isDate: real days only', isDate('2028-02-29') && !isDate('2026-02-29') && !isDate('2026-02-30') && !isDate('2026-13-45') && !isDate('2026-11-9') && !isDate(20261109)],
		['two findings whose fields join alike are both kept', run([lone('npm a b', 'c'), lone('npm a', 'b c')], proposed).rows.length === 2],
		['the same finding twice is one row', run([lone('npm a', 'c'), lone('npm a', 'c')], proposed).rows.length === 1],
		['approvalDrift: an exception a reference holds with its keys in another order is no drift', approvalDrift(undefined, reordered, proposed, exception({})).length === 0],
		['approvalDrift: nothing compared is no drift', approvalDrift(undefined, undefined, approved, exception({})).length === 0],
		// The reference choice: HEAD always, then the newest of main and origin/main that is an ancestor of HEAD.
		['references: a stale local main is passed over for origin/main, with a warning', names(staleMain) === 'HEAD, origin/main' && staleMain.warnings.some((warning) => warning.includes('differ'))],
		['references: a stale origin/main is passed over for main', names(choose([candidate('main', sha('a'), true), candidate('origin/main', sha('b'), true, ['main'])])) === 'HEAD, main'],
		['references: a candidate that is not an ancestor of HEAD is not compared, with a warning', (() => { const choice = choose([candidate('main', sha('a'), true), candidate('origin/main', sha('b'), false)]); return names(choice) === 'HEAD, main' && choice.warnings.some((warning) => warning.startsWith('origin/main') && warning.includes('not an ancestor')); })()],
		['references: neither an ancestor of HEAD leaves HEAD alone', names(choose([candidate('main', sha('a'), false), candidate('origin/main', sha('b'), false)])) === 'HEAD'],
		['references: no main and no origin/main leave HEAD alone, with warnings', (() => { const choice = choose([candidate('main', null, false), candidate('origin/main', null, false)]); return names(choice) === 'HEAD' && choice.warnings.length >= 2 && choice.problems.length === 0; })()],
		['references: main and origin/main at one commit are one reference', names(choose([candidate('main', sha('a'), true, ['origin/main']), candidate('origin/main', sha('a'), true, ['main'])])) === 'HEAD, main and origin/main'],
		['references: two ancestors of HEAD neither containing the other are both compared', names(choose([candidate('main', sha('a'), true), candidate('origin/main', sha('b'), true)])) === 'HEAD, main, origin/main'],
		['references: a candidate at HEAD itself is HEAD alone', names(choose([candidate('main', head.commit, true), candidate('origin/main', sha('b'), true, ['main'])])) === 'HEAD'],
		['references: an explicit reference replaces main and origin/main', names(choose([candidate('1a2b3c4', sha('c'), true)], true)) === 'HEAD, 1a2b3c4'],
		['references: an explicit reference that names no commit is a problem', choose([candidate('no-such-ref', null, false)], true).problems.length === 1],
		['references: an explicit reference that is not an ancestor of HEAD is compared, with a warning', (() => { const choice = choose([candidate('other', sha('c'), false)], true); return names(choice) === 'HEAD, other' && choice.warnings.length === 1; })()],
		['references: labels carry the short commit and its date', staleMain.references.map((reference) => reference.label).join('; ') === `HEAD at ${sha('1').slice(0, 8)} (2026-11-09); origin/main at ${sha('b').slice(0, 8)} (2026-11-01)`],
		['references: no HEAD commit is a warning, not a reference', (() => { const choice = approvalReferences({ head: { name: 'HEAD', commit: null }, candidates: [] }); return choice.references.length === 0 && choice.warnings.length === 1; })()]
	];
	for (const [name, ok] of checks) if (!ok) failures.push(name);
	return { count: cases.length + checks.length, failures };
}

async function main(argv) {
	if (argv.includes('--self-test')) {
		const { count, failures } = selfTest();
		if (failures.length) { console.error(`remediation frames self-test failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
		console.log(`remediation frames self-test: ${count} cases each give exactly their verdict`);
		return;
	}
	const value = (flag) => {
		const index = argv.indexOf(flag);
		if (index < 0) return undefined;
		if (index + 1 >= argv.length) throw new Error(`${flag} needs a value`);
		return argv[index + 1];
	};
	const values = (flag) => argv.flatMap((arg, index) => {
		if (arg !== flag) return [];
		if (index + 1 >= argv.length) throw new Error(`${flag} needs a value`);
		return [argv[index + 1]];
	});
	const pair = (flag, spec) => {
		const at = spec.indexOf('=');
		if (at <= 0) throw new Error(`${flag} takes a name, an equals sign and a file`);
		return [spec.slice(0, at), spec.slice(at + 1)];
	};
	const today = value('--today') ?? new Date().toISOString().slice(0, 10);
	if (!isDate(today)) throw new Error('--today must be a real day (YYYY-MM-DD)');
	const readJson = (file) => JSON.parse(fs.readFileSync(file, 'utf8'));
	const list = [];
	if (value('--nuget')) list.push({ name: 'nuget', read: () => nugetFindings(fs.readFileSync(value('--nuget'), 'utf8')) });
	for (const spec of values('--npm')) {
		const [name, file] = pair('--npm', spec);
		list.push({ name: `npm ${name}`, read: () => npmFindings(fs.readFileSync(file, 'utf8'), `npm ${name}`) });
	}
	for (const file of values('--trivy')) list.push({ name: `trivy ${path.basename(file)}`, read: () => trivyFindings(fs.readFileSync(file, 'utf8'), `trivy ${path.basename(file)}`) });
	// An input that cannot be read or parsed, or is not its tool's complete report, is one not read: reported while the frames
	// are Proposed, a failure once they are approved.
	const { findings, inputs, missing, reasons } = readInputs(list, values('--missing'));
	for (const reason of reasons) console.error(`remediation frames: not read: ${logSafe(reason)}`);
	const ids = [...new Set(findings.map((finding) => finding.id).filter(Boolean))];
	const advisoryDates = value('--advisories') ? readJson(value('--advisories')) : argv.includes('--offline') ? {} : await readAdvisories(ids);
	const config = (name) => readJson(path.join(ROOT, 'security', name));
	// The references' copies (approvalDrift), grouped by label: one named but unreadable fails the step, since the approvals then
	// cannot be compared with it.
	const drift = [];
	const references = new Map();
	for (const [flag, key, file] of [['--frames-at', 'frames', 'remediation-frames.json'], ['--exceptions-at', 'exceptions', 'remediation-exceptions.json']]) {
		for (const spec of values(flag)) {
			const [label, copy] = pair(flag, spec);
			if (!references.has(label)) references.set(label, { label, frames: undefined, exceptions: undefined });
			try {
				references.get(label)[key] = readJson(copy);
			} catch {
				drift.push(`the copy of security/${file} at ${safe(label, 80)} could not be read, so the approvals cannot be compared with it`);
			}
		}
	}
	const verdict = evaluate(findings, { frames: config('remediation-frames.json'), exceptions: config('remediation-exceptions.json'), advisoryDates, today, inputs, missing, references: [...references.values()], drift, notes: values('--note') });
	const text = summary(verdict);
	process.stdout.write(text);
	if (value('--summary')) fs.writeFileSync(value('--summary'), text);
	if (verdict.fails) process.exit(1);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
	main(process.argv.slice(2)).catch((error) => { console.error(`remediation frames: ${logSafe(error.message)}`); process.exit(2); });
}
