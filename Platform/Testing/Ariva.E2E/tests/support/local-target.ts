// ARV-128 (TC-56): which targets count as local. The E2E suites write, attack and load, so they run only against hosts on
// this machine; a deployed environment gets the read-only smoke suite (tests/smoke, playwright.smoke.config.ts) instead.
//
// Local means the host part of the address, after WHATWG URL parsing (which also turns 2130706433 or 0x7f000001 into
// 127.0.0.1), is `localhost`, an IPv4 loopback address (127.0.0.0/8) or the IPv6 loopback `[::1]`. Anything else is
// non-local: another name (also `x.localhost`, `localhost.` or a name that resolves to loopback), 0.0.0.0, an IPv4-mapped
// IPv6 address, a private address, and an address that cannot be parsed. Erring towards non-local only ever refuses a run.

const loopbackV4 = /^127\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/;

/** Whether a host name, as URL.hostname gives it, is this machine's loopback. */
export function isLocalHostName(hostname: string): boolean {
	if (hostname === 'localhost' || hostname === '[::1]') return true;
	const v4 = loopbackV4.exec(hostname);
	return v4 !== null && v4.slice(1).every((part) => Number(part) <= 255);
}

/** Whether an address (http, https, ws, wss or redis) points at this machine; false for anything unparsable. */
export function isLocalUrl(value: string): boolean {
	let url: URL;
	try {
		url = new URL(value);
	} catch {
		return false;
	}
	if (!['http:', 'https:', 'ws:', 'wss:', 'redis:', 'rediss:'].includes(url.protocol)) return false;
	return isLocalHostName(url.hostname);
}

/** Whether a Kafka bootstrap list (`host:port[,host:port]`) names only this machine. */
export function isLocalBootstrap(value: string): boolean {
	const entries = value.split(',').map((entry) => entry.trim());
	return entries.length > 0 && entries.every((entry) => entry.length > 0 && isLocalUrl(`http://${entry}`));
}

/** Whether a bare host name or address (Database__Host) is this machine. */
export function isLocalHost(value: string): boolean {
	return isLocalUrl(`http://${value.includes(':') && !value.startsWith('[') ? `[${value}]` : value}`);
}

/**
 * The names (never the values, which can hold a password) of the targets that are set and not local. Each entry is a URL,
 * except the kinds named in `kinds`: `bootstrap` for a Kafka list, `host` for a bare host.
 */
export function nonLocalTargets(targets: Record<string, string | undefined>, kinds: Record<string, 'url' | 'bootstrap' | 'host'> = {}): string[] {
	return Object.entries(targets)
		.filter(([, value]) => value !== undefined && value !== '')
		.filter(([name, value]) => {
			const kind = kinds[name] ?? 'url';
			if (kind === 'bootstrap') return !isLocalBootstrap(value!);
			if (kind === 'host') return !isLocalHost(value!);
			return !isLocalUrl(value!);
		})
		.map(([name]) => name);
}
