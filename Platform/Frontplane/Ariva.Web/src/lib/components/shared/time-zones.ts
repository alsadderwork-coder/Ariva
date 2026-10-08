/** One choice in a time zone list: the IANA id stored by the API and a label with its current UTC offset. */
export interface TimeZoneOption {
	value: string;
	label: string;
}

/** Zones of one IANA area (Africa, America, Asia...), in id order. */
export interface TimeZoneGroup {
	region: string;
	zones: TimeZoneOption[];
}

let known: TimeZoneOption[] | undefined;

/** The zone's UTC offset now, as UTC+03:00; empty when the browser does not know the zone. */
export function utcOffset(zone: string, at = new Date()): string {
	try {
		const name =
			new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'longOffset' })
				.formatToParts(at)
				.find((p) => p.type === 'timeZoneName')?.value ?? '';
		return name === 'GMT' ? 'UTC+00:00' : name.replace('GMT', 'UTC');
	} catch {
		return '';
	}
}

function option(zone: string): TimeZoneOption {
	const offset = utcOffset(zone);
	return { value: zone, label: offset ? `${zone} (${offset})` : zone };
}

function region(zone: string): string {
	const slash = zone.indexOf('/');
	return slash > 0 ? zone.slice(0, slash) : zone;
}

/** Every IANA zone the browser knows, UTC first; built once per page load. */
function knownZones(): TimeZoneOption[] {
	if (!known) {
		const ids =
			typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [];
		known = [...new Set(ids)]
			.filter((id) => id !== 'UTC')
			.sort()
			.map(option);
		known.unshift(option('UTC'));
	}
	return known;
}

/**
 * The zones grouped by area. A current value the browser lists under another name (an alias such as Asia/Calcutta for
 * Asia/Kolkata) is kept as its own choice, so opening a stored airport never changes its zone; the API decides
 * whether a zone is valid.
 */
export function timeZoneGroups(current: string): TimeZoneGroup[] {
	const zones = knownZones();
	const all =
		current && !zones.some((z) => z.value === current) ? [option(current), ...zones] : zones;
	const groups = new Map<string, TimeZoneOption[]>();
	for (const zone of all) {
		const key = region(zone.value);
		const list = groups.get(key);
		if (list) list.push(zone);
		else groups.set(key, [zone]);
	}
	return [...groups].map(([name, list]) => ({ region: name, zones: list }));
}
