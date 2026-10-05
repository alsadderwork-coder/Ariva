// Types of scripts/zap-findings.mjs for tests/zap/zap.spec.ts.
export interface ZapAlert {
	scan: string;
	pluginid: string;
	alert: string;
	riskcode: number;
	confidence: number;
	count: number;
	example: string;
}

export interface ZapTriage {
	scan: string;
	pluginid: string;
	alert: string;
	reason: string;
	proposedBy: string;
	approvedBy: string;
	date: string;
}

export const triageFile: string;
export function readAlerts(file: string, scan: string): ZapAlert[];
export function loadTriage(file?: string): ZapTriage[];
export function isPending(entry: ZapTriage): boolean;
export function isTriaged(alert: ZapAlert, triage: ZapTriage[]): boolean;
export function summary(alerts: ZapAlert[], triage: ZapTriage[]): string;
export function untriagedHigh(alerts: ZapAlert[], triage: ZapTriage[]): string[];
