/** The SQL line comment token, built so that no double hyphen appears in the source text. */
const sqlComment = '-'.repeat(2);

/** SQL injection probes for query strings. Every route must answer them with a 4xx or a normal 2xx, never a 5xx. */
export const sqlInjectionPayloads: readonly string[] = [
	"' OR '1'='1",
	`1' OR 1=1${sqlComment}`,
	`1; DROP TABLE zones;${sqlComment}`,
	`' UNION SELECT NULL, version()${sqlComment}`,
	`1' AND pg_sleep(5)${sqlComment}`,
	"admin'/*",
	'%27%20OR%201%3D1'
];

/** Cross-site scripting probes for query strings, URL fragments and paths. */
export const xssPayloads: readonly string[] = [
	'<script>alert(1)</script>',
	'"><img src=x onerror=alert(1)>',
	"'><svg/onload=alert(1)>",
	'javascript:alert(1)',
	'<iframe srcdoc="<script>alert(1)</script>">',
	'{{constructor.constructor("alert(1)")()}}'
];

/** Markup from the payloads that must never appear unescaped in an API response. */
export const markupFragments: readonly string[] = ['<script>', '<img src=x', '<svg/onload', '<iframe'];

/**
 * Text from the payloads that must never appear in the rendered DOM. The DOM legitimately holds one inline
 * <script> (SvelteKit's bootstrap), so the browser checks look for the payload code instead of the tag.
 */
export const domMarkers: readonly string[] = ['alert(', 'onerror', 'onload', 'javascript:', 'constructor.constructor'];
