// ARV-152 (CWE-532): the shapes of the secrets Ariva issues or the E2E suite handles, in one place. scripts/artifact-secrets.mjs
// looks for them in the report, the test output and the logs; tests/support/expect-status.ts replaces them (redactSecrets)
// before it quotes an answer in a failure message, and so do the support helpers that throw on a wrong status. Formats from
// Platform/Backplane: DeviceCredentials and DisplayCredentials (ardk_, ardp_ and 43 base64url characters), IntegrationCredentials
// (ics_ and 43), TemporaryPasswords (20 characters of an alphabet without look-alikes, four groups of five), RecoveryCodes (ten
// Crockford base32 characters, XXXXX-XXXXX) and Totp (six digits; the server issues no other six-digit code).

/** The alphabet of a temporary password (TemporaryPasswords.cs): no i, l, o, I, O, 0 or 1. */
const TEMPORARY = 'a-hjkmnp-zA-HJ-NP-Z2-9';
/** Crockford base32 as RecoveryCodes.cs draws it: no I, L, O or U. */
const CROCKFORD = '0-9A-HJKMNP-TV-Z';
/** A real draw mixes classes; a run of four five-letter words or of letters only is prose, not a secret. */
const mixes = (text, classes) => classes.filter((c) => c.test(text)).length >= 2;
const temporaryPassword = new RegExp(`(?<![A-Za-z0-9_-])[${TEMPORARY}]{5}(?:-[${TEMPORARY}]{5}){3}(?![A-Za-z0-9_-])`, 'g');
const recoveryCode = new RegExp(`(?<![A-Za-z0-9_-])[${CROCKFORD}]{5}-[${CROCKFORD}]{5}(?![A-Za-z0-9_-])`, 'g');

/**
 * The labels of Ariva.Web's TOTP code fields (src/lib/i18n, en and ar: the sign-in, step-up and account fields, the setup's
 * confirmation), as an error context's ARIA snapshot names them. The scanner reads text as latin1, so the Arabic labels are
 * matched as their UTF-8 bytes.
 */
const CODE_LABEL_TEXTS = ['Authenticator code', 'Code from the app', '\u0631\u0645\u0632 \u062a\u0637\u0628\u064a\u0642 \u0627\u0644\u0645\u0635\u0627\u062f\u0642\u0629', '\u0627\u0644\u0631\u0645\u0632 \u0645\u0646 \u0627\u0644\u062a\u0637\u0628\u064a\u0642'];
const CODE_LABELS = CODE_LABEL_TEXTS
	.map((label) => Buffer.from(label, 'utf8').toString('latin1').replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
	.join('|');
/** The label of Ariva.Web's recovery code field at sign-in (auth.login.recoveryCode, en and ar), matched as the TOTP fields are. */
const RECOVERY_LABEL_TEXTS = ['Recovery code', '\u0631\u0645\u0632 \u0627\u0644\u0627\u0633\u062a\u0631\u062f\u0627\u062f'];
const RECOVERY_LABELS = RECOVERY_LABEL_TEXTS
	.map((label) => Buffer.from(label, 'utf8').toString('latin1').replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
	.join('|');

/** JSON members whose value is a secret (any letter case), as an answer or a trace writes them. */
export const SECRET_MEMBERS = [
	'password', 'newPassword', 'currentPassword', 'temporaryPassword', 'clientSecret', 'secret', 'totpSecret', 'totpUri', 'credential',
	'accessToken', 'refreshToken', 'token', 'previewToken', 'recoveryCode', 'recoveryCodes', 'apiKey', 'key', 'deviceKey', 'displayKey'
];
const members = SECRET_MEMBERS.join('|');

/**
 * The kinds the artifact check reports. `re` is matched on each text (latin1, so any byte sequence is searchable); with
 * `accept`, only the matches it accepts count.
 */
export const SHAPES = [
	{ kind: 'a device or display credential (ardk_, ardp_)', re: /(?<![A-Za-z0-9_-])ard[kp]_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])/ },
	{ kind: 'an integration client secret (ics_)', re: /(?<![A-Za-z0-9_-])ics_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])/ },
	{ kind: 'a JWT', re: /(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{16,}/ },
	// Token-like only (16 characters or more with a digit or a dot), so prose such as "Bearer authentication" is not one.
	{ kind: 'a bearer credential', re: /\bBearer\s+(?!\[redacted)(?=[A-Za-z0-9\-._~+/]*[0-9.])[A-Za-z0-9\-._~+/]{16,}/ },
	// Not a template's `${...}` (the HTML report keeps code snippets of the specs).
	{ kind: 'an access_token value', re: /access_token=(?!\[redacted\]|\$\{)[^&\s"'\\<>]{8,}/i },
	{ kind: 'a TOTP seed (otpauth URI)', re: /otpauth:\/\/[^\s"'<>]*?[?&](?:amp;)?secret=[A-Za-z2-7]{16,}/i },
	{ kind: 'a TOTP seed (totpSecret member)', re: /\\?"(?:totpSecret|secret)\\?"\s*:\s*\\?"[A-Z2-7]{16,}=*\\?"/ },
	{ kind: 'recovery codes', re: new RegExp(`\\\\?"recoveryCodes\\\\?"\\s*:\\s*\\[\\s*\\\\?"[${CROCKFORD}]{5}-?[${CROCKFORD}]{5}\\\\?"`) },
	// A recovery code on its own (an error context lists the shown codes): letters and digits mixed, as ten random draws are.
	{ kind: 'a recovery code', re: recoveryCode, accept: (m) => /[0-9]/.test(m) && /[A-Z]/.test(m) },
	{ kind: 'a temporary password', re: new RegExp(`\\\\?"temporaryPassword\\\\?"\\s*:\\s*\\\\?"[${TEMPORARY}]{5}(?:-[${TEMPORARY}]{5}){3}\\\\?"`) },
	// A temporary password on its own (a dialog that shows it, an input's value in an error context).
	{ kind: 'a temporary password', re: temporaryPassword, accept: (m) => mixes(m, [/[a-z]/, /[A-Z]/, /[2-9]/]) },
	{ kind: 'a TOTP code (a "code" member)', re: /\\?"(?:code|totpCode)\\?"\s*:\s*\\?"\d{6}\\?"/ },
	// A TOTP code field's value in an error context (Playwright's ARIA snapshot writes input values): `textbox "<label>" [ref=e9]: 493817`.
	{ kind: 'a TOTP code in a code field', re: new RegExp(`textbox "(?:${CODE_LABELS})"(?: \\[[^\\]\\n]*\\])*: \\d{6}(?!\\d)`) },
	// A recovery code typed into the sign-in's recovery field, any code (one in about fifty has no digit, which the shape on its own
	// above lets pass), with or without its hyphen, in either case.
	{ kind: 'a recovery code in a code field', re: new RegExp(`textbox "(?:${RECOVERY_LABELS})"(?: \\[[^\\]\\n]*\\])*: [${CROCKFORD}]{5}-?[${CROCKFORD}]{5}(?![A-Za-z0-9_-])`, 'i') },
	{ kind: 'a TOTP code typed into a code field', re: /\b(?:Fill|Type|Insert)\s+\\?"\d{6}\\?"[^\n]{0,80}?(?:[Aa]uthenticator|TOTP|totp|[Cc]ode from the app|step-up-code|account-code)/ }
];

/** Whether a shape matches the text (every match is offered to `accept` when the shape has one). */
export function shapeMatches(shape, text) {
	if (!shape.accept) return shape.re.test(text);
	const re = new RegExp(shape.re.source, shape.re.flags.includes('g') ? shape.re.flags : `${shape.re.flags}g`);
	for (const m of text.matchAll(re)) if (shape.accept(m[0])) return true;
	return false;
}

/** Replacements, in order; the JSON members first, so a member's whole value goes, then any shape left in free text. */
const REDACTIONS = [
	// "name": "value" (an escaped string inside a JSON string first, then a plain one), an array or an object.
	[new RegExp(`(\\\\")(${members})(\\\\"\\s*:\\s*)\\\\"(?:(?!\\\\").)*\\\\"`, 'gi'), '$1$2$3\\"[redacted]\\"'],
	[new RegExp(`(")(${members})("\\s*:\\s*)(?:"(?:[^"\\\\]|\\\\.)*"|\\[[^\\]]*\\]|\\{[^{}]*(?:\\{[^{}]*\\}[^{}]*)*\\})`, 'gi'), '$1$2$3"[redacted]"'],
	// name=value in a query, a fragment or a form body.
	[/((?:^|[?&#;\s"'])(?:access_token|token|password|passwd|secret|client_secret|key|api_key|apikey|credential)=)[^&\s"'<>#]+/gi, '$1[redacted]'],
	[/(?<![A-Za-z0-9_-])(ard[kp]_|ics_)[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])/g, '$1[redacted]'],
	[/\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*/g, '[redacted JWT]'],
	[/\b(Bearer\s+)(?!\[redacted)[A-Za-z0-9\-._~+/]{16,}=*/g, '$1[redacted]'],
	[/(otpauth:\/\/[^\s"'<>]*?[?&](?:amp;)?secret=)[^&\s"'<>]+/gi, '$1[redacted]'],
	[temporaryPassword, (m) => (mixes(m, [/[a-z]/, /[A-Z]/, /[2-9]/]) ? '[redacted]' : m)],
	[recoveryCode, (m) => (/[0-9]/.test(m) && /[A-Z]/.test(m) ? '[redacted]' : m)],
	[new RegExp(`(textbox "(?:${CODE_LABEL_TEXTS.join('|')}|${CODE_LABELS})"(?: \\[[^\\]\\n]*\\])*: )\\d{6}(?!\\d)`, 'g'), '$1[redacted]'],
	[new RegExp(`(textbox "(?:${RECOVERY_LABEL_TEXTS.join('|')}|${RECOVERY_LABELS})"(?: \\[[^\\]\\n]*\\])*: )[${CROCKFORD}]{5}-?[${CROCKFORD}]{5}(?![A-Za-z0-9_-])`, 'gi'), '$1[redacted]']
];

/** The text with every secret member, query value and credential shape replaced. */
export function redactSecrets(text) {
	return REDACTIONS.reduce((result, [shape, replacement]) => result.replace(shape, replacement), String(text));
}

/** Samples redactSecrets must clear (each holds the value after "|"), and text it must leave as it is. */
export const REDACTION_SAMPLES = {
	cleared: [
		'{"password":"|pw-Secret-123"}',
		'{"previewToken":"|preview-Token-321"}',
		'{\\"password\\":\\"|pw-Escaped-456\\"}',
		'{"temporaryPassword":"|abcde-FGHJK-mnpqr-23456"}',
		'{"credential":{"value":"|nested-value-789"}}',
		'{"recoveryCodes":["|7K2QH-M9XAB","4RTPZ-0WQ5C"]}',
		'/hubs/live?access_token=|eyJhbGciOiJ.eyJzdWIiOiJ4.c2ln',
		'userName=e2e&password=|form-Pass-321',
		'/display?code=E2E-1#key=|ardp_display-key-value',
		'issued ardk_|AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
		'secret ics_|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
		'Authorization: Bearer |sim-e2e-0123456789abcdefgh',
		'otpauth://totp/Ariva:x?secret=|JBSWY3DPEHPK3PXPJBSWY3DP&issuer=Ariva',
		'- textbox "Temporary password": |hjkmn-PQRST-uvwxy-23456',
		'- listitem: |7K2QH-M9XAB',
		'- textbox "Authenticator code" [ref=e9]: |493817',
		'- textbox "Recovery code" [ref=e9]: |WXYZK-MNPQR'
	],
	kept: ['{"code":"S-17","name":"A-VIS entry"}', 'status 409 from /api/v1/admin/airports', 'where-these-three-teens', 'ABCDE-FGHJK', '2026-10-20T06:32:43.4938178Z', 'AR-05 and E2E-CANARY', '3f2b8c1e-9d4a-4b7e-8f00-1a2b3c4d5e6f']
};
