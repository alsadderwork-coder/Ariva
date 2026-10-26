#!/usr/bin/env node
// ARV-152 (CWE-532, ASVS V13.3.1): no E2E spec puts an HTTP answer's body or a secret where the report keeps it.
// Playwright uses an expect's custom message as the step title even when the assertion passes, and the HTML report keeps
// every step title, so `expect(x.status(), await x.text())` copies each answer into the report (the live rehearsal's sensor
// registration put 20 device credentials there). A spec checks a status with expectStatus or expectStatusIn
// (tests/support/expect-status.ts), which puts the body into the message only when the status is wrong, and redacted.
//
//   node scripts/expect-messages.mjs               check every .ts file under tests/ and the Playwright configs; exit 1 on a finding
//   node scripts/expect-messages.mjs --self-test   prove the check fires on every bad pattern and stays quiet on the helper
//   node scripts/expect-messages.mjs <files...>    check only these files
//
// Where the report keeps text: the message of expect, expect.soft and expect.poll, a test.step title, an annotation, an
// attachment, console output, and the URL of a request context's call or a navigation (a step's subtitle, query included).
// What may not go there: a `.text()`, `.json()` or `.body()` call; a `.body` property (the raw answers of the streaming and
// raw socket clients); a secret-named member or variable (credential, clientSecret, totpSecret, temporaryPassword,
// password, secret, accessToken, refreshToken, recoveryCodes, apiKey, deviceKey, displayKey, jwt); an answer's `.url()`
// (its query may carry a token: whereOf in tests/support/api-assertions.ts leaves it out); or a variable whose
// latest binding holds one of those, one step removed included (a template or a `+` concatenation that carries it). A
// request URL may not carry access_token=, token=, key=, secret=, password= or a token, credential, secret or key in its
// query: send those in a header, or through Node's fetch (`call` in tests/support/accounts.ts), which records no step.
// ARV-249: a failing matcher prints its subject and its expected value, so an expect's subject and its matcher's arguments
// may not be a secret either (a secret-named member or variable, a variable holding one, a part, an element or an encoding
// of one, a template or concatenation that carries one, a call named for one such as `response.refreshCookie()`, a variable
// bound to a process.env key, secret, password, token or seed, or the text of an element that reveals one: `credential-value`,
// `totp-secret`, `recovery-codes`): assert a boolean, a count or a shape instead
// (`expect(text.includes(secret)).toBe(false)`, `expect(codes.length).toBe(10)`, `expect(/^ics_/.test(secret)).toBe(true)`).
// toBeTruthy, toBeDefined, not.toBeNull, not.toBeUndefined and not.toBeFalsy print nothing of a secret subject and stay allowed;
// expect.poll's subject is a function, so only its matcher's arguments are read. scripts/security/secret-assertions.mjs holds
// the C# tests to the same rule. The check reads code only: comments, strings and the static parts of template literals are
// masked first.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { SHAPES, shapeMatches } from './secret-shapes.mjs';

const E2E = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
/** The helper itself builds the failure message from the body; it is the one place allowed to. */
const HELPER = path.join(E2E, 'tests', 'support', 'expect-status.ts');
/**
 * Files the check leaves out, each for its reason: the helper; and the smoke suite's leak probe (ARV-128), which writes a
 * made-up password into its report on purpose, in a child run against a local target only whose folder is removed
 * afterwards, to prove the smoke artifact reporter fails such a run.
 */
const EXEMPT = new Set([HELPER, path.join(E2E, 'tests', 'smoke', 'probes', 'leak.probe.ts')]);
/**
 * Files whose source lines may hold a secret's shape: global teardown runs outside every test, so no step's snippet ever
 * shows its lines, and its cases check the log scan's own matching rule on a made-up TOTP code (`{"code":"493817"}`).
 */
const SHAPES_EXEMPT = new Set([path.join(E2E, 'tests', 'support', 'global-teardown.ts')]);

/**
 * The source with comments, string contents, the static parts of template literals and regular expression bodies replaced
 * by spaces (line breaks kept), so brackets and names are code only and every offset still points at the original.
 */
export function mask(source) {
	const out = source.split('');
	const n = source.length;
	const blank = (from, to) => {
		for (let k = from; k < to && k < n; k++) if (out[k] !== '\n' && out[k] !== '\r') out[k] = ' ';
	};
	const KEYWORDS_BEFORE_REGEX = new Set(['return', 'typeof', 'case', 'in', 'of', 'delete', 'void', 'throw', 'new', 'await', 'yield', 'else', 'do']);
	const regexAllowed = (at) => {
		let k = at - 1;
		while (k >= 0 && /\s/.test(out[k])) k--;
		if (k < 0) return true;
		const c = out[k];
		if (/[\w$]/.test(c)) {
			let s = k;
			while (s > 0 && /[\w$]/.test(out[s - 1])) s--;
			return KEYWORDS_BEFORE_REGEX.has(out.slice(s, k + 1).join(''));
		}
		return !/[)\]}'"`]/.test(c);
	};
	// Scans code from i; with inTemplate, stops after the `}` that closes a template's `${`.
	const code = (start, inTemplate) => {
		let i = start;
		let depth = 0;
		while (i < n) {
			const c = source[i];
			const d = source[i + 1];
			if (c === '/' && d === '/') {
				const e = source.indexOf('\n', i);
				const end = e < 0 ? n : e;
				blank(i, end);
				i = end;
			} else if (c === '/' && d === '*') {
				const e = source.indexOf('*/', i + 2);
				const end = e < 0 ? n : e + 2;
				blank(i, end);
				i = end;
			} else if (c === "'" || c === '"') {
				let j = i + 1;
				while (j < n && source[j] !== c && source[j] !== '\n') j += source[j] === '\\' ? 2 : 1;
				blank(i + 1, j);
				i = j + 1;
			} else if (c === '`') {
				i = template(i + 1);
			} else if (c === '/' && regexAllowed(i)) {
				let j = i + 1;
				let inClass = false;
				while (j < n && source[j] !== '\n') {
					const r = source[j];
					if (r === '\\') j += 2;
					else if (r === '[') (inClass = true), j++;
					else if (r === ']') (inClass = false), j++;
					else if (r === '/' && !inClass) break;
					else j++;
				}
				if (j < n && source[j] === '/') {
					blank(i + 1, j);
					i = j + 1;
					while (i < n && /[a-z]/i.test(source[i])) i++;
				} else i++; // not a regular expression after all: a division
			} else if (c === '{') {
				depth++;
				i++;
			} else if (c === '}') {
				if (inTemplate && depth === 0) return i + 1;
				depth--;
				i++;
			} else i++;
		}
		return n;
	};
	// Scans a template literal's text from just after its opening backtick; returns the offset after its closing one.
	const template = (start) => {
		let i = start;
		while (i < n) {
			const c = source[i];
			if (c === '\\') {
				blank(i, i + 2);
				i += 2;
			} else if (c === '`') return i + 1;
			else if (c === '$' && source[i + 1] === '{') i = code(i + 2, true);
			else {
				blank(i, i + 1);
				i++;
			}
		}
		return n;
	};
	code(0, false);
	return out.join('');
}

/** The arguments of the call whose `(` is at `open` in masked text: [start, end) offsets, split at top-level commas. */
function callArguments(masked, open) {
	const args = [];
	let depth = 0;
	let from = open + 1;
	for (let i = open; i < masked.length; i++) {
		const c = masked[i];
		if (c === '(' || c === '[' || c === '{') depth++;
		else if (c === ')' || c === ']' || c === '}') {
			depth--;
			if (depth === 0) {
				if (masked.slice(from, i).trim()) args.push([from, i]);
				return args;
			}
		} else if (c === ',' && depth === 1) {
			args.push([from, i]);
			from = i + 1;
		}
	}
	return args;
}

const READS_BODY = /\.\s*(text|json|body)\s*\(\s*\)/;
const READS_BODY_ALL = /\.\s*(?:text|json|body)\s*\(\s*\)/g;
const BODY_PROPERTY = /\.\s*body\b(?!\s*\()/;
const IDENTIFIER = /^[A-Za-z_$][\w$]*$/;
/** Members and names that hold a secret: an answer's credential, a client secret, a TOTP seed, a password, a token, a key. */
const SECRET_NAMES = 'credential|credentials|clientSecret|totpSecret|totpSeed|totpUri|temporaryPassword|password|newPassword|currentPassword|secret|token|accessToken|refreshToken|previewToken|recoveryCodes|recoveryCode|apiKey|deviceKey|displayKey|privateKey|jwt';
const SECRET_MEMBER = new RegExp(`\\.\\s*(?:${SECRET_NAMES})\\b(?!\\s*\\()|\\bprocess\\.env\\.\\w*(?:KEY|SECRET|PASSWORD|TOKEN|SEED)\\w*`);
const SECRET_NAME = new RegExp(`^(?:${SECRET_NAMES})$|[a-z](?:Password|Secret|Credential|Token)$`);
/** Query parameters that carry a credential, and expressions that name one, in a request's URL. */
const SECRET_PARAM = /^(?:access_token|token|key|apikey|api_key|secret|client_secret|password|passwd|credential|jwt|sig|signature)$/i;
const SECRET_EXPRESSION = /[A-Za-z]*(?:[Tt]oken|[Jj]wt|[Ss]ecret|[Pp]assword|[Cc]redential)[A-Za-z]*|\b(?:apiKey|deviceKey|displayKey|key)\b/;

/**
 * Every binding of a name in the masked source, in source order, and what it holds. A body: a value that reads an answer
 * (`.text()`, `.json()` or `.body()`) anywhere, unless every read is only the start of a field (`(await r.json()).id`, a
 * value of its own); or a value that puts a body-holding name into a template (`${}`) or a `+` concatenation, passes it
 * through JSON.parse or names it alone. A secret: a value that reads a secret-named member (`.credential`, `.password`) or
 * holds a secret-holding name the same ways. A loop variable holds neither.
 */
export function bindings(masked) {
	const list = [];
	// `name = <rest of the line>` (a declaration or a plain assignment), destructuring declarations and loop variables.
	for (const m of masked.matchAll(/(?<![\w$.])([A-Za-z_$][\w$]*)\s*(?::\s*[^=;\n]+?)?(?:\|\||\?\?|&&)?=(?![=>])([^;\n]*)/g)) list.push({ names: [m[1]], at: m.index, value: m[2], valueAt: m.index + m[0].length - m[2].length });
	for (const m of masked.matchAll(/\b(?:const|let|var)\s*([{[][^=;]*?[}\]])\s*(?::\s*[^=;\n]+?)?=(?![=>])([^;\n]*)/g)) {
		const names = [...m[1].matchAll(/([A-Za-z_$][\w$]*)\s*(?=[,}\]=]|$)/g)].map((x) => x[1]);
		list.push({ names, at: m.index, value: m[2], valueAt: m.index + m[0].length - m[2].length });
	}
	for (const m of masked.matchAll(/\bfor\s*\(\s*(?:const|let|var)\s+([^;]*?)\s+(?:of|in)\b/g))
		list.push({ names: [...m[1].matchAll(/[A-Za-z_$][\w$]*/g)].map((x) => x[0]), at: m.index, value: '', valueAt: m.index });
	list.sort((a, b) => a.at - b.at);
	const entries = list.flatMap(({ names, at, value, valueAt }) =>
		names.filter((name) => IDENTIFIER.test(name)).map((name) => ({ name, at, value, valueAt, body: false, secret: false }))
	);
	for (let changed = true; changed; ) {
		changed = false;
		for (const entry of entries) {
			const value = entry.value;
			if (!entry.body && (readsWholeBody(value) || carries(value, entry.at, entries, 'body'))) (entry.body = true), (changed = true);
			if (!entry.secret && (SECRET_MEMBER.test(value) || carries(value, entry.at, entries, 'secret'))) (entry.secret = true), (changed = true);
		}
	}
	return entries;
}

/** A read of an answer that is not only the start of a field: `.json()` not followed by `.x`, `?.x` or `[` (then, catch and finally are the promise). */
function readsWholeBody(value) {
	for (const m of value.matchAll(READS_BODY_ALL)) {
		const after = value.slice(m.index + m[0].length).replace(/^[\s)]*/, '');
		if (!/^(?:\?\.|\.|\[)/.test(after) || /^\.\s*(?:then|catch|finally)\b/.test(after)) return true;
	}
	return false;
}

/** Whether the value puts a name holding `what` (body or secret) into a template, a concatenation, JSON.parse, or is that name alone. */
function carries(value, offset, entries, what) {
	const named = (text) => [...text.matchAll(/(?<![\w$.])([A-Za-z_$][\w$]*)(?![\w$])(?!\s*:)/g)].some((m) => holds(entries, m[1], offset, what));
	const alone = /^\s*(?:await\s+)?\(?\s*([A-Za-z_$][\w$]*)\s*\)?\s*(?:as\b[^;]*)?$/.exec(value)?.[1] ?? /\bJSON\.parse\s*\(\s*([A-Za-z_$][\w$]*)\s*\)\s*(?:as\b[^;]*)?$/.exec(value)?.[1];
	if (alone && holds(entries, alone, offset, what)) return true;
	for (const m of value.matchAll(/\$\{([^}]*)\}/g)) if (named(m[1])) return true;
	return /(?<![+])\+(?![+=])/.test(value) && named(value);
}

/** Whether the latest binding of the name before the offset holds `what` (body or secret). */
function holds(entries, name, offset, what) {
	let latest;
	for (const entry of entries) if (entry.name === name && entry.at < offset) latest = entry;
	return latest?.[what] === true;
}

/** Splits text at top-level occurrences of a sticky (/y) separator pattern, outside brackets. */
function splitTop(text, separator) {
	const parts = [];
	let depth = 0;
	let from = 0;
	for (let i = 0; i < text.length; i++) {
		const c = text[i];
		if ('([{'.includes(c)) depth++;
		else if (')]}'.includes(c)) depth--;
		else if (depth === 0) {
			separator.lastIndex = i;
			const m = separator.exec(text);
			if (m) {
				parts.push(text.slice(from, i));
				from = i + m[0].length;
				i = from - 1;
			}
		}
	}
	parts.push(text.slice(from));
	return parts;
}

/** The offset of the bracket that opens the one that closes at `close`, or -1. */
function opening(text, close) {
	let depth = 0;
	for (let i = close; i >= 0; i--) {
		if (')]}'.includes(text[i])) depth++;
		else if ('([{'.includes(text[i]) && --depth === 0) return i;
	}
	return -1;
}

/** Calls whose result is their receiver's text, or a part or an element of it; and functions that pass their argument on. */
const PASSES_ON = new Set(['slice', 'substring', 'substr', 'trim', 'trimStart', 'trimEnd', 'toLowerCase', 'toUpperCase', 'toString', 'at', 'padStart', 'padEnd', 'replace', 'replaceAll', 'normalize', 'split', 'filter', 'sort', 'toSorted', 'reverse', 'find', 'findLast', 'flat', 'valueOf']);
const PASSES_ARGUMENT = new Set(['encodeURIComponent', 'encodeURI', 'decodeURIComponent', 'decodeURI', 'String', 'concat']);
/**
 * Where a binding's value (its source text) holds a secret whatever its name: a process.env key that names a key, a secret,
 * a password, a token or a seed, and the text of an element that shows one (a credential or a temporary password the
 * screen reveals once, a TOTP seed, recovery codes).
 */
const SECRET_SOURCE = /\bprocess\.env\.\w*(?:KEY|SECRET|PASSWORD|TOKEN|SEED)\w*|getByTestId\(\s*['"](?:credential-value|credential-reveal|totp-secret|recovery-codes)['"]\s*\)[\s\S]*?\.(?:textContent|innerText|inputValue|allTextContents|allInnerTexts)\s*\(/;
/** An element that reveals a secret, as a locator (`page.getByTestId('credential-value')`, or a variable bound to one). */
const SECRET_ELEMENT = /^\s*(?:[\w$.]+\s*\.\s*)?getByTestId\(\s*['"](?:credential-value|credential-reveal|totp-secret|recovery-codes)['"]\s*\)\s*;?\s*$/;
/** Web-first matchers that print the element's text or value when they fail. */
const TEXT_MATCHERS = new Set(['toHaveText', 'toContainText', 'toHaveValue', 'toHaveValues', 'toHaveAccessibleName', 'toHaveAccessibleDescription', 'toMatchAriaSnapshot', 'not.toHaveText', 'not.toContainText', 'not.toHaveValue']);
/** Calls whose result is a secret by their name (an answer's refresh cookie, a token, a TOTP code). */
const SECRET_CALL = /(?:[Tt]oken|[Ss]ecret|[Pp]assword|[Cc]redential|[Cc]ookie|[Jj]wt)$|^(?:totp|otp)Code$/i;
/** Matchers that print nothing of a secret subject: they fail only on a missing or falsy value (`not` as given). */
const PRESENCE = new Set(['toBeTruthy', 'toBeDefined', 'not.toBeNull', 'not.toBeUndefined', 'not.toBeFalsy', 'not.toBeNaN']);

/**
 * Whether the value of an expression (masked text at the offset) is a secret, what a failing matcher prints: a secret-named
 * member or variable (secretMember), a variable whose latest binding is one, an element, part or encoding of one, or a
 * template, a concatenation, a `??`, `||` or `?:` that carries one. A comparison, `typeof`, `.length`, a `.test()` and any
 * other call are values of their own.
 */
export function secretValue(expression, offset, entries, depth = 0) {
	if (depth > 12) return false;
	let e = expression.trim();
	for (;;) {
		const before = e;
		e = e.replace(/^await\s+/, '').replace(/!+$/, '').replace(/\s+as\s+[\w$.<>[\]|,\s{}:?'"]+$/, '').replace(/\s+satisfies\s+[\w$.<>[\]|,\s]+$/, '').trim();
		if (e.startsWith('(') && closing(e, 0) === e.length - 1) e = e.slice(1, -1).trim();
		if (e === before) break;
	}
	if (!e || /^(?:typeof|void|delete)\b/.test(e) || /^!(?!=)/.test(e)) return false;
	if (splitTop(e, /===|!==|==|!=|<=|>=|&&|=>|\binstanceof\b|\bin\b|\s<\s|\s>\s/y).length > 1) return false;
	for (const separator of [/\?\?|\|\|/y, /\?(?![.[(])|:/y, /\+(?![+=])/y]) {
		const parts = splitTop(e, separator);
		if (parts.length > 1) return parts.some((part) => secretValue(part, offset, entries, depth + 1));
	}
	if (e.startsWith('`')) return [...e.matchAll(/\$\{/g)].some((m) => {
		const close = closing(e, m.index + 1);
		return close > 0 && secretValue(e.slice(m.index + 2, close), offset, entries, depth + 1);
	});
	if (/^['"]/.test(e) || /^[\d.]/.test(e) || /^\/.*\/[a-z]*$/s.test(e)) return false;
	if (e.startsWith('[') || e.startsWith('{')) {
		// An array or an object literal: printed whole, so a secret element or member value counts.
		const close = closing(e, 0);
		if (close === e.length - 1) return splitTop(e.slice(1, -1), /,/y).some((item) => secretValue(item.replace(/^\s*\.\.\./, '').replace(/^\s*[\w$'"]+\s*:(?!:)/, ''), offset, entries, depth + 1));
	}
	const last = e[e.length - 1];
	if (last === ']') {
		const open = opening(e, e.length - 1);
		return open > 0 && secretValue(e.slice(0, open).replace(/\?\.$/, ''), offset, entries, depth + 1);
	}
	if (last === ')') {
		const open = opening(e, e.length - 1);
		if (open <= 0) return false;
		const callee = e.slice(0, open).replace(/\?\.$/, '').replace(/<[^<>()]*>$/, '').trim();
		const m = /^(?:(.*?)\s*\??\.\s*)?([A-Za-z_$][\w$]*)$/s.exec(callee);
		if (!m) return false;
		const [, receiver, name] = m;
		const args = callArguments(e, open).map((at) => e.slice(at[0], at[1]));
		if (SECRET_CALL.test(name)) return true; // response.refreshCookie(), totpCode(seed)
		if (PASSES_ARGUMENT.has(name) && args.some((arg) => secretValue(arg, offset, entries, depth + 1))) return true;
		if (PASSES_ON.has(name) || name === 'concat') return receiver !== undefined && secretValue(receiver, offset, entries, depth + 1);
		return false;
	}
	const member = /\.\s*([A-Za-z_$][\w$]*)$/.exec(e);
	if (member) return secretMember(member[1]);
	if (!IDENTIFIER.test(e)) return false;
	if (SECRET_NAME.test(e)) return true;
	let latest;
	for (const entry of entries) if (entry.name === e && entry.at < offset) latest = entry;
	if (latest === undefined || latest.value.trim() === '') return false;
	return SECRET_SOURCE.test(latest.raw ?? '') || secretValue(latest.value, latest.at, entries, depth + 1);
}

/** Whether a locator (source text and masked text at the offset) is an element that reveals a secret, or a variable bound to one. */
function revealsSecret(raw, masked, offset, entries) {
	if (SECRET_ELEMENT.test(raw)) return true;
	const name = masked.trim();
	if (!IDENTIFIER.test(name)) return false;
	let latest;
	for (const entry of entries) if (entry.name === name && entry.at < offset) latest = entry;
	return latest !== undefined && SECRET_ELEMENT.test(latest.raw ?? '');
}

/** Whether a member name holds a secret (the names SECRET_MEMBER reads, a compound ending in Password, Secret, Credential or Token). */
function secretMember(name) {
	if (/^(?:must|require|requires|has|is|can|invalid|missing)[A-Z]/.test(name)) return false; // mustChangePassword: a flag
	if (name === 'authorization' || name === 'cookie') return true; // a request's headers: a bearer token, a session cookie
	return new RegExp(`^(?:${SECRET_NAMES})$`).test(name) || /[a-z](?:Password|Secret|Credential|Token|Jwt)$/.test(name);
}

/** Why a message, a title, an annotation, an attachment or a console argument (masked text at the offset) quotes an answer or a secret. */
function quotes(message, offset, entries) {
	if (READS_BODY.test(message)) return 'reads an answer body (.text(), .json() or .body())';
	if (BODY_PROPERTY.test(message)) return 'quotes a .body property';
	if (/\.\s*url\s*\(\s*\)/.test(message)) return 'quotes a URL with its query (.url(); whereOf in tests/support/api-assertions.ts leaves the query out)';
	if (SECRET_MEMBER.test(message)) return `quotes a secret member (${SECRET_MEMBER.exec(message)[0].replace(/^\.\s*/, '.')})`;
	for (const m of message.matchAll(/(?<![\w$.])([A-Za-z_$][\w$]*)(?![\w$])(?!\s*:)/g)) {
		if (holds(entries, m[1], offset, 'body')) return `quotes ${m[1]}, which holds an answer body`;
		if (holds(entries, m[1], offset, 'secret')) return `quotes ${m[1]}, which holds a secret`;
		if (SECRET_NAME.test(m[1])) return `quotes ${m[1]}, a secret by its name`;
	}
	return undefined;
}

/** An address with its origin: http(s), or an origin-like variable first (webUrl, hosts.main, host.url, target.base, base). */
const ORIGIN = /^(?:['"`]https?:|`\$\{\s*(?:[\w$]+\s*\.\s*)*[\w$]*(?:[Uu]rl|[Hh]osts?|[Oo]rigin|[Bb]ase)\w*\s*\})/;
const ORIGIN_NAME = /^\s*(?:[\w$]+\s*\.\s*)*[\w$]*(?:[Uu]rl|[Hh]osts?|[Oo]rigin|[Bb]ase)\w*\s*\+/;

/**
 * Why an address (the original text of a request's or a navigation's first argument, the bindings it names and the object
 * members it reads) carries a secret where the report keeps it, or undefined. A request context's call is a report step
 * whose subtitle is the URL with its query; so is a navigation's. Playwright leaves the fragment out only when the address
 * parses on its own (an absolute URL): a relative one is kept whole, fragment included. So the query counts always, the
 * fragment when the literal has no origin. A secret parameter (access_token=, token=, key=, secret=, password=) counts
 * whatever its value is, a literal included; any parameter counts when its value names a token, credential, secret or key.
 */
function urlCarries(candidates, offset, entries) {
	for (const candidate of candidates) {
		const text = candidate.trim();
		for (const m of text.matchAll(/`(?:[^`\\]|\\.)*`|'(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"/g)) {
			const literal = m[0];
			const continued = /^['"`][?&#]/.test(literal);
			const absolute = ORIGIN.test(literal) || (continued && (ORIGIN.test(text) || ORIGIN_NAME.test(text)));
			const start = literal.search(absolute ? /[?]/ : /[?#]/);
			if (start < 0 && !continued) continue;
			let part = literal.slice(Math.max(start, 0));
			if (absolute) part = part.split('#')[0];
			const rest = text.slice(m.index + literal.length);
			for (const p of `${part}${/^\s*\+/.test(rest) ? rest : ''}`.matchAll(/(?:^['"`]?|[?&#])([A-Za-z_][\w-]*)=(?:\$\{([^}]*)\}|['"`]\s*\+\s*([^,)]+)|([^&#'"`$]*))/g)) {
				if (SECRET_PARAM.test(p[1])) return `sends ${p[1]}= in its address`;
				const expression = p[2] ?? p[3] ?? '';
				if (expression && (SECRET_EXPRESSION.test(expression) || quotes(expression, offset, entries))) return `puts ${expression.trim().slice(0, 40)} in its address`;
			}
			// A secret after the ? or # with no parameter name of its own (`#${display.credential}`).
			for (const e of part.matchAll(/\$\{([^}]*)\}/g)) {
				if (SECRET_EXPRESSION.test(e[1]) || quotes(e[1], offset, entries)) return `puts ${e[1].trim().slice(0, 40)} in its address`;
			}
		}
	}
	return undefined;
}

/** The offset of the bracket that closes the one at `open` in masked text, or -1. */
function closing(masked, open) {
	let depth = 0;
	for (let i = open; i < masked.length; i++) {
		if ('([{'.includes(masked[i])) depth++;
		else if (')]}'.includes(masked[i]) && --depth === 0) return i;
	}
	return -1;
}

/** The body of a function declared in the file under this name (`function name(` or `async function name(`), or ''. */
function functionBody(source, masked, name) {
	const m = new RegExp(`\\bfunction\\s+${name.replace(/\$/g, '\\$')}\\s*(?:<[^>]*>)?\\(`).exec(masked);
	if (!m) return '';
	const params = closing(masked, m.index + m[0].length - 1);
	const open = params < 0 ? -1 : masked.indexOf('{', params);
	const close = open < 0 ? -1 : closing(masked, open);
	return close < 0 ? '' : source.slice(open, close + 1);
}

/**
 * The texts an address argument may come from: itself, the latest binding of the name it starts with (and the body of the
 * function that binding calls), and every definition of the member it ends with.
 */
function addressCandidates(source, masked, at, entries, support = []) {
	const expression = masked.slice(at[0], at[1]);
	const candidates = [source.slice(at[0], at[1])];
	const called = (text) => /^\s*(?:await\s+)?([A-Za-z_$][\w$]*)\s*(?:<[^>]*>)?\(/.exec(text)?.[1];
	const first = /^\s*([A-Za-z_$][\w$]*)/.exec(expression)?.[1];
	if (first) {
		let latest;
		for (const entry of entries) if (entry.name === first && entry.at < at[0]) latest = entry;
		if (latest) candidates.push(source.slice(latest.valueAt, latest.valueAt + latest.value.length));
		const call = called(expression) ?? (latest ? called(latest.value) : undefined);
		if (call) candidates.push(functionBody(source, masked, call));
	}
	const member = /\.\s*([A-Za-z_$][\w$]*)\s*$/.exec(expression)?.[1];
	if (member) {
		// Definitions of the member in this file, else in every support file (a row's url built by a helper): each is checked.
		const definitions = (text, maskedText) => {
			const found = [];
			for (const m of maskedText.matchAll(new RegExp(`(?<![\\w$.])${member.replace(/\$/g, '\\$')}\\s*:(?!:)`, 'g'))) {
				const from = m.index + m[0].length;
				const line = maskedText.indexOf('\n', from);
				const args = callArguments(`(${maskedText.slice(from, line < 0 ? maskedText.length : line)})`, 0);
				const end = from + (args[0] ? args[0][1] - 1 : 0);
				found.push(text.slice(from, end));
			}
			return found;
		};
		const found = definitions(source, masked);
		if (found.length === 0) for (const file of support) found.push(...definitions(file.source, file.masked));
		candidates.push(...found);
	}
	return { candidates: candidates.filter(Boolean), resolved: candidates.some((c, i) => i > 0 && /['"`]/.test(c)) || /^\s*['"`]/.test(expression) };
}

/** Findings in one file's source: "line: what", for every place a report would keep an answer or a secret. */
export function findings(source, support = []) {
	const masked = mask(source);
	const entries = bindings(masked);
	for (const entry of entries) entry.raw = source.slice(entry.valueAt, entry.valueAt + entry.value.length);
	const lineOf = (offset) => source.slice(0, offset).split('\n').length;
	const result = [];
	const report = (at, what, reason) => result.push({ line: lineOf(at[0]), reason: `${what} ${reason}`, text: source.slice(at[0], at[1]).trim().slice(0, 120) });
	// Expect messages, step titles, annotations, attachments and console output.
	const sites = [
		[/(?<![\w$.])expect(?:\s*\.\s*(?:soft|poll))?\s*\(/g, 'expect message', (args) => args.slice(1, 2)],
		[/(?<![\w$.])test\s*\.\s*step\s*\(/g, 'step title', (args) => args.slice(0, 1)],
		[/\bannotations\s*\.\s*push\s*\(/g, 'annotation', (args) => args],
		[/(?<![\w$])(?:test\s*\.\s*info\s*\(\s*\)|testInfo|info)\s*\.\s*attach\s*\(/g, 'attachment', (args) => args],
		[/(?<![\w$.])console\s*\.\s*(?:log|info|warn|error|debug)\s*\(/g, 'console output', (args) => args],
		// The status helper hands its message to expect when the status is right, so it is a step title too.
		[/(?<![\w$.])expectStatus(?:In)?\s*\(/g, 'expectStatus message', (args) => args.slice(2, 3)]
	];
	for (const [re, what, pick] of sites) {
		for (const m of masked.matchAll(re)) {
			for (const at of pick(callArguments(masked, m.index + m[0].length - 1))) {
				const reason = quotes(masked.slice(at[0], at[1]), at[0], entries);
				if (reason) report(at, what, reason);
			}
		}
	}
	// Request contexts' calls and navigations: the URL is in the report.
	for (const m of masked.matchAll(/(?<![\w$])((?:[\w$]+\s*\.\s*)*(?:request|[A-Za-z]+Request|requestContext|apiContext))\s*\.\s*(get|post|put|patch|delete|head|fetch)\s*\(|(?<=[\w$\])!])\s*\??\.\s*goto\s*\(/g)) {
		const at = callArguments(masked, m.index + m[0].length - 1)[0];
		if (!at) continue;
		const { candidates, resolved } = addressCandidates(source, masked, at, entries, support);
		const what = m[2] ? `request URL (${m[2]})` : 'navigation URL';
		const reason = urlCarries(candidates, at[0], entries);
		if (reason) report(at, what, reason);
		// A member or a call the check cannot read is flagged: its address could carry anything (a display's key).
		else if (!resolved && /[.(]/.test(masked.slice(at[0], at[1]).trim())) report(at, what, 'comes from a member or a call the check cannot read: build the address in a variable or a literal');
	}
	// Typed values: a fill's or a type's step title is `Fill "<value>"` (fillSecret in tests/support/web-auth.ts types a secret without one).
	for (const m of masked.matchAll(/\.\s*(fill|type|pressSequentially|insertText)\s*\(/g)) {
		const at = callArguments(masked, m.index + m[0].length - 1)[0];
		if (!at) continue;
		const reason = quotes(masked.slice(at[0], at[1]), at[0], entries);
		if (reason) report(at, `typed value (${m[1]})`, `${reason}: type it with fillSecret`);
	}
	// ARV-249: a failing matcher prints its subject and its expected value (`Expected: "..."`, `Received string: "..."`), and the
	// report keeps the failure: a secret is asserted as a boolean, a count or a shape (`expect(text.includes(secret)).toBe(false)`,
	// `expect(token.length).toBe(43)`, `expect(/^ics_/.test(secret)).toBe(true)`), never by value. expect.poll's subject is a
	// function and its matcher's argument a value: the argument is checked.
	for (const m of masked.matchAll(/(?<![\w$.])expect(\s*\.\s*(?:soft|poll))?\s*\(/g)) {
		const open = m.index + m[0].length - 1;
		const close = closing(masked, open);
		const subject = callArguments(masked, open)[0];
		if (close < 0 || !subject) continue;
		const chain = /^\s*((?:\.\s*(?:not|resolves|rejects)\s*)*)\.\s*(\w+)\s*\(/.exec(masked.slice(close + 1, close + 200));
		if (!chain) continue;
		const matcher = `${/\bnot\b/.test(chain[1]) ? 'not.' : ''}${chain[2]}`;
		if (!/poll/.test(m[1] ?? '') && !PRESENCE.has(matcher) && secretValue(masked.slice(subject[0], subject[1]), subject[0], entries))
			report(subject, `matcher subject (${matcher})`, 'is a secret, which a failing matcher prints: assert a boolean, a count or a shape');
		else if (TEXT_MATCHERS.has(matcher) && revealsSecret(source.slice(subject[0], subject[1]), masked.slice(subject[0], subject[1]), subject[0], entries))
			report(subject, `matcher subject (${matcher})`, 'is the element that reveals a secret, whose text a failing matcher prints: assert on a child that holds none, or read the text into a boolean');
		for (const at of callArguments(masked, close + chain[0].length)) {
			if (secretValue(masked.slice(at[0], at[1]), at[0], entries)) report(at, `matcher argument (${matcher})`, 'is a secret, which a failing matcher prints: compare as a boolean (`text.includes(secret)`, `a === b`)');
		}
	}
	return result;
}

function filesUnder(folder) {
	const files = [];
	const walk = (dir) => {
		for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
			const file = path.join(dir, entry.name);
			if (entry.isDirectory()) {
				if (!['node_modules', '__screenshots__'].includes(entry.name)) walk(file);
			} else if (entry.isFile() && file.endsWith('.ts')) files.push(file);
		}
	};
	walk(folder);
	return files;
}

/**
 * Source lines that hold a secret's shape (scripts/secret-shapes.mjs): the report keeps the lines around each step as its
 * snippet, so a made-up token or key written out in a spec reaches the report like a real one. Build such a value at run
 * time (`['eyJ...', 'e30', 'c2ln'].join('.')`, `'sim-guess-'.padEnd(35, '0')`).
 */
export function shapeFindings(source) {
	const result = [];
	source.split('\n').forEach((line, index) => {
		for (const shape of SHAPES) if (shapeMatches(shape, line)) result.push({ line: index + 1, reason: `source line holds ${shape.kind} (a step's snippet in the report keeps it)`, text: line.trim().slice(0, 120) });
	});
	return result;
}

export function check(files) {
	const all = [];
	const supportFolder = path.join(E2E, 'tests', 'support');
	const support = fs.existsSync(supportFolder)
		? fs.readdirSync(supportFolder).filter((name) => name.endsWith('.ts')).map((name) => {
				const source = fs.readFileSync(path.join(supportFolder, name), 'utf8');
				return { source, masked: mask(source) };
			})
		: [];
	for (const file of files) {
		const resolved = path.resolve(file);
		if (EXEMPT.has(resolved)) continue;
		const source = fs.readFileSync(file, 'utf8');
		for (const f of findings(source, support)) all.push({ file: path.relative(E2E, file), ...f });
		if (!SHAPES_EXEMPT.has(resolved)) for (const f of shapeFindings(source)) all.push({ file: path.relative(E2E, file), ...f });
	}
	return all;
}

// Bad patterns each produce a finding; good ones (the helper, request data, fixed text, the answer's own matchers) none.
const SELF_TEST = {
	bad: [
		"expect(r.status(), await r.text()).toBe(200);",
		"expect.soft(r.status(), await r.text()).toBe(201);",
		"expect(r.status(), `${name}: ${await r.text()}`).toBe(201);",
		"expect([400, 409], await r.text()).toContain(r.status());",
		"expect(r.ok(), JSON.stringify(await r.json())).toBe(true);",
		"const body = await r.json();\nexpect(body.publishable, JSON.stringify(body)).toBe(true);",
		"const text = await r.text();\nexpect(r.status(), text).toBe(200);",
		"let served;\nserved = await r.json();\nexpect(x, JSON.stringify(served.criteria)).toEqual(y);",
		"const raw = await r.text();\nconst data = JSON.parse(raw);\nexpect(1, `${data.id}`).toBe(1);",
		"const { data } = await r.json();\nexpect(data.length, String(data)).toBe(1);",
		"expect(streamed.status, streamed.body).toBe(413);",
		"expect(a, (await r.json()).title).toBe(b);",
		"await expect.poll(async () => f(), { message: await r.text() }).toBe(1);",
		"const t = await r.text();\nexpect([201, 400], s === 201 ? 'ok' : t).toContain(s);",
		"await test.step(await r.text(), async () => {});",
		"const answer = await (await call('GET', url)).json() as Thing;\nexpect(answer.x, `${answer.y}`).toBe(1);",
		// One step removed: a template or a concatenation that carries an answer.
		"const msg = `status ${await r.text()}`;\nexpect(r.status(), msg).toBe(200);",
		"const t = await r.text();\nconst m = 'answer ' + t;\nexpect(r.status(), m).toBe(200);",
		"const t = await r.text();\nconst m = `answer ${t.slice(0, 100)}`;\nawait test.step(m, async () => {});",
		"const shown = r.status() === 200 ? 'ok' : await r.text();\nexpect(r.status(), shown).toBe(200);",
		"const body = await r.json().catch(() => ({}));\nexpect(1, JSON.stringify(body)).toBe(1);",
		// Secrets: a member, a variable named for one, a variable that holds one.
		"const credential = (await r.json()).credential;\nexpect(c, `issued ${credential}`).toBe(1);",
		"const key = (await r.json()).credential;\nawait test.step(`open ${key}`, async () => {});",
		"expect(r.status(), `${issued.clientSecret} refused`).toBe(401);",
		"expect(x, account.password).toBe(1);",
		"const pw = accounts().web.password;\nexpect(x, 'as ' + pw).toBe(1);",
		"const token = session.accessToken;\nexpect(x, `token ${token}`).toBe(1);",
		// Annotations, attachments and console output.
		"test.info().annotations.push({ type: 'answer', description: await r.text() });",
		"const body = await r.json();\nawait testInfo.attach('answer', { body: JSON.stringify(body) });",
		"const issued = await r.json();\nconsole.log('issued', issued);",
		"console.log(`device key ${device.credential}`);",
		// Request URLs with a secret in the query.
		"await request.get(`${host}/hubs/live?access_token=${canary.jwt}`);",
		"const response = await request.get(`${systemInfo}?access_token=${token.accessToken}`);",
		"await page.request.post(`${url}?code=${code}&key=${display.credential}`, { data });",
		"const url = `${board}?token=${session}`;\nawait request.get(url);",
		"await page.goto(`/display?code=${code}&key=${encodeURIComponent(issued.credential)}`);",
		"await request.get(base + '?apikey=' + apiKey);",
		// A secret parameter whatever its value (a literal included); a relative navigation keeps its fragment in the report.
		"await request.get(`${x}?access_token=eyJabc.e30.sig`);",
		"await page.goto(`/display?code=${code}#key=${encodeURIComponent(issued.credential)}`);",
		"const display = { id, path: `/display?code=${code}#key=${encodeURIComponent(body.credential)}` };\nawait page.goto(display!.path);",
		"let playerPath = '';\nasync function createDisplay(shape: { layout?: string } = {}): Promise<string> {\n\tconst display = await created.json();\n\treturn `/display?code=${code}#key=${encodeURIComponent(display.credential)}`;\n}\nplayerPath ||= await createDisplay();\nawait page.goto(playerPath);",
		"const playerPath = `/display?code=${code}#key=${encodeURIComponent(display.credential)}`;\nawait page.goto(playerPath);",
		"await page.goto(elsewhere.address);",
		// Typed secrets: a fill's step title is `Fill "<value>"`.
		"await page.getByLabel('Password', { exact: true }).fill(account.password);",
		"await page.locator('#step-up-code').fill(totpCode(accounts().webUsers.totpSecret!));",
		"await field.pressSequentially(code ?? totpCode(account.totpSecret));",
		"await page.keyboard.type(secret);",
		"await page.keyboard.insertText(webFirstPassword());",
		"await page.getByRole('textbox', { name: /Access Key/ }).fill(process.env.ARIVA_E2E_CRONZ_KEY!);",
		// Tokens by name.
		"expect(x, `as ${token}`).toBe(1);",
		"expect(r.status(), `refused with ${adminToken}`).toBe(403);",
		"expect(response.status(), `status of ${response.url()}`).toBe(401);",
		// The status helper's own message.
		"await expectStatus(r, 200, await r.text());",
		"const body = await r.json();\nawait expectStatusIn(r, [200, 201], JSON.stringify(body));",
		"await expectStatus(r, 201, `issued ${issued.credential}`);",
		// A navigation on any page variable.
		"await duty.goto(`/display?code=${c}#key=${display.credential}`);",
		"await pages[0].goto(`/display?code=${c}#key=${display.credential}`);",
		"await page!.goto(`/display?code=${c}#key=${display.credential}`);",
		"await page?.goto(`/display?code=${c}#key=${display.credential}`);",
		"await (await context.newPage()).goto(`/display?code=${c}#key=${display.credential}`);",
		"await page.goto(`/display?code=${c}#${display.credential}`);",
		// A preview token by its member name.
		"expect(x, summary.previewToken).toBe(1);",
		"await expectStatus(r, 200, `${summary.previewToken}`);",
		// ARV-249: a matcher's subject or argument that is a secret (a failing matcher prints both).
		"expect(text, 'no credential comes back').not.toContain(account.password);",
		"expect(c.clientSecret).toMatch(/^ics_[A-Za-z0-9_-]{43}$/);",
		"expect(fresh.clientSecret).not.toBe(c.clientSecret);",
		"expect(c.totpUri).toContain(`secret=${c.totpSecret}`);",
		"expect(fresh.totpSecret).toBeNull();",
		"expect(recoveryCodes).toHaveLength(10);",
		"const next = (await reset.json()).temporaryPassword;\nexpect(JSON.stringify(entries)).not.toContain(next);",
		"expect(read).not.toContain(credential.slice(13));",
		"expect(page.url()).not.toContain(encodeURIComponent(credential));",
		"expect(text).not.toContain(accessToken.split('.')[2]);",
		"expect.soft((await rotated.json()).credential).not.toBe(credential);",
		"await expect.poll(() => readBack()).toBe(issued.credential);",
		"expect(code).toBe(totpCode(account.totpSecret));",
		"expect(stored).toEqual({ id, key: display.credential });",
		"expect(list).toEqual([first.credential]);",
		"expect(x).toBe(token ?? '');",
		"expect(await page.locator('#key').inputValue()).toBe(credential);",
		"expect(header).toBe('Bearer ' + session.accessToken);",
		"const issued = (await r.json()).credential as string;\nexpect(issued!).toMatch(/^ardk_/);",
		// A secret returned by a call, read from process.env, or read from the element that reveals it.
		"expect(a.refreshCookie()).toBe(b.refreshCookie());",
		"expect(body).not.toContain(response.refreshCookie()!);",
		"expect(theft.refreshCookie(), 'a refused refresh clears the cookie').toBe('');",
		"expect(noCode.refreshCookie(), 'no session yet').toBeUndefined();",
		"const seed = process.env.ARIVA_E2E_MOCK_AMAN_PULL_SEED ?? '';\nexpect(text).not.toContain(seed);",
		"const first = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nexpect(first).toMatch(pattern);",
		"const next = (await page.getByTestId('credential-value').textContent())!.trim();\nexpect(await page.content()).not.toContain(next);",
		"expect(x).toBe(account.totpSeed);",
		// A request's auth headers; a web-first text matcher on the element that reveals a secret.
		"expect(headers.authorization).toBeUndefined();",
		"expect(headers.cookie).toBeUndefined();",
		"const reveal = page.getByTestId('credential-reveal');\nawait expect(reveal).toContainText('Temporary password');",
		"await expect(page.getByTestId('credential-value')).toHaveText(/ardk_/);"
	],
	good: [
		"await expectStatus(r, 200);",
		"await expectStatus(r, 201, `${name} is created`);",
		"await expectStatusIn(r, [400, 409], 'a refusal');",
		"await second.goto(`${webUrl}/live`);",
		"await tab.goto('/admin/users');",
		"expect(r.status(), 'the observers are given to the simulator').toBe(200);",
		"const body = await r.json();\nexpect(body.publishable).toBe(true);\nexpect(body).toMatchObject({ a: 1 });",
		"const data = { name: 'x' };\nexpect(r.status(), JSON.stringify(data)).toBe(400);",
		"expect(text, `${removed} is removed`).not.toContain(removed); // the body is the value, never the message",
		"expect(x, 'the answer body is empty: await r.text() is never called here').toBe(1);",
		"expect(x, `body ${'.text()'}`).toBe(1);",
		"const re = /await r\\.text\\(\\)/;\nexpect(x, 'regex only').toMatch(re);",
		"expect(content.headers()['content-type'], 'SVG is text').toBe('image/svg+xml');",
		"expect(body, `${removed} is removed`).not.toContain(removed);",
		"expect(r.status(), { message: 'fixed' }).toBe(200);",
		"const rows = (await r.json()).data;\nfor (const row of rows) expect(row.verdict, describe(row)).toBe('Pass');",
		"const id = (await r.json()).id as string;\nexpect(x, `the rule ${id} changed`).toBe(1);",
		"const answer = await first.json();\nfor (const [r, answer] of answers.entries()) expect(n, `batch ${r}: ${answer.status()}`).toBe(1);",
		"const ok = (await r.text()).includes('x');\nexpect(ok, `${ok} found`).toBe(true);",
		"const m = `status of ${path}`;\nexpect(r.status(), m).toBe(200);",
		"const key = `E2E-${n}`;\nawait test.step(`idempotency ${key}`, async () => {});",
		"for (const prefix of ['ardk_', 'ardp_']) expect(text.includes(prefix), `no ${prefix} credential`).toBe(false);",
		"test.info().annotations.push({ type: 'note', description: 'the window leaves out the outage' });",
		"await info.attach('record', { body: JSON.stringify(record), contentType: 'application/json' });",
		"console.log(`log scan: ${files.length} host logs`);",
		"await request.get(`${host.url}/health/readiness`, { headers: { Authorization: `Bearer ${canary.jwt}` } });",
		"await call('GET', `${host.url}/hubs/live?access_token=${canary.jwt}`);",
		"await request.get(`${api}/display/board?code=${display.code}&siteCode=${site}&zoneKey=${zoneKey}`);",
		"await page.goto(`${webUrl}/display?code=${code}#key=${encodeURIComponent(issued.credential)}`);",
		"const display = { id, path: `${webUrl}/display?code=${code}#key=${encodeURIComponent(body.credential)}` };\nawait page.goto(display!.path);",
		"const screens = [{ path: '/live' }, { path: '/immigration' }];\nfor (const screen of screens) await page.goto(screen.path);",
		"async function createDisplay(): Promise<string> {\n\treturn `${webUrl}/display?code=${code}#key=${encodeURIComponent(display.credential)}`;\n}\nconst path = await createDisplay();\nawait page.goto(path);",
		"const campaignsPage = (query: string) => `/validation?site=${site}&${query}`;\nawait page.goto(campaignsPage('view=results'));",
		"await page.goto(`/?q=${value}&next=${value}&lang=${value}#${value}`);",
		"await page.getByLabel('Username').fill(account.userName);",
		"await fillSecret(page.getByLabel('Password', { exact: true }), account.password);",
		"await page.getByLabel('Password', { exact: true }).fill(payload);",
		"expect(r.status(), `status of ${whereOf(response)}`).toBe(401);",
		"const tokens = await signIn(a);\nexpect(tokens.length, 'two sessions').toBe(2);",
		// ARV-249: secrets asserted as booleans, counts and shapes; presence matchers; flags and values of their own.
		"expect(text.includes(account.password), 'no password comes back').toBe(false);",
		"expect(/^ics_[A-Za-z0-9_-]{43}$/.test(c.clientSecret), 'a client secret').toBe(true);",
		"expect(fresh.clientSecret === c.clientSecret, 'a new secret').toBe(false);",
		"expect(fresh.totpSecret === null, 'no new seed').toBe(true);",
		"expect(recoveryCodes.length).toBe(10);",
		"expect(typeof previewToken).toBe('string');",
		"expect(issued.credential).toBeTruthy();",
		"expect(issued.credential).not.toBeNull();",
		"expect(body.user.mustChangePassword).toBe(true);",
		"expect(await codes(border.accessToken)).toEqual(['DMO']);",
		"expect(answer.credentials.every((c) => format.test(c.credential))).toBe(true);",
		"expect(Object.keys(body).sort()).toEqual(['accessToken', 'expiresAt', 'sessionId']);",
		"expect(token.tokenType).toBe('Bearer');",
		"expect(claimsOf(admin).amr).toEqual(['pwd', 'otp']);",
		"expect(await (await call('GET', url, { token: admin })).json()).toMatchObject({ a: 1 });",
		"expect(x).toMatchObject({ token: 'Bearer' });",
		"expect(credential.slice(0, 13).length).toBe(13);",
		"const ok = format.test(issued.credential);\nexpect(ok).toBe(true);",
		"expect(secretPath).toBe('/x'); // a name that ends in Path is no secret",
		"await expect.poll(() => count(issued.credential)).toBe(0);",
		"expect(response.refreshCookie()).toBeTruthy();",
		"expect(a.refreshCookie() === b.refreshCookie(), 'the same successor').toBe(true);",
		"const reveal = page.getByTestId('credential-reveal');\nawait expect(reveal).toHaveCount(0);",
		"const shownCodes = page.getByTestId('recovery-codes').getByRole('listitem');\nawait expect(shownCodes).toHaveCount(10);",
		"const host = process.env.ARIVA_E2E_MAIN_URL ?? '';\nexpect(host).toBe('http://127.0.0.1');",
		"const cookies = await page.context().cookies();\nexpect(cookies.length).toBe(1);",
		"expect(headers.authorization === undefined, 'no bearer token').toBe(true);",
		"const reveal = page.getByTestId('credential-reveal');\nawait expect(reveal.locator('#credential-title')).toContainText('Temporary password');",
		"await expect(page.getByTestId('credential-value')).toBeVisible();",
		"await expect(page.getByTestId('recovery-codes')).toHaveCount(0);"
	]
};

// Source lines (shapeFindings): a literal secret shape fails; a value built at run time passes. Joined here, so this file
// holds no shape itself either.
const SHAPE_SAMPLES = {
	bad: [
		"{ headers: { Authorization: 'Bearer sim-guess-" + '0'.repeat(25) + "' } }",
		'const response = await request.get(`${systemInfo}?access_token=eyJhbGciOiJIUzI1NiJ9.' + 'e30.c2lnbmF0dXJl`);',
		"const key = 'ardp_" + 'A'.repeat(43) + "';",
		"const issued = '{\"temporaryPassword\":\"abcde-FGHJK-" + "mnpqr-23456\"}';"
	],
	good: [
		"{ headers: { Authorization: `Bearer ${'sim-guess-'.padEnd(35, '0')}` } }",
		"const madeUp = ['eyJhbGciOiJIUzI1NiJ9', 'e30', 'c2lnbmF0dXJl'].join('.');",
		"const response = await call('GET', `${systemInfo}?access_token=${madeUp}`);",
		"expect(credential).toMatch(/^ardp_[A-Za-z0-9_-]{43}$/);"
	]
};

function selfTest() {
	const problems = [];
	SHAPE_SAMPLES.bad.forEach((line, i) => {
		if (shapeFindings(line).length === 0) problems.push(`source shape ${i + 1} not found: ${line}`);
	});
	SHAPE_SAMPLES.good.forEach((line, i) => {
		const found = shapeFindings(line);
		if (found.length > 0) problems.push(`source line ${i + 1} flagged (${found[0].reason}): ${line}`);
	});
	SELF_TEST.bad.forEach((source, i) => {
		if (findings(source).length === 0) problems.push(`bad ${i + 1} not found: ${source.replace(/\n/g, ' / ')}`);
	});
	SELF_TEST.good.forEach((source, i) => {
		const found = findings(source);
		if (found.length > 0) problems.push(`good ${i + 1} flagged (${found[0].reason}): ${source.replace(/\n/g, ' / ')}`);
	});
	// The helper is exempt by path, and the real tree is checked from files: prove both with a temporary copy of a bad spec.
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-expect-messages-'));
	try {
		const spec = path.join(dir, 'bad.spec.ts');
		fs.writeFileSync(spec, SELF_TEST.bad[0]);
		if (check([spec]).length !== 1) problems.push('a bad spec file is not found');
		if (fs.existsSync(HELPER) && check([HELPER]).length !== 0) problems.push('the helper itself is flagged');
	} finally {
		fs.rmSync(dir, { recursive: true, force: true });
	}
	if (problems.length) {
		console.error(`expect message check self-test FAILED:\n${problems.join('\n')}`);
		return false;
	}
	console.log(`expect message check self-test: ${SELF_TEST.bad.length} bad patterns found, ${SELF_TEST.good.length} good ones quiet; ${SHAPE_SAMPLES.bad.length} literal secret shapes on source lines found, ${SHAPE_SAMPLES.good.length} run-time values quiet; the helper exempt.`);
	return true;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
	const args = process.argv.slice(2);
	if (args.includes('--self-test')) process.exit(selfTest() ? 0 : 1);
	const files = args.length
		? args.map((f) => path.resolve(f))
		: [...filesUnder(path.join(E2E, 'tests')), ...['playwright.config.ts', 'playwright.smoke.config.ts'].map((f) => path.join(E2E, f)).filter((f) => fs.existsSync(f))];
	const found = check(files);
	for (const f of found) console.log(`${f.file}:${f.line}: ${f.reason}: ${f.text}`);
	if (found.length) {
		console.error(`\n${found.length} places put an answer body or a secret where the report keeps it (ARV-152, CWE-532): check statuses with expectStatus or expectStatusIn (tests/support/expect-status.ts), keep secrets out of messages, titles, annotations, attachments and console output, and send a credential in a header or through call(), never in a request context's URL.`);
		process.exit(1);
	}
	console.log(`expect messages, titles, annotations, attachments, console output and request URLs: no answer body or secret in ${files.length} files.`);
}
