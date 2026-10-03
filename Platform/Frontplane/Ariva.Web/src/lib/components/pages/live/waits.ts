import type { StatusTone } from '$lib/components/shared/StatusBadge.svelte';
import type { ZoneSnapshot } from '$lib/core/Live.svelte';
import { isStale } from '$lib/core/Live.svelte';

/** Reference thresholds of the live screen: rule R-001 alerts above 15 minutes; amber from 10. */
export const nearMinutes = 10;
export const overMinutes = 15;

export type WaitStatus = 'withinTarget' | 'nearTarget' | 'overTarget' | 'degraded' | 'noData';

/** A zone's status from its snapshot: no data, stale or degraded first, then the wait against the thresholds. */
export function waitStatus(snapshot: ZoneSnapshot | undefined, now = Date.now()): WaitStatus {
	if (!snapshot) return 'noData';
	if (isStale(snapshot, now) || snapshot.nowcastDegraded || snapshot.nowcastMinutes === null)
		return 'degraded';
	if (snapshot.nowcastMinutes > overMinutes) return 'overTarget';
	if (snapshot.nowcastMinutes >= nearMinutes) return 'nearTarget';
	return 'withinTarget';
}

export const statusTone: Record<WaitStatus, StatusTone> = {
	withinTarget: 'success',
	nearTarget: 'warning',
	overTarget: 'danger',
	degraded: 'neutral',
	noData: 'neutral'
};

/**
 * Server values the screen has words for. Anything else is shown as the server sent it, as plain text, never used as an
 * i18n key or message (a brace in a server value would otherwise be read as ICU syntax).
 */
export const knownLabels = {
	deskStates: ['Serving', 'Idle', 'Paused', 'Closed', 'Unknown'],
	severities: ['Info', 'Warning', 'Critical'],
	alertStates: ['Raised', 'Acknowledged', 'Escalated', 'Cleared', 'Resolved'],
	noService: ['NothingOpen', 'ThroughputTooLow', 'NoThroughputData']
} as const;

/** The translated label of a known server value, or the value itself. */
export function labelOf(
	translate: (key: string) => string,
	prefix: string,
	value: string,
	known: readonly string[]
): string {
	return known.includes(value) ? translate(`${prefix}.${value}`) : value;
}
