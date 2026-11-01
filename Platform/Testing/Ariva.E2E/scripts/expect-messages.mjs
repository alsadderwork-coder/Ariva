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
// ARV-224: an address (a request context's call; goto, waitForURL, waitForRequest, waitForResponse, route, unroute) is read
// down to where it comes from: a name through its binding, every assignment of a variable included (`+=` read as a
// concatenation); a parameter through the call being read or every call of its function in the file (a list method's
// callback through the list's elements; a function passed on as a value, or any other anonymous callback, is not read); a
// member through the object literal it comes from; an element through its list (a list literal, Object.values, entries or
// keys of an object literal, filter, slice, map); a call through what its function returns (every function of that name in
// the file, or one a support module exports); `??`, `||`, `&&` and `a ? b : c` through every branch. A name set by a
// destructuring assignment, or whose object or list the file changes (a member or element set, push and the other mutating
// methods, Object.assign), is not read; a change made through another name is not seen. The leading part must end in a
// literal, a regular expression, a predicate, a `new RegExp(...)` pattern or an origin: a name imported from a support module
// (`../support/<name>`), named as one (webUrl, hosts, apiHosts) and, when the check reads that module, a `const` it never
// changes, or a member of one, a reviewed export (REVIEWED_ORIGINS:
// the smoke suite's target.base), or the baseURL fixture of a test's or a hook's callback. The address is absolute when that
// part is an origin or starts with http(s). Its later parts (a `+`'s, a template's other interpolations, a string method's
// arguments) are inlined when they resolve, and otherwise read for secrets only (a secret value anywhere; in the query or
// fragment also a quoted secret or a name that looks like one), so a server-issued value under a neutral name in a later
// part is held by review. Parameters are read across all the parts: a secret parameter (key=, token=, access_token=,
// session= and the like) fails in the query, and in the fragment unless the address is absolute and its step drops the
// fragment (goto and the request calls do; the waits and routes keep their argument whole). A request's options must be an
// object literal, or a name bound to one that nothing changes, with no spread or computed name; its params may not name,
// hold, spread or compute a secret. In a file whose addresses name a secret parameter, no matcher may take an address by
// value: a subject holding a `.url()` read or a `location` read in page.evaluate, however it is cut or wrapped, or a name in
// it that may hold one (through its binding, a loop's list, or as a parameter the argument at each call of its function or a
// list callback's list, however many steps; past 40 it fails closed), or a call of the file's or a support module's
// function that returns one, unless its outermost operation (or the name's own use) gives a boolean or a number or reads the pathname, origin, host, hostname, protocol, port, the search
// parameters' names or a search parameter that is not a secret's (or toHaveURL at all). `&&`, `||` and `??` give an operand. In any file, a matcher argument that names a secret parameter or
// a secret's prefix (ardp_, ardk_, ics_), or a matcher on searchParams.get of a secret parameter, fails. Not read: a
// navigation a support helper makes (the secret-address flag is per file), an address set inside page.evaluate,
// newContext({ baseURL }), goto's referer, the parameters of a function passed on as a value or of an object's or a class's
// method, a helper of a module the check does not read. A binding is the latest one before its use, whatever its scope, and a support module is found by its base name.

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
/** Expressions that name a credential, in an address's query or fragment (secretParamKey names its parameters). */
const SECRET_EXPRESSION = /[A-Za-z]*(?:[Tt]oken|[Jj]wt|[Ss]ecret|[Pp]assword|[Cc]redential)[A-Za-z]*|\b(?:apiKey|deviceKey|displayKey|key)\b/;

/**
 * Every binding of a name in the masked source, in source order, and what it holds. A body: a value that reads an answer
 * (`.text()`, `.json()` or `.body()`) anywhere, unless every read is only the start of a field (`(await r.json()).id`, a
 * value of its own); or a value that puts a body-holding name into a template (`${}`) or a `+` concatenation, passes it
 * through JSON.parse or names it alone. A secret: a value that reads a secret-named member (`.credential`, `.password`) or
 * holds a secret-holding name the same ways. A loop variable holds neither.
 */
export function bindings(masked, source = masked) {
	const list = [];
	// `name = <rest of the line>` (a declaration, a plain or a logical assignment), destructuring declarations and loop variables.
	// ARV-224: each with its kind (a constant, a variable, an assignment) and, for a destructured name, where in the value it is.
	for (const m of masked.matchAll(/(?<![\w$.])([A-Za-z_$][\w$]*)\s*(?::\s*[^=;\n]+?)?(\|\||\?\?|&&|\+)?=(?![=>])([^;\n]*)/g)) {
		const declared = /\b(const|let|var)\s+$/.exec(masked.slice(Math.max(0, m.index - 12), m.index))?.[1];
		const kind = m[2] === '+' ? 'append' : m[2] ? 'logical' : declared === 'const' ? 'const' : declared ? 'let' : 'assign';
		list.push({ names: [{ name: m[1], path: [] }], at: m.index, value: m[3], valueAt: m.index + m[0].length - m[3].length, kind });
	}
	for (const m of masked.matchAll(/\b(?:const|let|var)\s*([{[][^=;]*?[}\]])\s*(?::\s*[^=;\n]+?)?=(?![=>])([^;\n]*)/dg)) {
		const names = patternNames(source, masked, m.indices[1][0], m.indices[1][1]);
		list.push({ names, at: m.index, value: m[2], valueAt: m.indices[2][0], kind: 'destructure' });
	}
	// ARV-224: a destructuring assignment (`({ a } = x)`, `[a] = x`): the names it sets are not read (unknown).
	for (const m of masked.matchAll(/[}\]]\s*=(?![=>])/g)) {
		const open = opening(masked, m.index);
		if (open < 0) continue;
		let k = open;
		while (k > 0 && /\s/.test(masked[k - 1])) k--;
		if (k > 0 && !/[(;{},]/.test(masked[k - 1])) continue; // a declaration, a type, an index or a member: read elsewhere
		list.push({ names: patternNames(source, masked, open, m.index + 1), at: open, value: '', valueAt: open, kind: 'unknown' });
	}
	for (const m of masked.matchAll(/\bfor\s*\(\s*(?:const|let|var)\s+([^;]*?)\s+(of|in)\b/dg)) {
		// ARV-224: the list a loop variable takes its values from (to the loop's closing parenthesis).
		const open = masked.indexOf('(', m.index);
		const close = closing(masked, open);
		const iterableAt = m.index + m[0].length;
		const iterable = close > iterableAt ? masked.slice(iterableAt, close) : '';
		const names = patternNames(source, masked, m.indices[1][0], m.indices[1][1]);
		list.push({ names, at: m.index, value: '', valueAt: m.index, iterable, iterableAt, kind: 'loop', loopIn: m[2] === 'in' });
	}
	list.sort((a, b) => a.at - b.at);
	const entries = list.flatMap(({ names, ...rest }) =>
		names.filter((n) => IDENTIFIER.test(n.name)).map((n) => ({ ...rest, name: n.name, path: n.path, body: false, secret: false }))
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
		// response.refreshCookie(), totpCode(seed); the smoke suite's credential(credentialVariables.displayCode) reads a display's
		// code, an identifier that travels in the address by design, not a secret.
		if (SECRET_CALL.test(name) && !(name === 'credential' && /^\s*credentialVariables\s*\.\s*displayCode\s*$/.test(args[0] ?? ''))) return true;
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

/** The offset of the bracket that closes the one at `open` in masked text, or -1. */
function closing(masked, open) {
	let depth = 0;
	for (let i = open; i < masked.length; i++) {
		if ('([{'.includes(masked[i])) depth++;
		else if (')]}'.includes(masked[i]) && --depth === 0) return i;
	}
	return -1;
}

/** The offset just after the template literal whose opening backtick is at `open` in masked text, or -1. */
function skipTemplate(masked, open) {
	for (let k = open + 1; k < masked.length; k++) {
		if (masked[k] === '`') return k + 1;
		if (masked[k] === '$' && masked[k + 1] === '{') {
			k = closing(masked, k + 1);
			if (k < 0) return -1;
		}
	}
	return -1;
}

/** The bracket depth at an offset of a text (opened minus closed), in linear time. */
function depthAt(text, at) {
	let depth = 0;
	for (let i = 0; i < at; i++) {
		if ('([{'.includes(text[i])) depth++;
		else if (')]}'.includes(text[i])) depth--;
	}
	return depth;
}

/** The range [from, to) with white space, `await`, enclosing parentheses, `as T`, `satisfies T` and trailing `!` taken off, in masked text. */
function trimRange(masked, from, to) {
	for (;;) {
		while (from < to && /\s/.test(masked[from])) from++;
		while (to > from && /[\s!;]/.test(masked[to - 1])) to--;
		if (masked.startsWith('await', from) && /\s/.test(masked[from + 5] ?? '')) {
			from += 5;
			continue;
		}
		// A cast at the end: the last top-level ` as ` or ` satisfies ` followed by a type only (found by position, in linear time).
		const text = masked.slice(from, to);
		const cast = Math.max(text.lastIndexOf(' as '), text.lastIndexOf(' satisfies '));
		if (cast > 0 && /^\s+(?:as|satisfies)\s+[\w$.<>[\]|,\s{}:?'"]+$/.test(text.slice(cast)) && !/[()]/.test(text.slice(cast)) && depthAt(text, cast) === 0) {
			to = from + cast;
			continue;
		}
		if (masked[from] === '(' && closing(masked, from) === to - 1) {
			from++;
			to--;
			continue;
		}
		return [from, to];
	}
}

/** Top-level parts of masked[from, to) split at a sticky separator, as [from, to] ranges. */
function splitTopAt(masked, from, to, separator) {
	const parts = [];
	let depth = 0;
	let start = from;
	for (let i = from; i < to; i++) {
		const c = masked[i];
		if ('([{'.includes(c)) depth++;
		else if (')]}'.includes(c)) depth--;
		else if (depth === 0) {
			separator.lastIndex = i;
			const m = separator.exec(masked);
			if (m && i + m[0].length <= to) {
				parts.push([start, i]);
				start = i + m[0].length;
				i = start - 1;
			}
		}
	}
	parts.push([start, to]);
	return parts;
}

/**
 * The end of a binding's value from valueAt: up to a `;`, a `,` or a closing bracket at its own level, or a line break that
 * neither ends with an operator nor is followed by one (a chain or a ternary continued on the next line).
 */
function valueEnd(masked, entry) {
	let i = entry.valueAt;
	while (i < masked.length && /\s/.test(masked[i])) i++;
	let depth = 0;
	for (; i < masked.length; i++) {
		const c = masked[i];
		if (c === '`') {
			const end = skipTemplate(masked, i);
			if (end < 0) return masked.length;
			i = end - 1;
		} else if ('([{'.includes(c)) depth++;
		else if (')]}'.includes(c)) {
			if (depth === 0) return i;
			depth--;
		} else if (depth === 0 && (c === ';' || c === ',')) return i;
		else if (depth === 0 && c === '\n') {
			const before = masked.slice(entry.valueAt, i).trimEnd();
			const after = /^\s*(.)/.exec(masked.slice(i, i + 200))?.[1] ?? '';
			if (!/[-+*/%?:|&=,(<>]$/.test(before) && !/^[.?:+|&*/%-]$/.test(after)) return i;
		}
	}
	return masked.length;
}

/** The names a binding pattern declares and where each comes from: [] the whole value, a number an element, a string a member. */
function patternNames(source, masked, from, to, prefix = []) {
	[from, to] = [from + (masked.slice(from, to).length - masked.slice(from, to).trimStart().length), from + masked.slice(from, to).trimEnd().length];
	const text = masked.slice(from, to);
	if (IDENTIFIER.test(text)) return [{ name: text, path: prefix }];
	const open = text[0];
	if ((open !== '[' && open !== '{') || closing(masked, from) !== to - 1) return [];
	const names = [];
	splitTopAt(masked, from + 1, to - 1, /,/y).forEach(([a, b], index) => {
		const item = masked.slice(a, b);
		if (!item.trim()) return;
		const lead = item.length - item.trimStart().length;
		if (/^\s*\.\.\./.test(item)) {
			// The rest of a value: read as the whole value, a superset of what it holds.
			names.push(...patternNames(source, masked, a + item.indexOf('...') + 3, b, prefix));
			return;
		}
		// A default (`= value`) leaves the name and its place as they are.
		const [p, q] = splitTopAt(masked, a, b, /=(?![=>])/y)[0];
		if (open === '[') {
			names.push(...patternNames(source, masked, p, q, [...prefix, index]));
			return;
		}
		const keyed = /^\s*([A-Za-z_$][\w$]*|(['"])\s*\2)\s*:(?!:)/.exec(masked.slice(p, q));
		if (keyed) {
			const key = keyed[2] ? source.slice(p + lead + 1, p + lead + keyed[1].length - 1) : keyed[1];
			names.push(...patternNames(source, masked, p + keyed[0].length, q, [...prefix, key]));
		} else {
			const name = /^\s*([A-Za-z_$][\w$]*)\s*$/.exec(masked.slice(p, q))?.[1];
			if (name) names.push({ name, path: [...prefix, name] });
		}
	});
	return names;
}

/** The call whose argument list holds the offset: the `(` that opens it and the argument's index, or undefined. */
function enclosingCall(masked, at) {
	let depth = 0;
	let commas = 0;
	for (let i = at - 1; i >= 0; i--) {
		const c = masked[i];
		if (')]}'.includes(c)) depth++;
		else if ('([{'.includes(c)) {
			if (depth === 0) return c === '(' ? { open: i, index: commas } : undefined;
			depth--;
		} else if (depth === 0 && c === ',') commas++;
		else if (depth === 0 && c === ';') return undefined;
	}
	return undefined;
}

/** Where the expression that ends at `end` (exclusive) starts: a name with its members, calls and indexes, read backwards. */
function expressionStart(masked, end) {
	let i = end;
	for (;;) {
		let k = i;
		while (k > 0 && /\s/.test(masked[k - 1])) k--;
		const c = masked[k - 1];
		if (c === ')' || c === ']') {
			const open = opening(masked, k - 1);
			if (open < 0) return i;
			i = open;
			continue;
		}
		if (c && /[\w$]/.test(c)) {
			let s = k - 1;
			while (s > 0 && /[\w$]/.test(masked[s - 1])) s--;
			if (/^(?:await|return|of|in|new|typeof|void|yield|case|else)$/.test(masked.slice(s, k))) return i;
			let d = s;
			while (d > 0 && /\s/.test(masked[d - 1])) d--;
			if (masked[d - 1] === '.') {
				i = masked[d - 2] === '?' || masked[d - 2] === '!' ? d - 2 : d - 1;
				continue;
			}
			return s;
		}
		return i;
	}
}

/**
 * Every function of the file: declarations, function expressions and arrows, with their parameters (names, places in a
 * destructured parameter, defaults) and their bodies (a block, or an arrow's expression).
 */
function functionsOf(source, masked) {
	const list = [];
	const parameters = (open, close) => {
		if (open < 0) return [];
		return callArguments(masked, open).flatMap(([a, b], index) => {
			const [p, q] = splitTopAt(masked, a, b, /=(?![=>])/y)[0];
			const def = splitTopAt(masked, a, b, /=(?![=>])/y)[1];
			let start = p;
			while (start < q && /\s/.test(masked[start])) start++;
			if (masked.startsWith('...', start)) start += 3;
			while (start < q && /\s/.test(masked[start])) start++;
			if (masked[start] === '{' || masked[start] === '[') {
				const end = closing(masked, start);
				return patternNames(source, masked, start, end + 1).map((n) => ({ name: n.name, index, path: n.path, def: undefined }));
			}
			const name = /^([A-Za-z_$][\w$]*)/.exec(masked.slice(start, q))?.[1];
			return name ? [{ name, index, path: [], def }] : [];
		});
	};
	// The `{` that opens a function's body after its parameters, past a return type (`: Promise<{ id: string }>`, `: (A & { b: B })[]`).
	const bodyBrace = (from) => {
		let i = from;
		while (i < masked.length && /\s/.test(masked[i])) i++;
		if (masked[i] === '{') return i;
		if (masked[i] !== ':') return -1;
		let depth = 0;
		let previous = ':';
		for (i++; i < masked.length; i++) {
			const c = masked[i];
			if (/\s/.test(c)) continue;
			if (depth === 0 && (c === ';' || (c === '=' && masked[i + 1] !== '>'))) return -1;
			if (c === '{' && depth === 0 && !/[:|&<,(]/.test(previous) && !(previous === '>' && masked[i - 2] === '=')) return i;
			if ('([{<'.includes(c)) depth++;
			else if (')]}>'.includes(c) && !(c === '>' && masked[i - 1] === '=')) depth--;
			previous = c;
		}
		return -1;
	};
	const boundName = (at) => /(?:\b(?:const|let|var)\s+)([A-Za-z_$][\w$]*)\s*(?::[^=;]+)?=\s*(?:async\s+)?$/d.exec(masked.slice(Math.max(0, at - 200), at));
	const bodyOf = (from) => {
		let b = from;
		while (b < masked.length && /\s/.test(masked[b])) b++;
		if (masked[b] === '{') {
			const end = closing(masked, b);
			return end < 0 ? undefined : { body: [b + 1, end], expression: false };
		}
		let depth = 0;
		let i = b;
		for (; i < masked.length; i++) {
			const c = masked[i];
			if (c === '`') {
				const end = skipTemplate(masked, i);
				if (end < 0) break;
				i = end - 1;
			} else if ('([{'.includes(c)) depth++;
			else if (')]}'.includes(c)) {
				if (depth === 0) break;
				depth--;
			} else if (depth === 0 && (c === ',' || c === ';')) break;
		}
		return { body: [b, i], expression: true };
	};
	for (const m of masked.matchAll(/\bfunction\b\s*\*?\s*([A-Za-z_$][\w$]*)?\s*(?:<[^<>]*>)?\s*\(/g)) {
		const open = m.index + m[0].length - 1;
		const close = closing(masked, open);
		if (close < 0) continue;
		const brace = bodyBrace(close + 1);
		if (brace < 0) continue; // an overload or a type: no body
		const body = bodyOf(brace);
		if (!body) continue;
		const bound = m[1] ? undefined : boundName(m.index);
		const nameAt = m[1] ? m.index + m[0].indexOf(m[1], 8) : bound ? Math.max(0, m.index - 200) + bound.indices[1][0] : -1;
		list.push({ name: m[1] ?? bound?.[1], nameAt, at: m.index, open, close, params: parameters(open, close), ...body });
	}
	for (const m of masked.matchAll(/=>/g)) {
		let k = m.index;
		while (k > 0 && /\s/.test(masked[k - 1])) k--;
		const head = masked.slice(Math.max(0, k - 300), k);
		const base = k - head.length;
		let open = -1;
		let close = -1;
		let start;
		let params;
		if (masked[k - 1] === ')') {
			close = k - 1;
			open = opening(masked, close);
		} else {
			const typed = /\)\s*:\s*[^()=;{}]*$/.exec(head);
			const single = /(?<![\w$.])([A-Za-z_$][\w$]*)$/.exec(head);
			if (typed && (!single || !/^(?:async)$/.test(single[1]))) {
				close = base + typed.index;
				open = opening(masked, close);
			} else if (single) {
				start = base + single.index;
				params = [{ name: single[1], index: 0, path: [], def: undefined }];
			}
		}
		if (open < 0 && !params) continue;
		if (!params) {
			start = open;
			params = parameters(open, close);
		}
		const asyncAt = /\basync\s*$/.exec(masked.slice(Math.max(0, start - 20), start));
		const at = asyncAt ? start - (Math.min(start, 20) - asyncAt.index) : start;
		const body = bodyOf(m.index + 2);
		if (!body) continue;
		const bound = boundName(at);
		list.push({ name: bound?.[1], nameAt: bound ? Math.max(0, at - 200) + bound.indices[1][0] : -1, at, open, close, params, ...body });
	}
	return list;
}

/** The names a file imports, each with the module's base name (`../support/hosts` is hosts) and the name it exports. */
function importsOf(source, masked) {
	const imports = new Map();
	for (const m of masked.matchAll(/\bimport\s+(?!type\b)([^;]*?)\s+from\s+(['"])/g)) {
		const quote = m.index + m[0].length - 1;
		const end = source.indexOf(source[quote], quote + 1);
		const specifier = source.slice(quote + 1, end);
		const module = path.basename(specifier).replace(/\.(?:ts|js|mjs)$/, '');
		const clause = m[1];
		const named = /\{([^}]*)\}/.exec(clause);
		if (named) {
			for (const item of named[1].split(',')) {
				const parts = item.trim().replace(/^type\s+/, '').split(/\s+as\s+/);
				if (!parts[0] || /^type\s/.test(item.trim())) continue;
				imports.set((parts[1] ?? parts[0]).trim(), { module, specifier, exported: parts[0].trim() });
			}
		}
		const outside = clause.replace(/\{[^}]*\}/, '');
		const star = /\*\s*as\s+([A-Za-z_$][\w$]*)/.exec(outside);
		if (star) imports.set(star[1], { module, specifier, exported: '*' });
		const fallback = /^\s*([A-Za-z_$][\w$]*)/.exec(outside);
		if (fallback && !star) imports.set(fallback[1], { module, specifier, exported: 'default' });
	}
	return imports;
}

/** ARV-224: what an address is read in: a file's source and masked text, its bindings, functions and imports, and the support files. */
function addressContext(source, masked, entries, support = []) {
	return { source, masked, entries, functions: functionsOf(source, masked), imports: importsOf(source, masked), support, visiting: new Set() };
}

/** Whether an import's specifier names a support module (`../support/hosts`; `./hosts` from a support file itself). */
function fromSupport(ctx, specifier) {
	return /(?:^|\/)support\/[^/]+$/.test(specifier) || (ctx.isSupport === true && /^\.\/[^/]+$/.test(specifier));
}

/**
 * Whether a support module's export is a constant nothing changes, so its name can stand for what it holds: when the module
 * is read (tests/support), its export must be a `const` binding the module never changes; a module the check does not read
 * (another support folder) is taken on its name.
 */
function constantExport(ctx, imported) {
	const sctx = supportContext(ctx, imported.module);
	if (!sctx) return true;
	const entry = latestBinding(sctx, imported.exported, sctx.masked.length);
	return entry !== undefined && entry.kind === 'const' && !sctx.entries.some((e) => e.name === imported.exported && e !== entry) && !changedAfterWritten(sctx, imported.exported);
}

/**
 * ARV-224: exports read as origins whatever their names, each reviewed: the smoke suite's target (tests/smoke/support), whose
 * base is the address the smoke run was given.
 */
const REVIEWED_ORIGINS = new Map([['smoke', new Map([['target', new Set(['base'])]])]]);

/** The context of the support file a module name names (tests/support/<name>.ts), or undefined. */
function supportContext(ctx, module) {
	const file = ctx.support.find((s) => s.name === module);
	if (!file) return undefined;
	if (!file.ctx) {
		const entries = bindings(file.masked);
		for (const entry of entries) entry.raw = file.source.slice(entry.valueAt, entry.valueAt + entry.value.length);
		file.ctx = addressContext(file.source, file.masked, entries, ctx.support);
		file.ctx.isSupport = true;
	}
	return file.ctx;
}

/**
 * ARV-224: an origin's name. An import of the file named as one (webUrl, hosts, apiHosts, an origin's member such as
 * target.base or host.url) or the baseURL fixture is the origin it names; nothing else is, however it looks.
 */
const ORIGIN_ROOT = /^(?:url|hosts?|origin|base)$|[a-z0-9](?:Url|URL|Hosts?|Origin|Base)$/;
const MAX_DEPTH = 60;
const MAX_FORMS = 64;
/** The most parts one form may have: an address longer than this is not read (a chain of templates can double at each step). */
const MAX_PARTS = 256;
/** String methods an address may go through: the result is read from the receiver (and the arguments, which may be inserted). */
const STRING_METHODS = new Set(['trim', 'trimStart', 'trimEnd', 'toString', 'valueOf', 'normalize', 'toLowerCase', 'toUpperCase', 'slice', 'substring', 'substr', 'replace', 'replaceAll', 'padStart', 'padEnd', 'concat']);
/** Those that keep an absolute address absolute. */
const KEEPS_ORIGIN = new Set(['trim', 'trimStart', 'trimEnd', 'toString', 'valueOf', 'normalize', 'toLowerCase', 'concat', 'padEnd']);
/** Functions whose result is their argument, encoded or not; true when an absolute address stays absolute. */
const ARGUMENT_FUNCTIONS = new Map([['String', true], ['encodeURI', true], ['decodeURI', true], ['encodeURIComponent', false], ['decodeURIComponent', false]]);
/** List methods whose elements are some of the receiver's. */
const LIST_METHODS = new Set(['filter', 'slice', 'sort', 'toSorted', 'reverse', 'toReversed', 'concat']);
/** List methods whose callback takes the receiver's elements as its first parameter. */
const ITERATING_METHODS = new Set(['forEach', 'map', 'flatMap', 'filter', 'some', 'every', 'find', 'findLast', 'findIndex', 'findLastIndex']);

const unresolved = (text) => ({ kind: 'unresolved', text: String(text).trim().replace(/\s+/g, ' ').slice(0, 80) });
const isObjectLiteral = (masked, from, to) => masked[from] === '{' && closing(masked, from) === to - 1;
const isArrayLiteral = (masked, from, to) => masked[from] === '[' && closing(masked, from) === to - 1;

/** The latest binding of the name before the offset, leaving out a parameter's default (it belongs to the parameter). */
function latestBinding(ctx, name, at) {
	if (!ctx.byName) {
		// Once per file: the bindings by name, those inside a parameter list (a default) left out.
		ctx.byName = new Map();
		for (const entry of ctx.entries) {
			if (ctx.functions.some((f) => f.open >= 0 && entry.at > f.open && entry.at < f.close)) continue;
			if (!ctx.byName.has(entry.name)) ctx.byName.set(entry.name, []);
			ctx.byName.get(entry.name).push(entry);
		}
	}
	let latest;
	for (const entry of ctx.byName.get(name) ?? []) if (entry.at < at) latest = entry;
	return latest;
}

/** The innermost function whose body holds the offset and whose parameters name the name. */
function parameterFunction(ctx, name, at) {
	let best;
	for (const f of ctx.functions) if (f.body[0] <= at && at <= f.body[1] && f.params.some((p) => p.name === name) && (!best || f.at > best.at)) best = f;
	return best;
}

/**
 * ARV-224: whether the file changes the object or list a name holds: a member or an element set (`d.path = x`, `paths[0] = x`,
 * `d.path += x`), a mutating method (push, unshift, splice, fill, set, add, copyWithin) or Object.assign onto it. What such a
 * name holds is not what its binding wrote, so it is not read. A change through another name (an alias) is not seen.
 */
function changedAfterWritten(ctx, name) {
	ctx.changed ??= new Map();
	if (!ctx.changed.has(name)) {
		const n = name.replace(/\$/g, '\\$');
		const access = String.raw`(?:\s*(?:\?\.|!\.|\.)\s*[A-Za-z_$][\w$]*|\s*!?\s*\[[^\]\n]*\])`;
		const re = new RegExp(String.raw`(?<![\w$.])${n}${access}+\s*(?:\|\||\?\?|&&|[-+*/%])?=(?![=>])|(?<![\w$.])${n}${access}*\s*(?:\?\.|\.)\s*(?:push|unshift|splice|fill|set|add|copyWithin)\s*\(|\bObject\s*\.\s*assign\s*\(\s*${n}(?![\w$])`);
		ctx.changed.set(name, re.test(ctx.masked));
	}
	return ctx.changed.get(name);
}

/** The values a name may hold at the offset: its binding's, its parameter's (through the call being read or every call of the function), or an import. */
function nameValues(ctx, name, at, env, depth) {
	if (depth > MAX_DEPTH) return [unresolved(name)];
	if (changedAfterWritten(ctx, name)) return [unresolved(`${name} (changed after it is written: a member or an element set, a mutating method, Object.assign)`)];
	const fn = parameterFunction(ctx, name, at);
	const binding = latestBinding(ctx, name, at);
	// A binding made inside the function wins over its parameter; a parameter over a binding made before the function.
	if (binding && (!fn || binding.at > fn.at)) return bindingValues(ctx, binding, env, depth + 1);
	if (fn) return parameterValues(ctx, fn, name, env, depth + 1);
	const imported = ctx.imports.get(name);
	if (imported) return [{ kind: 'import', ctx, name, origin: ORIGIN_ROOT.test(name) && fromSupport(ctx, imported.specifier) && constantExport(ctx, imported), ...imported }];
	return [unresolved(name)];
}

/** The values of a binding: a constant's value, every assignment of a variable, a loop variable's elements, a destructured part. */
function bindingValues(ctx, entry, env, depth) {
	if (ctx.visiting.has(entry) || depth > MAX_DEPTH) return [unresolved(entry.name)];
	ctx.visiting.add(entry);
	try {
		if (entry.kind === 'loop') {
			const [a, b] = [entry.iterableAt, entry.iterableAt + entry.iterable.length];
			if (entry.loopIn) return objectKeys(ctx, values(ctx, a, b, env, depth + 1), depth + 1);
			return applyPath(ctx, listElements(ctx, a, b, env, depth + 1), entry.path, depth + 1);
		}
		const value = (e) => applyPath(ctx, values(ctx, e.valueAt, valueEnd(ctx.masked, e), env, depth + 1), e.path ?? [], depth + 1);
		if (entry.kind === 'const' || entry.kind === 'destructure') return value(entry);
		// A variable: any of its assignments in the file may be the one in force when the address is read; each `+=` adds to
		// them (read as a concatenation), and a destructuring assignment sets it to something the check does not read.
		const all = ctx.entries.filter((e) => e.name === entry.name && (e === entry || ['let', 'assign', 'logical', 'append', 'unknown'].includes(e.kind)) && !ctx.functions.some((f) => f.open >= 0 && e.at > f.open && e.at < f.close));
		if (all.some((e) => e.kind === 'unknown')) return [unresolved(`${entry.name} (set by a destructuring assignment)`)];
		const bases = all.filter((e) => e.kind !== 'append').flatMap((e) => (ctx.visiting.has(e) && e !== entry ? [] : value(e)));
		const appends = all.filter((e) => e.kind === 'append');
		if (appends.length === 0) return bases;
		return [...bases, { kind: 'append', ctx, env, base: bases, adds: appends.map((e) => [e.valueAt, valueEnd(ctx.masked, e)]) }];
	} finally {
		ctx.visiting.delete(entry);
	}
}

/** A parameter's values: the argument of the call being read, else of every call of its function in the file, else a list's elements for a list method's callback. */
function parameterValues(ctx, fn, name, env, depth) {
	const param = fn.params.find((p) => p.name === name);
	// Playwright's baseURL fixture: destructured from the parameters of a callback handed to test (or smoke) or one of its hooks.
	if (name === 'baseURL' && param.path.length === 1 && !fn.name) {
		const call = enclosingCall(ctx.masked, fn.at);
		if (call && /(?:^|[^\w$.])(?:test|smoke)(?:\s*\.\s*(?:only|skip|fixme|fail|slow|beforeEach|afterEach|beforeAll|afterAll))?\s*$/.test(ctx.masked.slice(Math.max(0, call.open - 60), call.open)))
			return [{ kind: 'origin', any: false, text: 'baseURL' }];
	}
	for (let frame = env; frame; frame = frame.parent) {
		if (frame.fn !== fn || frame.fnCtx !== ctx) continue;
		const arg = frame.args[param.index];
		const vals = arg ? values(frame.argsCtx, arg[0], arg[1], frame.parent, depth + 1) : param.def ? values(ctx, param.def[0], param.def[1], frame.parent, depth + 1) : [{ kind: 'nothing' }];
		return applyPath(ctx, vals, param.path, depth + 1);
	}
	if (fn.name) {
		const sites = callSites(ctx, fn);
		if (!sites) return [unresolved(`${name} (a parameter of ${fn.name}, which is passed on or never called)`)];
		return sites.flatMap((args) => {
			const arg = args[param.index];
			const vals = arg ? values(ctx, arg[0], arg[1], undefined, depth + 1) : param.def ? values(ctx, param.def[0], param.def[1], undefined, depth + 1) : [{ kind: 'nothing' }];
			return applyPath(ctx, vals, param.path, depth + 1);
		});
	}
	// An anonymous function: a list method's callback takes the list's elements first.
	const call = enclosingCall(ctx.masked, fn.at);
	if (call && call.index === 0 && param.index === 0) {
		const method = /\.\s*([A-Za-z_$][\w$]*)\s*$/.exec(ctx.masked.slice(Math.max(0, call.open - 60), call.open));
		if (method && ITERATING_METHODS.has(method[1])) {
			const dot = call.open - method[0].length;
			const receiverEnd = ctx.masked[dot - 1] === '?' ? dot - 1 : dot;
			return applyPath(ctx, listElements(ctx, expressionStart(ctx.masked, receiverEnd), receiverEnd, env, depth + 1), param.path, depth + 1);
		}
	}
	return [unresolved(`${name} (a parameter of a callback)`)];
}

/** The argument ranges of every call of a named function in its file, or undefined when it is passed on as a value or never called. */
function callSites(ctx, fn) {
	const sites = [];
	for (const m of ctx.masked.matchAll(new RegExp(`(?<![\\w$.])${fn.name.replace(/\$/g, '\\$')}(?![\\w$])`, 'g'))) {
		if (m.index === fn.nameAt) continue;
		const after = /^\s*(?:<[^<>()]*>)?\(/.exec(ctx.masked.slice(m.index + m[0].length, m.index + m[0].length + 80));
		if (!after) return undefined;
		sites.push(callArguments(ctx.masked, m.index + m[0].length + after[0].length - 1));
	}
	return sites.length ? sites : undefined;
}

/** The ranges a function's result comes from: an arrow's expression, or each `return` of its body outside the functions within it. */
function returnRanges(ctx, fn) {
	if (fn.expression) return [fn.body];
	const ranges = [];
	const inner = ctx.functions.filter((f) => f !== fn && f.at > fn.body[0] && f.body[1] <= fn.body[1]);
	for (const m of ctx.masked.slice(fn.body[0], fn.body[1]).matchAll(/\breturn\b/g)) {
		const at = fn.body[0] + m.index;
		if (inner.some((f) => f.at <= at && at <= f.body[1])) continue;
		const end = valueEnd(ctx.masked, { valueAt: at + 6, value: '' });
		ranges.push([at + 6, Math.min(end, fn.body[1])]);
	}
	return ranges;
}

/** The functions a callee name calls: one of the file's, or one a support file exports. */
function calledFunctions(ctx, name, at, env, depth) {
	const vals = nameValues(ctx, name, at, env, depth + 1);
	// A declared function has no binding entry: find it by name.
	if (vals.length === 1 && vals[0].kind === 'unresolved') {
		// Every declaration of that name (two describe blocks may each declare one): all are read.
		const declared = ctx.functions.filter((f) => f.name === name && f.nameAt >= 0 && /^(?:async\s+)?function\b/.test(ctx.masked.slice(f.at, f.at + 16)));
		return declared.length ? declared.map((fn) => ({ fn, fnCtx: ctx })) : undefined;
	}
	const found = [];
	for (const v of vals) {
		if (v.kind === 'import') {
			const sctx = supportContext(ctx, v.module);
			const fn = sctx?.functions.find((f) => f.name === v.exported);
			if (!fn) return undefined;
			found.push({ fn, fnCtx: sctx });
		} else if (v.kind === 'expr') {
			const fn = v.ctx.functions.find((f) => f.at === v.from);
			if (!fn) return undefined;
			found.push({ fn, fnCtx: v.ctx });
		} else return undefined;
	}
	return found;
}

/**
 * The values an expression (masked[from, to) in ctx) may hold: a name through its binding or parameter, a member or an index
 * read off what it is taken from, a call to a function of the file or a support file through what it returns. Anything else is
 * itself (an `expr`, read by the caller: a literal, a template, an object or a list); `null` and `undefined` are nothing.
 */
function values(ctx, from, to, env, depth) {
	const { masked } = ctx;
	[from, to] = trimRange(masked, from, to);
	const e = masked.slice(from, to);
	if (!e || depth > MAX_DEPTH) return [unresolved(ctx.source.slice(from, to) || '(empty)')];
	if (e === 'null' || e === 'undefined') return [{ kind: 'nothing' }];
	if (IDENTIFIER.test(e)) return nameValues(ctx, e, from, env, depth + 1);
	const member = /(?:\?\.|!?\s*\.)\s*([A-Za-z_$][\w$]*)$/.exec(e);
	if (member && !/^\d/.test(e) && expressionStart(masked, to) <= from) {
		const base = values(ctx, from, from + member.index, env, depth + 1);
		return base.flatMap((v) => memberValues(ctx, v, member[1], depth + 1));
	}
	const last = e[e.length - 1];
	if (last === ']' && !isArrayLiteral(masked, from, to) && expressionStart(masked, to) <= from) {
		const open = opening(masked, to - 1);
		if (open > from) {
			const index = masked.slice(open + 1, to - 1).trim();
			const key = /^(['"])\s*\1$/.test(index) ? ctx.source.slice(open + 1, to - 1).trim().slice(1, -1) : undefined;
			const objectTo = masked[open - 1] === '.' && masked[open - 2] === '?' ? open - 2 : open;
			const base = values(ctx, from, objectTo, env, depth + 1);
			if (key !== undefined) return base.flatMap((v) => memberValues(ctx, v, key, depth + 1));
			if (/^\d+$/.test(index)) return base.flatMap((v) => elementValues(ctx, v, Number(index), depth + 1));
			return base.flatMap((v) => (v.kind === 'origin' || (v.kind === 'import' && v.origin) ? [{ kind: 'origin', any: false, text: `${v.text ?? v.name}[]` }] : elementsOfValue(ctx, v, depth + 1)));
		}
	}
	if (last === ')') {
		const open = opening(masked, to - 1);
		const callee = open > from ? masked.slice(from, open).replace(/\?\.\s*$/, '').replace(/<[^<>()]*>\s*$/, '').trim() : '';
		if (IDENTIFIER.test(callee)) {
			const called = calledFunctions(ctx, callee, from, env, depth + 1);
			if (!called) return [unresolved(ctx.source.slice(from, to))];
			const args = callArguments(masked, open);
			return called.flatMap(({ fn, fnCtx }) => {
				const frame = { fn, fnCtx, args, argsCtx: ctx, parent: env };
				const returns = returnRanges(fnCtx, fn);
				if (returns.length === 0) return [unresolved(`${callee}() (no return the check can read)`)];
				return returns.flatMap(([a, b]) => values(fnCtx, a, b, frame, depth + 1));
			});
		}
	}
	return [{ kind: 'expr', ctx, from, to, env }];
}

/** A value's member: an object literal's (written or shorthand), an origin's, an import's from its support file. */
function memberValues(ctx, v, prop, depth) {
	if (depth > MAX_DEPTH) return [unresolved(prop)];
	switch (v.kind) {
		case 'nothing':
			return [];
		case 'origin':
			return v.any || ORIGIN_ROOT.test(prop) ? [{ kind: 'origin', any: false, text: `${v.text}.${prop}` }] : [unresolved(`${v.text}.${prop}`)];
		case 'import': {
			if (v.origin || REVIEWED_ORIGINS.get(v.module)?.get(v.exported)?.has(prop)) return [{ kind: 'origin', any: false, text: `${v.name}.${prop}` }];
			return importValues(v, depth + 1).flatMap((x) => memberValues(ctx, x, prop, depth + 1));
		}
		case 'expr': {
			const { masked } = v.ctx;
			const [a, b] = trimRange(masked, v.from, v.to);
			if (!isObjectLiteral(masked, a, b)) {
				const reduced = values(v.ctx, a, b, v.env, depth + 1);
				if (reduced.length === 1 && reduced[0].kind === 'expr' && reduced[0].from === a && reduced[0].to === b) return [unresolved(`${v.ctx.source.slice(a, b)}.${prop}`)];
				return reduced.flatMap((x) => memberValues(ctx, x, prop, depth + 1));
			}
			const found = objectMember(v.ctx, a, b, prop);
			if (!found) return [unresolved(`${v.ctx.source.slice(a, Math.min(b, a + 40))}.${prop}`)];
			if (found.shorthand) return nameValues(v.ctx, prop, found.at, v.env, depth + 1);
			return values(v.ctx, found.from, found.to, v.env, depth + 1);
		}
		default:
			return [unresolved(`${v.text ?? ''}.${prop}`)];
	}
}

/** An object literal's member by name: its value's range, or the shorthand's place; undefined when absent, computed or spread over. */
function objectMember(ctx, from, to, prop) {
	let found;
	for (const [a, b] of splitTopAt(ctx.masked, from + 1, to - 1, /,/y)) {
		const item = ctx.masked.slice(a, b);
		if (!item.trim()) continue;
		if (/^\s*\.\.\./.test(item) || /^\s*\[/.test(item)) {
			found = undefined; // a spread or a computed key after the member may replace it
			continue;
		}
		const keyed = /^\s*([A-Za-z_$][\w$]*|(['"])\s*\2)\s*:(?!:)/.exec(item);
		if (keyed) {
			const lead = item.length - item.trimStart().length;
			const key = keyed[2] ? ctx.source.slice(a + lead + 1, a + lead + keyed[1].length - 1) : keyed[1];
			if (key === prop) found = { from: a + keyed[0].length, to: b };
			continue;
		}
		const name = /^\s*([A-Za-z_$][\w$]*)\s*$/.exec(item);
		if (name && name[1] === prop) found = { shorthand: true, at: a + item.indexOf(prop) };
	}
	return found;
}

/** What an import of a support file holds: its exported binding's values, or unresolved. */
function importValues(v, depth) {
	const sctx = supportContext(v.ctx, v.module);
	if (!sctx) return [unresolved(`${v.name} (imported from ${v.module})`)];
	const entry = latestBinding(sctx, v.exported, sctx.masked.length);
	if (entry && changedAfterWritten(sctx, v.exported)) return [unresolved(`${v.name} (changed in ${v.module} after it is written)`)];
	if (entry) return bindingValues(sctx, entry, undefined, depth + 1);
	return [unresolved(`${v.name} (imported from ${v.module})`)];
}

/** A value's element at an index: a pair's key or value, a list literal's element, else any element of the list. */
function elementValues(ctx, v, index, depth) {
	if (v.kind === 'pair') return index === 0 ? [v.key] : index === 1 ? [v.value] : [{ kind: 'nothing' }];
	return elementsOfValue(ctx, v, depth + 1);
}

/** The values a path (from patternNames) reads off each value. */
function applyPath(ctx, vals, path, depth) {
	let out = vals;
	for (const step of path) out = out.flatMap((v) => (typeof step === 'number' ? elementValues(ctx, v, step, depth + 1) : memberValues(ctx, v, step, depth + 1)));
	return out;
}

/** The elements of the list an expression (masked[from, to) in ctx) holds. */
function listElements(ctx, from, to, env, depth) {
	return elementsOfValue(ctx, { kind: 'expr', ctx, from, to, env }, depth);
}

/**
 * The elements of a list: a list literal's (spreads read through), Object.values or Object.entries of an object literal (an entry
 * is a key and value pair), a list method's receiver (filter, slice, sort), a list method's callback results (map, flatMap), or
 * the elements of what a name, a member or a call holds. An origin's elements are origins (apiHosts' hosts).
 */
function elementsOfValue(ctx, v, depth) {
	if (depth > MAX_DEPTH) return [unresolved('a list')];
	if (v.kind === 'nothing') return [];
	if (v.kind === 'origin') return [{ kind: 'origin', any: false, text: `${v.text}[]` }];
	if (v.kind === 'import') return v.origin ? [{ kind: 'origin', any: false, text: `${v.name}[]` }] : importValues(v, depth + 1).flatMap((x) => elementsOfValue(ctx, x, depth + 1));
	if (v.kind !== 'expr') return [unresolved(v.text ?? 'a list')];
	const c = v.ctx;
	const { masked } = c;
	const [a, b] = trimRange(masked, v.from, v.to);
	const e = masked.slice(a, b);
	if (isArrayLiteral(masked, a, b)) {
		return splitTopAt(masked, a + 1, b - 1, /,/y).flatMap(([p, q]) => {
			const item = masked.slice(p, q);
			if (!item.trim()) return [];
			if (/^\s*\.\.\./.test(item)) return listElements(c, p + item.indexOf('...') + 3, q, v.env, depth + 1);
			return [{ kind: 'expr', ctx: c, from: p, to: q, env: v.env }];
		});
	}
	const object = /^Object\s*\.\s*(entries|values|keys)\s*\(/.exec(e);
	if (object && closing(masked, a + object[0].length - 1) === b - 1) {
		const objects = values(c, a + object[0].length, b - 1, v.env, depth + 1);
		if (object[1] === 'keys') return objectKeys(c, objects, depth + 1);
		return objects.flatMap((o) => objectEntries(o, object[1], depth + 1));
	}
	if (e.endsWith(')')) {
		const open = opening(masked, b - 1);
		const method = /(?:\?\.|\.)\s*([A-Za-z_$][\w$]*)\s*$/.exec(masked.slice(a, open));
		if (method && open > a) {
			const receiverEnd = a + method.index;
			if (LIST_METHODS.has(method[1])) {
				const own = listElements(c, a, receiverEnd, v.env, depth + 1);
				if (method[1] !== 'concat') return own;
				return [...own, ...callArguments(masked, open).flatMap(([p, q]) => listElements(c, p, q, v.env, depth + 1))];
			}
			if (method[1] === 'map' || method[1] === 'flatMap') {
				// The callback's results, its first parameter bound to the receiver's elements.
				const arg = callArguments(masked, open)[0];
				const fn = arg && c.functions.find((f) => f.at === trimRange(masked, arg[0], arg[1])[0]);
				if (!fn) return [unresolved(c.source.slice(a, b))];
				const results = returnRanges(c, fn).flatMap(([p, q]) => values(c, p, q, v.env, depth + 1));
				return method[1] === 'flatMap' ? results.flatMap((r) => elementsOfValue(ctx, r, depth + 1)) : results;
			}
		}
	}
	const reduced = values(c, a, b, v.env, depth + 1);
	if (reduced.length === 1 && reduced[0].kind === 'expr' && reduced[0].from === a && reduced[0].to === b) return [unresolved(c.source.slice(a, b))];
	return reduced.flatMap((x) => elementsOfValue(ctx, x, depth + 1));
}

/** Object.values (each member's value) or Object.entries (a pair of the key and the value) of an object literal; anything else is unresolved. */
function objectEntries(o, how, depth) {
	if (o.kind === 'nothing') return [];
	if (o.kind === 'origin') return [how === 'entries' ? { kind: 'pair', key: { kind: 'literal', text: '' }, value: { kind: 'origin', any: false, text: `${o.text}[]` } } : { kind: 'origin', any: false, text: `${o.text}[]` }];
	if (o.kind === 'import') {
		if (o.origin) return objectEntries({ kind: 'origin', any: false, text: o.name }, how, depth + 1);
		return importValues(o, depth + 1).flatMap((x) => objectEntries(x, how, depth + 1));
	}
	if (o.kind !== 'expr') return [unresolved(o.text ?? 'an object')];
	const { masked, source } = o.ctx;
	const [a, b] = trimRange(masked, o.from, o.to);
	if (!isObjectLiteral(masked, a, b)) {
		const reduced = values(o.ctx, a, b, o.env, depth + 1);
		if (reduced.length === 1 && reduced[0].kind === 'expr' && reduced[0].from === a && reduced[0].to === b) return [unresolved(source.slice(a, b))];
		return reduced.flatMap((x) => objectEntries(x, how, depth + 1));
	}
	return splitTopAt(masked, a + 1, b - 1, /,/y).flatMap(([p, q]) => {
		const item = masked.slice(p, q);
		if (!item.trim()) return [];
		if (/^\s*(?:\.\.\.|\[)/.test(item)) return [unresolved(source.slice(p, q))];
		const keyed = /^\s*([A-Za-z_$][\w$]*|(['"])\s*\2)\s*:(?!:)/.exec(item);
		const name = /^\s*([A-Za-z_$][\w$]*)\s*$/.exec(item)?.[1];
		const value = keyed ? { kind: 'expr', ctx: o.ctx, from: p + keyed[0].length, to: q, env: o.env } : name ? { kind: 'expr', ctx: o.ctx, from: p, to: q, env: o.env } : unresolved(source.slice(p, q));
		if (how === 'values') return [value];
		const lead = item.length - item.trimStart().length;
		const key = keyed ? (keyed[2] ? source.slice(p + lead + 1, p + lead + keyed[1].length - 1) : keyed[1]) : name;
		return [{ kind: 'pair', key: { kind: 'literal', text: key ?? '' }, value }];
	});
}

/** The keys of object literals (Object.keys, for...in): literals; anything else is unresolved. */
function objectKeys(ctx, objects, depth) {
	return objects.flatMap((o) => {
		if (o.kind === 'nothing') return [];
		const pairs = objectEntries(o, 'entries', depth + 1);
		return pairs.map((p) => (p.kind === 'pair' ? p.key : p));
	});
}

/**
 * The forms an address may take: each a list of parts (literal text, an origin, or an expression the check reads for secrets
 * only) and whether it is absolute (it starts with an origin or `http(s):`). The address resolves when its leading part does:
 * a literal, a regular expression, a predicate, an origin, or a name, a member or a call read down to one of those. Its
 * other parts are inlined when they resolve, and kept as expressions otherwise.
 */
function addressForms(ctx, v, depth) {
	if (depth > MAX_DEPTH) return { forms: [], unresolved: v.text ?? 'too deep' };
	switch (v.kind) {
		case 'nothing':
			return { forms: [] };
		case 'origin':
			return { forms: [{ parts: [{ origin: v.text }], absolute: true }] };
		case 'literal':
			return { forms: [{ parts: [{ text: v.text }], absolute: /^https?:/i.test(v.text) }] };
		case 'import': {
			if (v.origin) return { forms: [{ parts: [{ origin: v.name }], absolute: true }] };
			const all = importValues(v, depth + 1);
			return union(all.map((x) => addressForms(ctx, x, depth + 1)));
		}
		case 'expr':
			return exprForms(v.ctx, v.from, v.to, v.env, depth + 1);
		case 'append': {
			// A variable with `+=`: what it was set to, followed by what each `+=` adds.
			const base = union(v.base.map((x) => addressForms(ctx, x, depth + 1)));
			if (base.unresolved !== undefined) return base;
			let forms = base.forms;
			for (const [a, b] of v.adds) forms = concatenate(forms, laterPart(v.ctx, a, b, v.env, depth));
			return forms === null ? { forms: [], unresolved: `more than ${MAX_FORMS} forms` } : { forms: [...base.forms, ...forms] };
		}
		default:
			return { forms: [], unresolved: v.text ?? '?' };
	}
}

/** The forms of every alternative together; unresolved when any is. */
function union(results) {
	const forms = results.flatMap((r) => r.forms);
	const bad = results.find((r) => r.unresolved !== undefined);
	if (bad) return { forms, unresolved: bad.unresolved };
	if (forms.length > MAX_FORMS) return { forms: forms.slice(0, MAX_FORMS), unresolved: `more than ${MAX_FORMS} forms` };
	return { forms };
}

/** Every form of a followed by every form of b (a concatenation), keeping a's absolute flag; null past MAX_FORMS. */
function concatenate(a, b) {
	if (a === null || a.length * b.length > MAX_FORMS) return null;
	const forms = [];
	for (const x of a) {
		for (const y of b) {
			if (x.parts.length + y.parts.length > MAX_PARTS) return null;
			forms.push({ parts: [...x.parts, ...y.parts], absolute: x.absolute });
		}
	}
	return forms;
}

/** A later part of an address: its forms when it resolves, else itself as an expression read for secrets. */
function laterPart(ctx, from, to, env, depth) {
	const r = exprForms(ctx, from, to, env, depth + 1);
	if (r.unresolved === undefined && r.forms.length > 0) return r.forms;
	return [{ parts: [{ expr: [from, to], ctx }], absolute: false }];
}

/** The top-level `?` and its `:` of a conditional in masked[from, to), or undefined. */
function conditionalAt(masked, from, to) {
	let depth = 0;
	let question = -1;
	let nested = 0;
	for (let i = from; i < to; i++) {
		const c = masked[i];
		if (c === '`') {
			const end = skipTemplate(masked, i);
			if (end < 0) return undefined;
			i = end - 1;
		} else if ('([{'.includes(c)) depth++;
		else if (')]}'.includes(c)) depth--;
		else if (depth === 0 && c === '?') {
			if (masked[i + 1] === '?' || masked[i - 1] === '?') {
				i += masked[i + 1] === '?' ? 1 : 0;
				continue;
			}
			if (masked[i + 1] === '.' && !/\d/.test(masked[i + 2] ?? '')) continue;
			if (question < 0) question = i;
			else nested++;
		} else if (depth === 0 && c === ':' && question >= 0) {
			if (nested === 0) return { question, colon: i };
			nested--;
		}
	}
	return undefined;
}

let frameIds = 0;
/**
 * The forms of an expression (masked[from, to) in ctx), each computed once per call being read (a chain of templates that
 * each name the one before several times is read in linear time, not once per path).
 */
function exprForms(ctx, from, to, env, depth) {
	ctx.formsCache ??= new Map();
	if (env && env.id === undefined) env.id = ++frameIds;
	const key = `${env ? env.id : 0}:${from}:${to}`;
	const cached = ctx.formsCache.get(key);
	if (cached) return cached;
	const result = readForms(ctx, from, to, env, depth);
	ctx.formsCache.set(key, result);
	return result;
}

/** exprForms without the cache. */
function readForms(ctx, from, to, env, depth) {
	const { masked, source } = ctx;
	[from, to] = trimRange(masked, from, to);
	const e = masked.slice(from, to);
	const text = source.slice(from, to);
	if (!e || depth > MAX_DEPTH) return { forms: [], unresolved: text || '(empty)' };
	// A predicate (waitForURL, waitForResponse): no address of its own.
	if (/^(?:async\b\s*)?function\b/.test(e) || ctx.functions.some((f) => f.at === from && f.body[1] >= to - 1)) return { forms: [{ parts: [], absolute: false }] };
	const logical = splitTopAt(masked, from, to, /\?\?|\|\||&&/y);
	if (logical.length > 1) return union(logical.map(([a, b]) => exprForms(ctx, a, b, env, depth + 1)));
	const conditional = conditionalAt(masked, from, to);
	if (conditional) return union([exprForms(ctx, conditional.question + 1, conditional.colon, env, depth + 1), exprForms(ctx, conditional.colon + 1, to, env, depth + 1)]);
	const sum = splitTopAt(masked, from, to, /\+(?![+=])/y);
	if (sum.length > 1) {
		const first = exprForms(ctx, sum[0][0], sum[0][1], env, depth + 1);
		if (first.unresolved !== undefined) return first;
		let forms = first.forms;
		for (const [a, b] of sum.slice(1)) forms = concatenate(forms, laterPart(ctx, a, b, env, depth));
		return forms === null ? { forms: [], unresolved: `more than ${MAX_FORMS} forms` } : { forms };
	}
	if (/^(['"])[^\n]*\1$/.test(e) && e.length >= 2 && !/['"]/.test(e.slice(1, -1))) {
		const literal = text.slice(1, -1);
		return { forms: [{ parts: [{ text: literal }], absolute: /^https?:/i.test(literal) }] };
	}
	if (/^\/ *\/[a-z]*$/.test(e)) return { forms: [{ parts: [], absolute: false }] }; // a regular expression
	const pattern = /^new\s+RegExp\s*\(/.exec(e);
	if (pattern && closing(masked, from + pattern[0].length - 1) === to - 1) {
		const arg = callArguments(masked, from + pattern[0].length - 1)[0];
		if (!arg) return { forms: [{ parts: [], absolute: false }] };
		return { forms: laterPart(ctx, arg[0], arg[1], env, depth).map((f) => ({ ...f, absolute: false })) };
	}
	if (e[0] === '`' && skipTemplate(masked, from) === to) {
		let forms = null;
		let staticFrom = from + 1;
		const addText = (a, b) => {
			const t = source.slice(a, b);
			if (forms === null) forms = [{ parts: [{ text: t }], absolute: /^https?:/i.test(t) }];
			else if (t && forms !== null) forms = forms.map((f) => ({ ...f, parts: [...f.parts, { text: t }] }));
		};
		for (let k = from + 1; k < to - 1; k++) {
			if (masked[k] !== '$' || masked[k + 1] !== '{') continue;
			const close = closing(masked, k + 1);
			if (close < 0) return { forms: [], unresolved: text };
			if (k > staticFrom || forms !== null) addText(staticFrom, k);
			if (forms === null) {
				const lead = exprForms(ctx, k + 2, close, env, depth + 1);
				if (lead.unresolved !== undefined) return lead;
				forms = lead.forms;
			} else forms = concatenate(forms, laterPart(ctx, k + 2, close, env, depth));
			if (forms === null) return { forms: [], unresolved: `more than ${MAX_FORMS} forms` };
			staticFrom = close + 1;
			k = close;
		}
		addText(staticFrom, to - 1);
		return { forms };
	}
	if (e.endsWith(')')) {
		const open = opening(masked, to - 1);
		const head = open > from ? masked.slice(from, open) : '';
		const method = /(?:\?\.|\.)\s*([A-Za-z_$][\w$]*)\s*$/.exec(head);
		if (method && STRING_METHODS.has(method[1])) {
			const receiver = exprForms(ctx, from, from + method.index, env, depth + 1);
			if (receiver.unresolved !== undefined) return receiver;
			let forms = receiver.forms.map((f) => ({ ...f, absolute: f.absolute && KEEPS_ORIGIN.has(method[1]) }));
			for (const [a, b] of callArguments(masked, open)) forms = concatenate(forms, laterPart(ctx, a, b, env, depth));
			return forms === null ? { forms: [], unresolved: `more than ${MAX_FORMS} forms` } : { forms };
		}
		const fn = head.trim();
		if (ARGUMENT_FUNCTIONS.has(fn)) {
			const arg = callArguments(masked, open)[0];
			if (!arg) return { forms: [], unresolved: text };
			const inner = exprForms(ctx, arg[0], arg[1], env, depth + 1);
			if (inner.unresolved !== undefined) return inner;
			return { forms: inner.forms.map((f) => ({ ...f, absolute: f.absolute && ARGUMENT_FUNCTIONS.get(fn) })) };
		}
	}
	const vals = values(ctx, from, to, env, depth + 1);
	if (vals.length === 1 && vals[0].kind === 'expr' && vals[0].ctx === ctx && vals[0].from === from && vals[0].to === to) return { forms: [], unresolved: text };
	return union(vals.map((v) => addressForms(ctx, v, depth + 1)));
}

/** ARV-224: a params key, a query parameter or a matcher argument that names a secret, without case, dashes or underscores (api-key, access_token, Token). */
const secretParamKey = (key) => /^(?:accesstoken|token|key|apikey|secret|clientsecret|password|passwd|credential|jwt|sig|signature|otp|totp|totpcode|session|sessionid|auth|authorization)$/.test(key.toLowerCase().replace(/[-_]/g, '')) || SECRET_NAME.test(key);
/** ARV-224: a URL matcher's argument that names a secret parameter or a secret's prefix: the page's address then holds one, which a failure prints. */
const SECRET_IN_ADDRESS = /(?<![A-Za-z0-9_])(?:access_token|token|key|apikey|api_key|api-key|secret|client_secret|password|credential|jwt)(?![A-Za-z0-9_])|ard[pk]_|ics_/i;

/**
 * Why an expression inside an address puts a secret there: anywhere, a secret value (secretValue: a secret-named member or
 * variable, a part or encoding of one, a call that issues one, an element that reveals one); in the query or fragment, also a
 * quoted secret or a name that looks like one (the ARV-152 rule).
 */
function expressionCarries(part, inQuery) {
	const { ctx, expr } = part;
	const text = ctx.masked.slice(expr[0], expr[1]);
	const reason = secretValue(text, expr[0], ctx.entries) ? 'a secret' : inQuery ? (quotes(text, expr[0], ctx.entries) ?? (SECRET_EXPRESSION.test(text) ? 'a secret by its name' : undefined)) : undefined;
	return reason ? `puts ${ctx.source.slice(expr[0], expr[1]).trim().slice(0, 40)} in its address (${reason})` : undefined;
}

/**
 * Why an address carries a secret where the report keeps it, and whether it names a secret parameter at all. A request
 * context's call and a navigation are report steps whose subtitle is the address with its query; Playwright leaves the
 * fragment out only when the address is absolute (starts with an origin), and keeps a relative one whole, and waitForURL,
 * waitForRequest, waitForResponse and route keep their argument as written. A secret parameter (access_token=, token=,
 * key=, secret=, password=) counts whatever its value; an expression counts when it holds a secret, and in the query or
 * fragment also when its name only looks like one.
 */
function formsCarry(forms, fragmentKept) {
	let named = false;
	let reason;
	for (const form of forms) {
		const exprs = [];
		let s = '';
		for (const part of form.parts) {
			if (part.text !== undefined) s += part.text;
			else if (part.origin !== undefined) s += '\u0001';
			else {
				s += `\u0002${exprs.length}\u0003`;
				exprs.push(part);
			}
		}
		for (const m of s.matchAll(/(?:^|[?&#;])([A-Za-z_][\w.-]*)=/g)) if (secretParamKey(m[1])) named = true;
		if (reason) continue;
		const kept = form.absolute && !fragmentKept ? s.split('#')[0] : s;
		const query = kept.search(/[?#]/);
		for (const m of kept.matchAll(/(?:^|[?&#;])([A-Za-z_][\w.-]*)=/g)) {
			if (secretParamKey(m[1])) {
				reason = `sends ${m[1]}= in its address`;
				break;
			}
		}
		if (reason) continue;
		for (const m of kept.matchAll(/\u0002(\d+)\u0003/g)) {
			reason = expressionCarries(exprs[Number(m[1])], query >= 0 && m.index > query);
			if (reason) break;
		}
	}
	return { reason, named };
}

/** ARV-224: why a request's options argument (or the params it holds) puts a secret in the address the step keeps, or undefined. */
function paramsCarry(ctx, from, to) {
	// Options changed after they are written (`options.params = ...`, Object.assign) read as unresolved (changedAfterWritten).
	const objects = values(ctx, from, to, undefined, 0);
	for (const o of objects) {
		if (o.kind === 'nothing') continue;
		if (o.kind !== 'expr') return 'sends options the check cannot read: write them in the call';
		const [a, b] = trimRange(o.ctx.masked, o.from, o.to);
		if (!isObjectLiteral(o.ctx.masked, a, b)) return 'sends options the check cannot read: write them in the call';
		for (const [p, q] of splitTopAt(o.ctx.masked, a + 1, b - 1, /,/y)) {
			const item = o.ctx.masked.slice(p, q);
			if (/^\s*\.\.\./.test(item)) return 'spreads options the check cannot read: write each in the call';
			if (/^\s*\[/.test(item)) return 'has an option with a computed name the check cannot read: write it in the call';
		}
		const params = objectMember(o.ctx, a, b, 'params');
		if (!params) continue;
		const pvals = params.shorthand ? nameValues(o.ctx, 'params', params.at, o.env, 0) : values(o.ctx, params.from, params.to, o.env, 0);
		for (const pv of pvals) {
			if (pv.kind === 'nothing') continue;
			if (pv.kind !== 'expr') return 'sends params the check cannot read: write them in the call';
			const c = pv.ctx;
			const [x, y] = trimRange(c.masked, pv.from, pv.to);
			if (!isObjectLiteral(c.masked, x, y)) return 'sends params the check cannot read: write them in the call';
			for (const [m, n] of splitTopAt(c.masked, x + 1, y - 1, /,/y)) {
				const text = c.masked.slice(m, n).trim();
				if (!text) continue;
				if (text.startsWith('...')) return 'spreads params the check cannot read: write each in the call';
				if (text.startsWith('[')) return 'has a params key the check cannot read (computed): write each key in the call';
				const keyed = /^([A-Za-z_$][\w$]*|(['"])\s*\2)\s*:(?!:)/.exec(text);
				const lead = c.masked.slice(m, n).length - c.masked.slice(m, n).trimStart().length;
				const key = keyed ? (keyed[2] ? c.source.slice(m + lead + 1, m + lead + keyed[1].length - 1) : keyed[1]) : text;
				if (secretParamKey(key)) return `sends ${key} in its params (the address keeps them)`;
				const valueText = keyed ? text.slice(keyed[0].length) : text;
				const at = keyed ? m + lead + keyed[0].length : m + lead;
				if (SECRET_EXPRESSION.test(valueText) || quotes(valueText, at, c.entries) || secretValue(valueText, at, c.entries)) return `puts ${c.source.slice(at, n).trim().slice(0, 40)} in its params (the address keeps them)`;
			}
		}
	}
	return undefined;
}

/**
 * ARV-224: whether a matcher's subject is an address a failing matcher prints: anything that holds a `.url()` read (a page's,
 * a frame's, a request's or a response's) or reads `location` inside page.evaluate, however it is cut or wrapped (`split`,
 * `slice`, `replace`, decodeURIComponent, a template, an object holding it), or a name bound to one; for expect.poll, what its
 * function returns. Not when its outermost operation gives a boolean or a number (includes, startsWith, endsWith, has, test,
 * indexOf, a comparison, `!`, length) or reads the pathname, origin, host, hostname, protocol or port, or one search parameter
 * by a name that is not a secret's.
 */
function readsAddress(ctx, from, to, poll, depth = 0, seen = new Set()) {
	const { masked } = ctx;
	[from, to] = trimRange(masked, from, to);
	if (from >= to) return false;
	ctx.uid ??= ++contextIds;
	const key = `${ctx.uid}:${from}:${to}:${poll ? 1 : 0}`;
	if (seen.has(key)) return false; // read already on this path (a cycle, or a value reached twice)
	seen.add(key);
	if (depth > 40) return true; // a chain this long is not read: fail closed
	if (poll) {
		const fn = ctx.functions.find((f) => f.at === from);
		return fn !== undefined && returnRanges(ctx, fn).some(([a, b]) => readsAddress(ctx, a, b, false, depth + 1, seen));
	}
	if (outermostIsSafe(ctx, from, to)) return false;
	// The expression's own reads: a `.url()` inside a function within it (a waitForResponse predicate) is that function's.
	let e = masked.slice(from, to);
	for (const f of ctx.functions) {
		if (f.at >= from && f.body[1] < to && !(/\bevaluate\s*\(\s*$/.test(masked.slice(Math.max(from, f.at - 40), f.at)))) e = e.slice(0, f.at - from) + ' '.repeat(f.body[1] + 1 - f.at) + e.slice(f.body[1] + 1 - from);
	}
	// A call of a function the check reads (the file's, or a support module's export) gives what that function returns, read
	// below: its arguments are not its value (a helper given `page.url()` may return a number), and its parameters lead back to them.
	for (const m of e.matchAll(/(?<![\w$.])([A-Za-z_$][\w$]*)\s*(?:<[^<>()]*>)?\(/g)) {
		const imported = ctx.imports.get(m[1]);
		const readable = ctx.functions.some((f) => f.name === m[1]) || (imported !== undefined && supportContext(ctx, imported.module)?.functions.some((f) => f.name === imported.exported));
		if (!readable) continue;
		const open = m.index + m[0].length - 1;
		const close = closing(e, open);
		if (close > open) e = e.slice(0, open + 1) + e.slice(open + 1, close).replace(/[^\n]/g, ' ') + e.slice(close);
	}
	if (/\.\s*url\s*\(\s*\)/.test(e) || (/\bevaluate\s*\(/.test(e) && /\blocation\b/.test(e))) return true;
	// Every name in it: a call through the returns of the file's functions of that name or a support module's export
	// (`hashOf(page)`); a name through its binding (`where`, `${where}`, decodeURIComponent(where), { where }), a loop
	// variable through its list, a parameter through the argument at every call of its function (a list method's callback
	// through the list). A name read only for a part with no secret (`url.pathname`, `url.searchParams.keys()`) is left alone.
	for (const m of e.matchAll(/(?<![\w$])([A-Za-z_$][\w$]*)(?![\w$])/g)) {
		const name = m[1];
		if (/^(?:await|new|typeof|void|true|false|null|undefined|this|of|in|as)$/.test(name)) continue;
		if (e[m.index - 1] === '.' && e[m.index - 2] !== '.') continue; // a member, not a name (a spread's `...name` is one)
		const at = from + m.index;
		const chain = /^[A-Za-z_$][\w$]*((?:\s*(?:\?\.|!?\.)\s*[A-Za-z_$][\w$]*(?:\s*\([^()]*\))?|\s*\[[^\]]*\])*)/.exec(e.slice(m.index))[0];
		if (/^\s*(?:<[^<>()]*>)?\(/.test(e.slice(m.index + name.length))) {
			if (ctx.functions.some((f) => f.name === name && returnRanges(ctx, f).some(([a, b]) => readsAddress(ctx, a, b, false, depth + 1, seen)))) return true;
			const imported = ctx.imports.get(name);
			const sctx = imported && supportContext(ctx, imported.module);
			if (sctx && sctx.functions.some((f) => f.name === imported.exported && returnRanges(sctx, f).some(([a, b]) => readsAddress(sctx, a, b, false, depth + 1, seen)))) return true;
			continue;
		}
		if (chain.length > name.length && outermostIsSafe(ctx, at, at + chain.trimEnd().length)) continue;
		const fn = parameterFunction(ctx, name, at);
		const b = latestBinding(ctx, name, at);
		if (b !== undefined && (!fn || b.at > fn.at)) {
			const [a, z] = b.kind === 'loop' ? [b.iterableAt, b.iterableAt + b.iterable.length] : [b.valueAt, valueEnd(masked, b)];
			if (readsAddress(ctx, a, z, false, depth + 1, seen)) return true;
		} else if (fn && parameterReadsAddress(ctx, fn, name, depth + 1, seen)) return true;
	}
	return false;
}

let contextIds = 0;

/** Whether a parameter may hold an address read: the argument at a call of its named function, or a list method's callback's list. */
function parameterReadsAddress(ctx, fn, name, depth, seen) {
	const param = fn.params.find((p) => p.name === name);
	if (fn.name) {
		const sites = callSites(ctx, fn) ?? [];
		return sites.some((args) => {
			const arg = args[param.index] ?? param.def;
			return arg !== undefined && readsAddress(ctx, arg[0], arg[1], false, depth + 1, seen);
		});
	}
	const call = enclosingCall(ctx.masked, fn.at);
	if (!call || call.index !== 0 || param.index !== 0) return false;
	const method = /\.\s*([A-Za-z_$][\w$]*)\s*$/.exec(ctx.masked.slice(Math.max(0, call.open - 60), call.open));
	if (!method || !ITERATING_METHODS.has(method[1])) return false;
	const dot = call.open - method[0].length;
	const receiverEnd = ctx.masked[dot - 1] === '?' ? dot - 1 : dot;
	return readsAddress(ctx, expressionStart(ctx.masked, receiverEnd), receiverEnd, false, depth + 1, seen);
}

/** Whether the outermost operation of masked[from, to) gives a boolean or a number, or reads a part of an address that holds no secret. */
function outermostIsSafe(ctx, from, to) {
	const { masked, source } = ctx;
	const e = masked.slice(from, to);
	if (/^(?:!|typeof\b|void\b)/.test(e)) return true;
	// A comparison gives a boolean; `&&`, `||` and `??` give one of their operands, so they are not safe.
	if (splitTopAt(masked, from, to, /===|!==|==|!=|<=|>=|\binstanceof\b|\s<\s|\s>\s/y).length > 1) return true;
	if (/\.\s*(?:length|pathname|origin|host|hostname|protocol|port)\s*$/.test(e)) return true;
	if (e.endsWith(')')) {
		const open = opening(masked, to - 1);
		const head = masked.slice(from, open);
		const method = /(?:\?\.|\.)\s*([A-Za-z_$][\w$]*)\s*$/.exec(head)?.[1];
		if (method && /^(?:includes|startsWith|endsWith|has|test|indexOf|lastIndexOf|search|localeCompare)$/.test(method)) return true;
		// The names of the search parameters, not their values.
		if (method === 'keys' && /\bsearchParams\s*\.\s*$/.test(head.slice(0, head.lastIndexOf('keys')))) return true;
		if (/^(?:Boolean|Number|parseInt|parseFloat)$/.test(head.trim())) return true;
		// One search parameter by a name that is not a secret's prints that parameter only.
		if (method === 'get' || method === 'getAll') {
			const arg = source.slice(open + 1, to - 1).trim();
			if (/\bsearchParams\s*\.\s*$/.test(head.slice(0, head.lastIndexOf(method))) && /^(['"])[^'"]*\1$/.test(arg) && !SECRET_IN_ADDRESS.test(arg)) return true;
		}
	}
	return false;
}

/** Findings in one file's source: "line: what", for every place a report would keep an answer or a secret. */
export function findings(source, support = []) {
	const masked = mask(source);
	const entries = bindings(masked, source);
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
	// Request contexts' calls, navigations and waits for an address: the URL is in the report (a request's params join its query).
	// ARV-224: each address is read down to where it comes from (addressForms); one that does not end in a literal or an imported
	// origin could carry anything, a display's key read from the page among them. A file whose addresses name a secret
	// parameter holds its URL matchers to booleans (below).
	const context = addressContext(source, masked, entries, support);
	let secretAddress = false;
	for (const m of masked.matchAll(/(?<![\w$])((?:[\w$]+\s*\.\s*)*(?:request|[A-Za-z]+Request|requestContext|apiContext))\s*\.\s*(get|post|put|patch|delete|head|fetch)\s*\(|(?<=[\w$\])!])\s*\??\.\s*(goto|waitForURL|waitForRequest|waitForResponse|route|unroute)\s*\(/g)) {
		const args = callArguments(masked, m.index + m[0].length - 1);
		const at = args[0];
		if (!at) continue;
		const what = m[2] ? `request URL (${m[2]})` : m[3] === 'goto' ? 'navigation URL' : `awaited or routed URL (${m[3]})`;
		const { forms, unresolved: unread } = exprForms(context, at[0], at[1], undefined, 0);
		const { reason, named } = formsCarry(forms, m[3] !== undefined && m[3] !== 'goto');
		if (named) secretAddress = true;
		if (reason) report(at, what, reason);
		else if (unread !== undefined) report(at, what, `comes from something the check cannot read down to a literal or an imported origin (${unread}): build the address from literals and an origin (webUrl, hosts.main) in this file`);
		if (m[2] && args[1]) {
			const carried = paramsCarry(context, args[1][0], args[1][1]);
			if (carried) report(args[1], `request params (${m[2]})`, carried);
		}
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
		// ARV-224: a URL matcher prints the page's address whole (fragment included) when it fails, and so does any matcher on an
		// address read with .url() (or its hash, href, search or a search parameter). In a file whose addresses carry a secret
		// parameter, every such matcher is a finding; in any file, one whose argument names a secret parameter or a secret's prefix.
		const namedParameter = /searchParams\s*\.\s*get(?:All)?\s*\(\s*(['"])([^'"]*)\1\s*\)/.exec(source.slice(subject[0], subject[1]));
		const matcherArguments = callArguments(masked, close + chain[0].length);
		// The subject is read as an address only where a rule below needs it (its cost is the reading of every name in it).
		const needed = secretAddress || namedParameter !== null || matcherArguments.some((at) => SECRET_IN_ADDRESS.test(source.slice(at[0], at[1])));
		const onAddress = /^(?:not\.)?toHaveURL$/.test(matcher) || (needed && readsAddress(context, subject[0], subject[1], /poll/.test(m[1] ?? '')));
		if (onAddress && namedParameter && SECRET_IN_ADDRESS.test(namedParameter[2]) && !PRESENCE.has(matcher))
			report(subject, `matcher subject (${matcher})`, "reads a secret parameter of an address, which a failing matcher prints: assert a boolean (`url.searchParams.has('key')`)");
		else if (onAddress && secretAddress && !PRESENCE.has(matcher))
			report(subject, `matcher on an address (${matcher})`, "matches an address by value in a file whose addresses carry a secret parameter, and a failing matcher prints the address whole: read it into a boolean (`new URL(page.url()).hash.includes('key=')`)");
		for (const at of matcherArguments) {
			if (secretValue(masked.slice(at[0], at[1]), at[0], entries)) report(at, `matcher argument (${matcher})`, 'is a secret, which a failing matcher prints: compare as a boolean (`text.includes(secret)`, `a === b`)');
			else if (onAddress && !secretAddress && !PRESENCE.has(matcher) && SECRET_IN_ADDRESS.test(source.slice(at[0], at[1])))
				report(at, `matcher argument (${matcher})`, "names a secret parameter of an address a failing matcher prints whole: read the address into a boolean (`new URL(page.url()).hash.includes('key=')`)");
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
				return { name: name.replace(/\.ts$/, ''), source, masked: mask(source) };
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
		"await expect(page.getByTestId('credential-value')).toHaveText(/ardk_/);",
		// ARV-224: an address read through its bindings, a function's parameters and a loop's list, down to a literal or an origin.
		"async function open(page: Page, path: string) {\n\tawait page.goto(path);\n}",
		"async function open(page: Page, path: string) {\n\tawait page.goto(path);\n}\nawait open(page, '/live');\nawait open(page, `/display?code=${c}#key=${display.credential}`);",
		"const show = async (where: string) => {\n\tawait page.goto(where);\n};\nawait show(elsewhere.address);",
		"const playerBase = `/display?code=${c}#key=${encodeURIComponent(display.credential)}`;\nawait page.goto(`${playerBase}`);",
		"const a = `/display?code=${c}#key=${encodeURIComponent(display.credential)}`;\nconst b = a;\nawait page.goto(b);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(shown);",
		"await page.goto(shown);",
		"for (const path of ['/live', elsewhere.address]) await page.goto(path);",
		"const list = ['/live', elsewhere.address];\nfor (const path of list) await page.goto(path);",
		"const documented = { main: hosts.main, other: elsewhere.address } as const;\nfor (const [name, url] of Object.entries(documented)) await request.get(`${url}/openapi/v1.json`);",
		"await page.goto(ready ? '/live' : elsewhere.address);",
		"await page.goto(chosen ?? elsewhere.address);",
		"await page.goto(shown ?? '/live');",
		"await page.goto(ready ? elsewhere.address : '/live');",
		// A request's params join its query.
		"await request.get(`${hosts.main}/api/v1/x`, { params: { token: accessToken } });",
		"await request.get(`${hosts.main}/api/v1/x`, { params: { access_token: t } });",
		"await request.get(`${hosts.main}/api/v1/x`, { params: { code: display.credential } });",
		"await request.get(`${hosts.main}/api/v1/x`, { params: { 'api-key': k, code: 'DMO' } });",
		"await request.get(`${hosts.main}/api/v1/x`, { params: query });",
		"await request.get(`${hosts.main}/api/v1/x`, { params: { ...extra } });",
		"await request.get(`${hosts.main}/api/v1/x`, { params: new URLSearchParams(q) });",
		"await request.get(`${hosts.main}/api/v1/x`, { params: build() });",
		"await request.get(`${hosts.main}/api/v1/x`, options);",
		"await request.get(`${hosts.main}/api/v1/x`, withAuth());",
		"const options = { params: { token: t } };\nawait request.get(`${hosts.main}/api/v1/x`, options);",
		"const query = { key: k };\nawait request.get(`${hosts.main}/api/v1/x`, { params: query });",
		"await page.request.post(`${hosts.main}/api/v1/x`, { data, params: { secret } });",
		// waitForURL keeps its argument as written: the fragment counts on an absolute address too.
		"await page.waitForURL(`${webUrl}/display?code=${c}#key=${display.credential}`);",
		"await page.waitForURL(playerAddress);",
		// A URL matcher, or a matcher on an address read with .url(), naming a secret parameter: a failure prints the address whole.
		"await expect(page).not.toHaveURL(/#key=/);",
		"await expect(page).toHaveURL(/[?&]token=/);",
		"await expect(second).toHaveURL(new RegExp(`access_token=`));",
		"expect(page.url()).not.toContain('#key=');",
		"expect(response.url()).toMatch(/password=/);",
		// ARV-224 round 2: an address is absolute by what its leading part resolves to, and its parameters are read across interpolations.
		"const playerBase = `/display?code=${code}`;\nawait page.goto(`${playerBase}#key=${encodeURIComponent(issued.credential)}`);",
		"const playerUrl = '/display?code=DMO';\nawait page.goto(`${playerUrl}#key=${k}`);",
		"const url = `${hosts.main}/api/v1/x?code=1`;\nawait request.get(`${url}&access_token=${t}`);",
		"await request.get(`${where}&key=${k}`);",
		// Members and calls read down to what they hold, never passed because a quote sits nearby.
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(shown.trim());",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(shown.slice(0));",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto('/'.concat(shown));",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto('/display'.replace('/display', shown));",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst display = { id: 'x', path: shown };\nawait page.goto(display.path);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst screens = [{ path: '/live' }, { path: shown }];\nfor (const screen of screens) await page.goto(screen.path);",
		"function playerUrl() {\n\treturn page.getByTestId('credential-value').textContent();\n}\nawait page.goto(await playerUrl());",
		"const issued = await (await request.post('/api/v1/displays')).json();\nawait page.goto(issued.address.trim());",
		"const where = await playerAddressOf(page);\nawait page.goto(where);",
		"for (const row of rowsOf(list)) await request.fetch(row.path);",
		// Only an import (or the baseURL fixture) is an origin; every parameter resolves through its calls.
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\n[shown].forEach((url) => page.goto(url));",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nasync function openPlayer(page: Page, display: Display) {\n\tawait page.goto(display.playerUrl);\n}\nawait openPlayer(page, { playerUrl: shown });",
		"await page.goto(issued.playerUrl);",
		"await page.goto(database);",
		"await page.goto(shownUrl);",
		"const where = '/live';\nawait page.goto(where);\nconst shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst show = async (where: string) => {\n\tawait page.goto(where);\n};\nawait show(shown);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nasync function open(page: Page, path = '/live') {\n\tawait page.goto(path);\n}\nawait open(page, shown);",
		"async function open(path: string) {\n\tawait page.goto(path);\n}\nawait open('/live');\n[elsewhere.address].forEach(open);",
		// Every branch, every part that leads, every element.
		"await page.goto('/live' || elsewhere.address);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(ready ? '/a' : flag ? '/b' : shown);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(shown + '/x');",
		"await page.goto(elsewhere.address + '/x');",
		"const issued = await r.json();\nfor (const k of Object.keys(issued)) await page.goto(k);",
		"const issued = await r.json();\nfor (const v of Object.values(issued)) await page.goto(v);",
		"const groups = [['/a'], [elsewhere.address]];\nfor (const group of groups) for (const path of group) await page.goto(path);",
		"for (const [name, url] of Object.entries({ a: '/x', b: elsewhere.address })) await page.goto(url);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nfor (const [name] of Object.entries({ [shown]: '/x' })) await page.goto(name);",
		// Routes and waits for a request or a response keep their argument too; a pattern built at run time is read.
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.route(shown, (r) => r.continue());",
		"await page.waitForResponse(`${hosts.main}/x?access_token=${t}`);",
		"await page.waitForResponse(new RegExp(`access_token=${t}`));",
		// Options: spread, computed or changed after they are written.
		"const opts = { params: { token: t } };\nawait request.get(`${hosts.main}/x`, { ...opts, data: 1 });",
		"await request.get(`${hosts.main}/x`, { params: { ['token']: k } });",
		"const options = { headers: {} };\noptions.params = { token: t };\nawait request.get(`${hosts.main}/x`, options);",
		"const options = { headers: {} };\nObject.assign(options, { params: { token: t } });\nawait request.get(`${hosts.main}/x`, options);",
		// In a file whose addresses carry a secret parameter, an address matched by value; in any file, a secret parameter read by value.
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nawait expect(page).toHaveURL(`${webUrl}/display?code=${code}`);",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(page.url()).toBe(`${webUrl}/display`);",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(new URL(page.url()).hash).toBe('');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nawait expect.poll(() => page.url()).not.toContain('#key=');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst where = new URL(page.url());\nexpect(where.href).toBe('x');",
		"expect(new URL(page.url()).searchParams.get('key')).toBeNull();",
		"await expect(page).not.toHaveURL(/#key/);",
		"expect(page.url()).not.toContain('ardp_');",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nlet where = shown;\nif (ready) where = '/live';\nawait page.goto(where);",
		"function where(ready: boolean) {\n\tif (ready) return '/live';\n\treturn page.getByTestId('credential-value').textContent();\n}\nawait page.goto(await where(true));",
		"await page.goto(`/x?c=${myTokenValue}`);",
		"await page.goto(`${target}/probe`);",
		"page.on('framenavigated', (frame) => page.goto(frame));",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nasync function open(path = '/live') {\n\tawait page.goto(path);\n}\nawait open(shown);",
		"const display = { path: '/x', ...issued };\nawait page.goto(display.path);",
		"for (const host of apiHosts) await page.goto(host.name);",
		"for (const v of Object.values(elsewhere)) await page.goto(v);",
		"for (const url of list.map(toUrl)) await page.goto(url);",
		"await request.get(`${hosts.main}/x`, { [name]: { token: t } });",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(String(shown));",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(decodeURIComponent(shown));",
		"await page.goto('/display?code=' + display.credential);",
		"for (const path of ['/a', ...elsewhere.paths]) await page.goto(path);",
		"await page.goto(`${playerAddressOf}`);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst where = '/live' +\n\tshown;\nawait page.goto(where);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst list = ['/a', shown];\nawait page.goto(list[1]);",
		"function where() {\n\tpage.getByTestId('x');\n}\nawait page.goto(where());",
		"await request.get(`${hosts.main}/x`, `token=${t}`);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst list = ['/a', shown];\nawait page.goto(list[i]);",
		// ARV-224 round 3: += read as a concatenation; destructuring assignments, member and element sets, mutating methods.
		"let path = '/display?code=X';\npath += '#key=' + k;\nawait page.goto(path);",
		"let address = `${hosts.main}/x?code=1`;\naddress += `&access_token=${t}`;\nawait request.get(address);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nlet path = '/';\npath += shown;\nawait page.goto(path);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nlet playerPath = '/live';\n({ playerPath } = { playerPath: shown });\nawait page.goto(playerPath);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nlet p = '/live';\n[p] = [shown];\nawait page.goto(p);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst d = { path: '/live' };\nd.path = shown;\nawait page.goto(d.path);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst d = { path: '/live' };\nObject.assign(d, { path: shown });\nawait page.goto(d.path);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst paths = ['/live'];\npaths.push(shown);\nfor (const p of paths) await page.goto(p);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst paths = ['/live'];\npaths[0] = shown;\nawait page.goto(paths[0]);",
		// Origins come from support modules (or a reviewed export) only; baseURL from a test's callback only.
		"import { state } from '../support/web-auth';\nawait page.goto(state.playerUrl);",
		"import { playerUrl } from './shared-state';\nawait page.goto(playerUrl);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nasync function open({ baseURL }: { baseURL: string }) {\n\tawait page.goto(`${baseURL}/x`);\n}\nawait open({ baseURL: shown });",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nasync function open(baseURL: string) {\n\tawait page.goto(`${baseURL}/x`);\n}\nawait open(shown);",
		"test.describe('a', () => {\n\tfunction where() {\n\t\treturn '/live';\n\t}\n\ttest('x', async () => {\n\t\tawait page.goto(where());\n\t});\n});\ntest.describe('b', () => {\n\tfunction where() {\n\t\treturn page.getByTestId('credential-value').textContent();\n\t}\n});",
		// A secret value in the query or fragment, by value as in the path.
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(`/display?code=X#k=${shown}`);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait page.goto(`${webUrl}/display?code=X&k=${shown}`);",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nawait request.get(`${hosts.main}/x?c=${encodeURIComponent(shown)}`);",
		"await request.get(`${hosts.main}/x?c=${r.refreshCookie()}`);",
		// An address cut or wrapped is still an address; an absolute one cut is no longer absolute.
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(page.url().split('#')[1]).toBeUndefined();",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(page.url().slice(-60)).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(page.url().replace(webUrl, '')).toBe('/display?code=X');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(decodeURIComponent(page.url())).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(`${page.url()}`).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(new URL(page.url()).hash.slice(1)).toBe('');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(await page.evaluate(() => location.hash)).toBe('');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect({ url: page.url() }).toEqual({ url: 'x' });",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst req = await page.waitForRequest('**/board**');\nexpect(req.url()).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst where = page.url();\nexpect(where.split('#')[0]).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${c}#key=${k}`.slice(webUrl.length));",
		"await page.goto(encodeURIComponent(`${webUrl}/display#key=${k}`));",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\n[{ baseURL: shown }].forEach(async ({ baseURL }) => {\n\tawait page.goto(`${baseURL}/x`);\n});",
		"import { shared } from '../support/state';\nawait page.goto(shared.path);",
		"await request.get(`${hosts.main}/x?session=${s}`);",
		// ARV-224 round 4: a name bound to an address read, wrapped; a helper's return; an operand of && or ||; a compound member set; a reviewed export's other members.
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst where = page.url();\nexpect(`${where}`).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst where = page.url();\nexpect(decodeURIComponent(where)).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst where = page.url();\nexpect({ where }).toEqual({ where: 'x' });",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nfunction hashOf(p: Page) {\n\treturn new URL(p.url()).hash;\n}\nexpect(hashOf(page)).toBe('');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nfunction hashOf(p: Page) {\n\treturn new URL(p.url()).hash;\n}\nconst h = hashOf(page);\nexpect(h).toBe('');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(ready && page.url()).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(page.url() || 'x').toBe('x');",
		"const shown = ((await page.getByTestId('credential-value').textContent()) ?? '').trim();\nconst d = { path: '/live' };\nd.path += shown;\nawait page.goto(d.path);",
		"await page.goto(`${target.other}/x`);",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst where = page.url();\nexpect([...where]).toEqual([]);",
		"import { lastPlayerUrl } from '../support/state';\nawait page.goto(lastPlayerUrl);",
		// ARV-224 round 5: a chain of aliases of any length, a helper's parameter, a callback's and a loop's list, a support helper's return.
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst a0 = page.url();\nconst a1 = a0;\nconst a2 = a1;\nconst a3 = a2;\nconst a4 = a3;\nconst a5 = a4;\nexpect(a5).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nfunction expectAt(address: string) {\n\texpect(address).toBe('x');\n}\nexpectAt(page.url());",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst expectAt = (address: string) => {\n\texpect(address).toBe('x');\n};\nexpectAt(page.url());",
		"function expectNoKey(address: string) {\n\texpect(address).not.toContain('#key=');\n}\nexpectNoKey(page.url());",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\n[page.url()].forEach((u) => expect(u).toBe('x'));",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nfor (const u of [page.url()]) expect(u).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(currentAddress(page)).toBe('x');",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst address = '/x';\nfunction expectAt(address: string) {\n\texpect(address).toBe('x');\n}\nexpectAt(page.url());",
		// Past the depth the reader follows (40), a chain fails closed.
		`await page.goto(\`\${webUrl}/display#key=\${k}\`);\nconst a0 = page.url();\n${Array.from({ length: 44 }, (_, i) => `const a${i + 1} = a${i};`).join('\n')}\nexpect(a44).toBe('x');`
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
		"for (const host of apiHosts) await request.get(`${host.url}/health/readiness`, { headers: { Authorization: `Bearer ${canary.jwt}` } });",
		"await call('GET', `${host.url}/hubs/live?access_token=${canary.jwt}`);",
		"await request.get(`${apiUrl}/display/board?code=${display.code}&siteCode=${site}&zoneKey=${zoneKey}`);",
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
		"await expect(page.getByTestId('recovery-codes')).toHaveCount(0);",
		// ARV-224: addresses read down to literals and origins.
		"for (const path of ['/users', '/reports', '/validation']) await page.goto(path);",
		"const paths = ['/users', '/reports'] as const;\nfor (const path of paths) await page.goto(path);",
		"for (const host of [hosts.ingest, hosts.integration]) await request.get(`${host}/health`);",
		"const documented = { main: hosts.main, ingest: hosts.ingest } as const;\nfor (const [name, url] of Object.entries(documented)) await request.get(`${url}/openapi/v1.json`);",
		"async function open(page: Page, path: string) {\n\tawait page.goto(path);\n}\nawait open(page, '/live');\nawait open(page, `${webUrl}/display?code=${c}#key=${k}`);",
		"const show = async (where: string) => {\n\tawait page.goto(where);\n};\nawait show('/live');",
		"await page.goto(`${target.base}/probe`);",
		"const where = '/live';\nconst again = where;\nawait page.goto(again);",
		"await page.goto(ready ? '/live' : `${webUrl}/history`);",
		"let displayKey = '';\nfunction playerUrl(): string {\n\treturn `${webUrl}/display?code=${code}#key=${encodeURIComponent(displayKey)}`;\n}\nawait page.goto(playerUrl());",
		"await request.get(`${hosts.main}/api/v1/x`, { params: { code: 'DMO', limit: 10 } });",
		"const query = { code: 'DMO' };\nawait request.get(`${hosts.main}/api/v1/x`, { params: query });",
		"await request.get(`${hosts.main}/api/v1/x`, { headers: { Authorization: `Bearer ${token}` } });",
		"const options = { headers: { Authorization: `Bearer ${token}` }, params: { code: 'DMO' } };\nawait request.get(`${hosts.main}/api/v1/x`, options);",
		"await page.waitForURL(/^chrome-error:/);",
		"await page.waitForURL('**/login');",
		"await page.waitForURL((url) => url.pathname === '/live');",
		"await expect(page).toHaveURL(`${webUrl}/login?next=%2Faccount`);",
		"await expect(page).toHaveURL(/view=counts/);",
		"await expect.poll(() => new URL(page.url()).hash.includes('key=')).toBe(false);",
		"expect(new URL(page.url()).hash.includes('key='), 'the key leaves the address').toBe(false);",
		// ARV-224 round 2: addresses read down to literals and imported origins through bindings, calls, loops, callbacks and support files.
		"for (const row of rowsOf(list)) await request.fetch(row.url);",
		"for (const [name, url] of Object.entries({ a: '/x', b: `${webUrl}/y` })) await page.goto(url);",
		"const campaignsPage = (query: string) => `/validation?site=${site}&${query}`;\nawait page.goto(campaignsPage(`campaign=${id}&view=results`));",
		"async function createDisplay(): Promise<{ id: string; path: string }> {\n\treturn { id: 'x', path: `${webUrl}/display?code=${code}#key=${encodeURIComponent(body.credential)}` };\n}\nlet display: { id: string; path: string } | null = null;\ndisplay = await createDisplay();\nawait page.goto(display!.path);",
		"let playerPath = '';\nplayerPath ||= `${webUrl}/display?code=${c}#key=${k}`;\nawait page.goto(playerPath);",
		"await page.route(new RegExp(`/api/v1/admin/devices/${device.id}/coverage$`), (r) => r.continue());",
		"await page.waitForResponse((r) => r.url().endsWith('/x'));",
		"test('x', async ({ page, baseURL }) => {\n\tawait page.goto(`${baseURL}/live`);\n});",
		"['/a', '/b'].forEach((path) => page.goto(path));",
		"const groups = [['/a'], ['/b']];\nfor (const group of groups) for (const path of group) await page.goto(path);",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nawait expect.poll(() => new URL(page.url()).hash.includes('key=')).toBe(false);\nexpect(new URL(page.url()).pathname).toBe('/display');\nexpect(new URL(page.url()).searchParams.get('code')).toBe(code);",
		"expect(new URL(page.url()).searchParams.get('code')).toBe('DMO');",
		"await expect(page).toHaveURL(`${webUrl}/live`);",
		// ARV-224 round 3.
		"await page.goto('/a' ?? '/b');\nawait page.goto('/a' || '/b');\nawait page.goto('/a' && '/b');",
		"let path = '/a';\npath += '/b';\nawait page.goto(path);",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect(page.url().includes('#key=')).toBe(false);\nexpect(new URL(page.url()).pathname).toBe('/display');\nconst response = await page.waitForResponse((r) => new URL(r.url()).pathname === '/x');\nexpect(response.status()).toBe(200);\nexpect(/display/.test(page.url())).toBe(true);",
		"const code = credential(credentialVariables.displayCode);\nawait request.get(`${hosts.main}/api/v1/display/board?code=${encodeURIComponent(code)}`);",
		"test('x', async ({ page, baseURL }) => {\n\tawait page.goto(`${baseURL}/live?code=${code}`);\n});",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nexpect((await page.waitForResponse((r) => new URL(r.url()).pathname === '/x')).status()).toBe(200);",
		"import { fixed } from '../support/state';\nawait page.goto(fixed.path);",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nconst url = new URL(page.url());\nexpect([...url.searchParams.keys()]).toEqual(['code']);\nexpect(url.pathname).toBe('/display');\nexpect(url.searchParams.get('code')).toBe(code);",
		"await page.goto(`${webUrl}/display?code=${code}#key=${k}`);\nfunction lengthOf(address: string) {\n\treturn address.length;\n}\nexpect(lengthOf(page.url())).toBeGreaterThan(0);\nconst cut = 3;\nconst left = cut + 1;\nconst right = left * 2;\nexpect(right).toBe(8);"
	]
};

// ARV-224: support files the samples import from (tests/support stands in): a helper that builds rows with their addresses from
// an origin, and one that returns a value read from the page.
const SELF_SUPPORT = [
	{ name: 'rows', source: "import { hosts } from './hosts';\nexport function rowsOf(list: Row[]) {\n\treturn list.filter((row) => row.on).map((row) => ({ ...row, url: `${hosts[row.host]}${row.route}` }));\n}\nexport async function playerAddressOf(page: Page): Promise<string> {\n\treturn ((await page.getByTestId('player-address').textContent()) ?? '').trim();\n}\nexport function currentAddress(page: Page): string {\n\treturn page.url();\n}\n" },
	{ name: 'smoke', source: "export const target = { base: process.env.ARIVA_SMOKE_URL ?? '', local: true };\n" },
	{ name: 'state', source: "export const shared = { path: '/live' };\nexport function remember(page: Page) {\n\tshared.path = page.url();\n}\nexport const fixed = { path: '/live' };\nexport let lastPlayerUrl = '/live';\nexport function note(page: Page) {\n\tlastPlayerUrl = page.url();\n}\n" }
];

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
	// ARV-224: every sample imports the origins the suite's support files export, as a spec does; SELF_SUPPORT stands in for them.
	const prelude = "import { hosts, webUrl, apiUrl, apiHosts } from '../support/hosts';\nimport { target } from '../support/smoke';\nimport { rowsOf, playerAddressOf, currentAddress } from '../support/rows';\n";
	const support = SELF_SUPPORT.map((file) => ({ ...file, masked: mask(file.source) }));
	SELF_TEST.bad.forEach((source, i) => {
		if (findings(prelude + source, support).length === 0) problems.push(`bad ${i + 1} not found: ${source.replace(/\n/g, ' / ')}`);
	});
	SELF_TEST.good.forEach((source, i) => {
		const found = findings(prelude + source, support);
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
