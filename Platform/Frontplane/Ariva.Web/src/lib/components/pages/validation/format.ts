/** Time formatting of the observer tablet (ARV-104c): site local time, Latin digits in both languages (as the reports screen). */

function tag(locale: string | null | undefined): string {
	return locale?.startsWith('ar') ? 'ar-u-nu-latn' : 'en-GB';
}

/** "14:45" in the site's time zone. */
export function siteClock(ms: number, timeZone: string, locale: string | null | undefined): string {
	return new Intl.DateTimeFormat(tag(locale), {
		timeZone,
		hour: '2-digit',
		minute: '2-digit',
		hourCycle: 'h23'
	}).format(new Date(ms));
}

/** "14:45:07" in the site's time zone. */
export function siteClockSeconds(
	ms: number,
	timeZone: string,
	locale: string | null | undefined
): string {
	return new Intl.DateTimeFormat(tag(locale), {
		timeZone,
		hour: '2-digit',
		minute: '2-digit',
		second: '2-digit',
		hourCycle: 'h23'
	}).format(new Date(ms));
}

/** "8 Oct, 14:45" in the site's time zone, for a bin or a run on another day. */
export function siteDayClock(
	ms: number,
	timeZone: string,
	locale: string | null | undefined
): string {
	return new Intl.DateTimeFormat(tag(locale), {
		timeZone,
		day: 'numeric',
		month: 'short',
		hour: '2-digit',
		minute: '2-digit',
		hourCycle: 'h23'
	}).format(new Date(ms));
}

/** A duration as "m:ss", or "h:mm:ss" from an hour. */
export function duration(ms: number): string {
	const total = Math.max(0, Math.floor(ms / 1000));
	const hours = Math.floor(total / 3600);
	const minutes = Math.floor((total % 3600) / 60);
	const seconds = String(total % 60).padStart(2, '0');
	return hours > 0
		? `${hours}:${String(minutes).padStart(2, '0')}:${seconds}`
		: `${minutes}:${seconds}`;
}

/** A clock offset in seconds with its sign and one decimal ("+0.4", "-12.0"). */
export function offsetSeconds(ms: number): { sign: string; seconds: string } {
	const seconds = (Math.abs(ms) / 1000).toFixed(1);
	// An offset that rounds to zero has no sign ("+0.0", never "-0.0").
	return { sign: ms < 0 && seconds !== '0.0' ? '-' : '+', seconds };
}
