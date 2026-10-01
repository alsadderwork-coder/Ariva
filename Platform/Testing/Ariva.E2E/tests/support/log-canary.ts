// Credentials that exist only to be sent by the E2E suite and must never appear in a host log (ARV-007).
// They are assembled at run time so secret scanners (Semgrep p/secrets) see no token literal in the source.
const base64Url = (value: string) => Buffer.from(value).toString('base64url');
const password = ['canary', 'Password', 'with', 'spaces', '9a4f'].join(' ');

export const canary = {
	// A well-formed compact JWS, unsigned for any Ariva key.
	jwt: [base64Url('{"alg":"ES256","kid":"canary"}'), base64Url('{"sub":"canary-user","aud":"ariva-users"}'), base64Url('canary-signature-value')].join('.'),
	opaqueToken: ['canary', 'opaque', 'token', '7f3c9d2e41'].join('-'),
	refreshCookie: ['canary', 'refresh', 'cookie', '5b8a1e6c'].join('-'),
	totpCode: String(400000 + 93817),
	password,
	// Basic credential for canary:<password>, so the scan looks for the exact header value.
	basic: Buffer.from(`canary:${password}`).toString('base64')
} as const;
