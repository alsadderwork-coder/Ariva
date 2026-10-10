import type { StatusTone } from '$lib/components/shared/StatusBadge.svelte';
import type { CampaignStatus, Criterion, TracerRun, Verdict } from '$lib/core/validation';

/**
 * Formatting of the campaign screens (ARV-104h): site local dates and times, shares, minutes and verdicts. Latin digits in
 * both languages, as the reports screen and the observer tablet. Everything returned is plain text for interpolation.
 */

function tag(locale: string | null | undefined): string {
	return locale?.startsWith('ar') ? 'ar-u-nu-latn' : 'en-GB';
}

/**
 * The site's time zone when the browser knows it, otherwise UTC: a zone name the server sends that Intl cannot use (an
 * unknown or malformed name) must not stop the screen from rendering.
 */
export function safeTimeZone(timeZone: string | null | undefined): string {
	if (!timeZone) return 'UTC';
	try {
		new Intl.DateTimeFormat('en', { timeZone });
		return timeZone;
	} catch {
		return 'UTC';
	}
}

/** "8 Oct 2026, 14:45" in the site's time zone (UTC when the browser does not know the zone). */
export function siteDateTime(
	iso: string,
	timeZone: string,
	locale: string | null | undefined
): string {
	const ms = Date.parse(iso);
	if (!Number.isFinite(ms)) return '';
	return new Intl.DateTimeFormat(tag(locale), {
		timeZone: safeTimeZone(timeZone),
		day: 'numeric',
		month: 'short',
		year: 'numeric',
		hour: '2-digit',
		minute: '2-digit',
		hourCycle: 'h23'
	}).format(new Date(ms));
}

/** A planned local day ("2026-10-08") as "8 Oct 2026"; the day itself when it is not a date. */
export function dayLabel(day: string, locale: string | null | undefined): string {
	const ms = Date.parse(`${day}T00:00:00Z`);
	if (!/^\d{4}-\d{2}-\d{2}$/.test(day) || !Number.isFinite(ms)) return day;
	return new Intl.DateTimeFormat(tag(locale), {
		timeZone: 'UTC',
		day: 'numeric',
		month: 'short',
		year: 'numeric'
	}).format(new Date(ms));
}

/** Today's local date (yyyy-MM-dd) in a time zone, or the browser's. */
export function today(timeZone?: string): string {
	return new Intl.DateTimeFormat('en-CA', {
		timeZone,
		year: 'numeric',
		month: '2-digit',
		day: '2-digit'
	}).format(new Date());
}

/** A local date moved by whole days ("2026-10-08" plus 1 is "2026-10-09"). */
export function addDays(day: string, days: number): string {
	const ms = Date.parse(`${day}T00:00:00Z`);
	return new Date(ms + days * 86_400_000).toISOString().slice(0, 10);
}

function numbers(locale: string | null | undefined, digits: number): Intl.NumberFormat {
	return new Intl.NumberFormat(locale?.startsWith('ar') ? 'ar-u-nu-latn' : 'en', {
		minimumFractionDigits: digits,
		maximumFractionDigits: digits
	});
}

/** A share as a percentage with one decimal ("95.3%"). */
export function percent(value: number, locale: string | null | undefined): string {
	return `${numbers(locale, 1).format(value * 100)}%`;
}

/** A signed share as a percentage ("+2.4%", "-1.0%"); a value that rounds to zero has no sign. */
export function signedPercent(value: number, locale: string | null | undefined): string {
	const text = numbers(locale, 1).format(Math.abs(value) * 100);
	const sign = value < 0 && text !== numbers(locale, 1).format(0) ? '-' : '+';
	return `${sign}${text}%`;
}

/** Minutes with one decimal ("1.8"). */
export function minutes(value: number, locale: string | null | undefined): string {
	return numbers(locale, 1).format(value);
}

/** A whole number ("1,234"). */
export function whole(value: number, locale: string | null | undefined): string {
	return numbers(locale, 0).format(value);
}

/** The status colour of a campaign; the label is always shown beside it. */
export function statusTone(status: CampaignStatus): StatusTone {
	return status === 'Running' ? 'info' : status === 'Closed' ? 'neutral' : 'warning';
}

/** The colour of a verdict; the word is always shown beside it. */
export function verdictTone(verdict: Verdict): StatusTone {
	return verdict === 'Pass' ? 'success' : verdict === 'Fail' ? 'danger' : 'neutral';
}

/** How a criterion's value and target read: a share, a signed share (bias), or minutes (nowcast error). */
export function criterionKind(criterion: Criterion): 'share' | 'bias' | 'minutes' {
	return criterion === 'WaitBias' ? 'bias' : criterion === 'NowcastError' ? 'minutes' : 'share';
}

/**
 * The wait ranges tracer runs are spread over (wiki 07 section 8: from short to long waits, so the tolerance rule of the
 * larger of 1 minute or 10 percent is tested at both ends; 20 minutes is the nowcast's cut). Upper bounds in minutes.
 */
export const waitRanges = [5, 10, 20, 30, Infinity] as const;

/** The range index (0 to 4) of a run's wait on Ariva's clock. */
export function waitRange(run: Pick<TracerRun, 'waitSeconds'>): number {
	const waited = run.waitSeconds / 60;
	const index = waitRanges.findIndex((upper) => waited < upper);
	return index < 0 ? waitRanges.length - 1 : index;
}

/** Runs per queue zone by wait range, the abandoned ones apart (they have no wait to compare). */
export interface ZoneSpread {
	zone: string;
	ranges: number[];
	abandoned: number;
	timed: number;
}

/** The spread of runs over the wait ranges, per zone (in the order given) and over every zone. */
export function spread(
	runs: readonly Pick<TracerRun, 'zoneName' | 'waitSeconds' | 'abandoned'>[],
	zones: readonly string[]
): { zones: ZoneSpread[]; total: ZoneSpread } {
	const empty = (zone: string): ZoneSpread => ({
		zone,
		ranges: waitRanges.map(() => 0),
		abandoned: 0,
		timed: 0
	});
	const byZone = new Map(zones.map((zone) => [zone, empty(zone)]));
	const total = empty('');
	for (const run of runs) {
		let row = byZone.get(run.zoneName);
		if (!row) {
			row = empty(run.zoneName);
			byZone.set(run.zoneName, row);
		}
		for (const target of [row, total]) {
			if (run.abandoned) target.abandoned++;
			else {
				target.ranges[waitRange(run)]++;
				target.timed++;
			}
		}
	}
	return { zones: [...byZone.values()], total };
}

/** A short, stable label for an observer's Ariva user id (never a name): its first 8 characters. */
export function observerLabel(id: string): string {
	return id.slice(0, 8);
}
