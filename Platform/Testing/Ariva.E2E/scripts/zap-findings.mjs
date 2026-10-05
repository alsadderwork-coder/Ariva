#!/usr/bin/env node
// ARV-063: reads ZAP JSON reports, writes a summary table and fails on High risk alerts that security/zap-triage.json
// does not accept. Used by tests/zap/zap.spec.ts and by the dev deployment job of .github/workflows/security-zap.yml.
//
//   node scripts/zap-findings.mjs <summary.md> <scan>=<report.json> [<scan>=<report.json> ...]
//
// Exit code 0 when every High risk alert is triaged, 1 otherwise (the untriaged ones are printed).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const triageFile = path.resolve(here, '..', '..', '..', '..', 'security', 'zap-triage.json');
const RISKS = ['Informational', 'Low', 'Medium', 'High'];

/** The alerts of one ZAP JSON report (traditional-json), tagged with the scan's name. */
export function readAlerts(file, scan) {
	const report = JSON.parse(fs.readFileSync(file, 'utf8'));
	return (report.site ?? []).flatMap((site) =>
		(site.alerts ?? []).map((alert) => ({
			scan,
			pluginid: String(alert.pluginid),
			alert: String(alert.alert),
			riskcode: Number(alert.riskcode),
			confidence: Number(alert.confidence),
			count: Number(alert.count ?? alert.instances?.length ?? 0),
			example: String(alert.instances?.[0]?.uri ?? '')
		}))
	);
}

const FIELDS = ['scan', 'pluginid', 'alert', 'reason', 'proposedBy', 'approvedBy', 'date'];

/** The triage entries. An entry without every field is an error, never a silent acceptance. */
export function loadTriage(file = triageFile) {
	if (!fs.existsSync(file)) return [];
	const entries = JSON.parse(fs.readFileSync(file, 'utf8')).entries ?? [];
	for (const [index, entry] of entries.entries()) {
		const missing = FIELDS.filter((field) => typeof entry[field] !== 'string' || entry[field].trim() === '');
		if (missing.length) throw new Error(`security/zap-triage.json entry ${index}: missing ${missing.join(', ')}`);
	}
	return entries;
}

/** Proposed by an agent and not yet approved by a person: listed, but it accepts nothing. */
export const isPending = (entry) => /^PENDING/i.test(entry.approvedBy.trim());

function entryFor(alert, triage) {
	return triage.find((entry) => entry.pluginid === alert.pluginid && (entry.scan === '*' || entry.scan === alert.scan));
}

/** Accepted only by an approved entry for the alert's rule and scan. */
export function isTriaged(alert, triage) {
	const entry = entryFor(alert, triage);
	return !!entry && !isPending(entry);
}

/** A Markdown table of every alert, highest risk first. */
export function summary(alerts, triage) {
	const escape = (text) => String(text).replace(/\|/g, '\\|');
	const lines = ['# ZAP findings', '', '| Risk | Scan | Rule | Alert | Instances | Triaged | Example |', '|---|---|---|---|---|---|---|'];
	for (const alert of [...alerts].sort((a, b) => b.riskcode - a.riskcode || a.scan.localeCompare(b.scan) || a.pluginid.localeCompare(b.pluginid))) {
		lines.push(`| ${RISKS[alert.riskcode] ?? alert.riskcode} | ${alert.scan} | ${alert.pluginid} | ${escape(alert.alert)} | ${alert.count} | ${entryFor(alert, triage) ? (isTriaged(alert, triage) ? 'yes' : 'pending') : 'no'} | ${escape(alert.example)} |`);
	}
	return lines.join('\n') + '\n';
}

/** The High risk alerts the triage file does not accept, one line each. */
export function untriagedHigh(alerts, triage) {
	return alerts.filter((alert) => alert.riskcode >= 3 && !isTriaged(alert, triage)).map((alert) => `${alert.scan}: ${alert.pluginid} ${alert.alert} (${alert.example})`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
	const [summaryFile, ...reports] = process.argv.slice(2);
	if (!summaryFile || reports.length === 0) {
		console.error('usage: node scripts/zap-findings.mjs <summary.md> <scan>=<report.json> ...');
		process.exit(2);
	}
	const alerts = reports.flatMap((entry) => {
		const [scan, file] = entry.split('=');
		return readAlerts(file, scan);
	});
	const triage = loadTriage();
	fs.writeFileSync(summaryFile, summary(alerts, triage));
	const high = untriagedHigh(alerts, triage);
	if (high.length) {
		console.error(`ZAP: ${high.length} untriaged High risk finding(s):\n  ${high.join('\n  ')}`);
		process.exit(1);
	}
	console.log(`ZAP: ${alerts.length} alert type(s), no untriaged High risk finding. Summary: ${summaryFile}`);
}
