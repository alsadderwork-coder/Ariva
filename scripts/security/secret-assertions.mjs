#!/usr/bin/env node
// ARV-249 (CWE-532, ASVS V13.3.1): no C# test asserts a secret's value. A failing FluentAssertions assertion prints the value
// it was given and the value it expected (`Expected signIn.RefreshToken to be "..." with a length of 43, but ...`), and the
// failure lands in the test log, the TRX file and CI's output. So an assertion on a token, a credential, a secret, a TOTP
// seed or code, a recovery code or a password asserts a boolean, a count or a shape, never the value:
//   string.Equals(second.RefreshToken, first.RefreshToken, StringComparison.Ordinal).Should().BeTrue(...)
//                                                       not   second.RefreshToken.Should().Be(first.RefreshToken)
//   secret.Length.Should().Be(43)                                       not   secret.Should().HaveLength(43)
//   stored.Contains(secret).Should().BeFalse(...)                       not   stored.Should().NotContain(secret)
// (string.Equals where the name is one the security scan's SEC-041 reads, Secret, Password, ApiKey or TotpCode: it refuses
// == and != on those, tests included; == is fine on a token or a credential.)
// The TypeScript side (Playwright's matchers) is checked by Platform/Testing/Ariva.E2E/scripts/expect-messages.mjs.
//
//   node scripts/security/secret-assertions.mjs               check every .cs file of the test projects; exit 1 on a finding
//   node scripts/security/secret-assertions.mjs --self-test   prove the check fires on every bad pattern and stays quiet on good ones
//   node scripts/security/secret-assertions.mjs <files...>    check only these files
//
// What it reads: each `.Should()` with its subject (the expression before it) and the assertions chained after it (`.Be(x)`,
// `.And.StartWith(y)`, until `.Which`, `.Subject` or the statement's end). A finding is a secret subject with an assertion
// that prints it (anything but NotBeNull, NotBeNullOrEmpty, NotBeNullOrWhiteSpace, NotBeEmpty, BeTrue and BeFalse), or a
// secret in an assertion's argument (an expected value, a because argument). Whether a value is a secret is read from its
// name: a member, variable or constant whose name ends in Password, Secret, Credential, a compound Token (AccessToken,
// RefreshToken, PreviewToken), Jwt, ApiKey, PrivateKey, SigningKey, PseudonymKey, HmacKey, RecoveryCode, TotpCode, TotpSeed
// or TotpUri (a number after it included: Password1), or a variable named token (a member named Token is a
// CancellationToken more often than not, so it is left to review); an index, a range or a substring of one; a
// concatenation, an interpolation, a `??` or a `?:` that carries one; and a call named for issuing one (CreateToken,
// NewPassword), a TOTP code (`Code(...)`, `Totp.Code(...)`, `CodeNow(...)`) or a protected value read back (`Unprotect`,
// `Decrypt`); and, read from the subject's source, a secret JSON member (`GetProperty("clientSecret")`), a secret header
// (`GetValues("X-TOTP-Code")`, `X-Api-Key`) or the Authorization header. A comparison, a call that derives something else
// (`IsWellFormed(x)`, `Hash(x)`, `.Contains(x)`), `.Length` and `.Count` are not. A string literal or an initializer
// (`new[] { ... }`) is read as a subject like any other; a subject the check cannot read is a finding (fail closed), and so is
// a walk that finds fewer than 100 files in the unit and integration test projects. Names and reads are the heuristic, with
// no data flow: a secret held under another name (a seed called Seed, a planted canary) or a container asserted by shape is
// asserted the same way by review. The check reads code only: comments, preprocessor lines and the text of string literals
// (regular, verbatim, interpolated, raw) are masked first (an interpolation's holes stay code).

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const SKIP_DIRS = new Set(['bin', 'obj', 'node_modules', '.git', 'TestResults']);

/**
 * The source with comments, the text of string and character literals replaced by spaces (line breaks kept), so brackets
 * and names are code only and every offset still points at the original. An interpolated string's holes stay code, their
 * braces included; regular, verbatim, interpolated and raw literals (with any number of `$`) are read.
 */
export function mask(source) {
	const out = source.split('');
	const n = source.length;
	const blank = (from, to) => {
		for (let k = from; k < to && k < n; k++) if (out[k] !== '\n' && out[k] !== '\r') out[k] = ' ';
	};
	// Whether the `#` at `at` starts a preprocessor line (only white space before it on its line).
	const directive = (at) => {
		for (let k = at - 1; k >= 0 && source[k] !== '\n'; k--) if (source[k] !== ' ' && source[k] !== '\t' && source[k] !== '\r') return false;
		return true;
	};
	// Scans code from `start`; with `inHole`, stops after the `}` that closes an interpolation hole.
	const code = (start, inHole) => {
		let i = start;
		let depth = 0;
		while (i < n) {
			const c = source[i];
			const d = source[i + 1];
			if ((c === '/' && d === '/') || (c === '#' && directive(i))) {
				// A comment, or a preprocessor line (`#region the observers' ...`), whose text is not code.
				const e = source.indexOf('\n', i);
				const end = e < 0 ? n : e;
				blank(i, end);
				i = end;
			} else if (c === '/' && d === '*') {
				const e = source.indexOf('*/', i + 2);
				const end = e < 0 ? n : e + 2;
				blank(i, end);
				i = end;
			} else if (c === "'") {
				let j = i + 1;
				while (j < n && source[j] !== "'" && source[j] !== '\n') j += source[j] === '\\' ? 2 : 1;
				blank(i + 1, j);
				i = j + 1;
			} else if (c === '"' || ((c === '$' || c === '@') && /^[$@]+"/.test(source.slice(i, i + 12)) && !/[\w]/.test(source[i - 1] ?? ''))) {
				i = literal(i);
			} else if (c === '{') {
				depth++;
				i++;
			} else if (c === '}') {
				if (inHole && depth === 0) return i + 1;
				depth--;
				i++;
			} else i++;
		}
		return n;
	};
	// Scans a string literal from its first prefix character or quote; returns the offset after its closing quote(s).
	const literal = (start) => {
		let i = start;
		let dollars = 0;
		let verbatim = false;
		while (source[i] === '$' || source[i] === '@') {
			if (source[i] === '$') dollars++;
			else verbatim = true;
			i++;
		}
		let quotes = 0;
		while (source[i + quotes] === '"') quotes++;
		if (quotes >= 3 && !verbatim) {
			// Raw literal (a verbatim literal is never raw: @ and four quotes is one quote between quotes): ends at a run of as many quotes; a hole opens with `dollars` braces in a row.
			const fence = '"'.repeat(quotes);
			i += quotes;
			while (i < n) {
				if (source.startsWith(fence, i)) return i + quotes;
				if (dollars > 0 && source.startsWith('{'.repeat(dollars), i) && source[i + dollars] !== '{') {
					// One brace stays as the hole's opener (the closer's extra braces are text), so the brackets balance.
					blank(i, i + dollars - 1);
					i = code(i + dollars, true);
					continue;
				}
				blank(i, i + 1);
				i++;
			}
			return n;
		}
		if (quotes === 2) return i + 2; // ""
		i++;
		while (i < n) {
			const c = source[i];
			if (verbatim && c === '"' && source[i + 1] === '"') {
				blank(i, i + 2);
				i += 2;
			} else if (c === '"') return i + 1;
			else if (!verbatim && c === '\\') {
				blank(i, i + 2);
				i += 2;
			} else if (!verbatim && c === '\n') return i;
			else if (dollars > 0 && c === '{' && source[i + 1] === '{') {
				blank(i, i + 2);
				i += 2;
			} else if (dollars > 0 && c === '{') i = code(i + 1, true);
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

/** The offset of the bracket that closes the one at `open`, or -1. */
function closing(text, open) {
	let depth = 0;
	for (let i = open; i < text.length; i++) {
		if ('([{'.includes(text[i])) depth++;
		else if (')]}'.includes(text[i]) && --depth === 0) return i;
	}
	return -1;
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

/** The `<` that opens the type argument list closing at `close` (a `>` right before a call's `(`), or -1. */
function typeArgumentsOpening(text, close) {
	let depth = 0;
	for (let i = close; i >= 0; i--) {
		if (text[i] === '>') depth++;
		else if (text[i] === '<' && --depth === 0) return i;
		else if (';{}='.includes(text[i])) return -1;
	}
	return -1;
}

/** The offset where the string literal whose last quote is at `close` (masked text) begins, its `$` and `@` included, or -1. */
function literalOpening(masked, close) {
	let quotes = 0;
	while (close - quotes >= 0 && masked[close - quotes] === '"') quotes++;
	let j;
	if (quotes === 2) j = close - 1; // an empty literal
	else {
		// Back over the text (masked) and the holes (code, with literals of their own) to a quote outside every hole.
		let depth = 0;
		for (j = close - quotes; j >= 0; j--) {
			if (masked[j] === '}') depth++;
			else if (masked[j] === '{') depth--;
			else if (masked[j] === '"' && depth <= 0) break;
		}
		if (j < 0) return -1;
		while (j > 0 && masked[j - 1] === '"') j--;
	}
	while (j > 0 && (masked[j - 1] === '$' || masked[j - 1] === '@')) j--;
	return j;
}

/** The start of the expression whose member access begins at `dot` (`a.b(c)[d]!.Should()`: the offset of `a`), or -1. */
export function subjectStart(masked, dot) {
	let i = dot - 1;
	const skip = () => {
		while (i >= 0 && /\s/.test(masked[i])) i--;
	};
	skip();
	let start = -1;
	while (i >= 0) {
		while (i >= 0 && masked[i] === '!') {
			i--;
			skip();
		}
		// A string literal (its text is masked, its holes are code): back to its opening quotes and their `$` and `@`.
		if (masked[i] === '"') return literalOpening(masked, i);
		if (masked[i] === '}') {
			// An initializer: `new[] { ... }`, `new List<T> { ... }`, `new T(...) { ... }`.
			const open = opening(masked, i);
			if (open < 0) return -1;
			start = open;
			i = open - 1;
			skip();
			if (masked[i] === '>') {
				const lt = typeArgumentsOpening(masked, i);
				if (lt > 0) i = lt - 1;
			}
			if (i >= 0 && /[\w)\]]/.test(masked[i])) continue;
			return start;
		}
		if (masked[i] === ')' || masked[i] === ']') {
			const open = opening(masked, i);
			if (open < 0) return -1;
			start = open;
			i = open - 1;
			if (masked[i] === '>') {
				const lt = typeArgumentsOpening(masked, i);
				if (lt > 0 && /\w/.test(masked[lt - 1])) i = lt - 1;
			}
			if (i >= 0 && /\w/.test(masked[i])) continue; // a call's or an indexer's receiver
			if (i >= 0 && (masked[i] === ')' || masked[i] === ']' || masked[i] === '!')) continue;
			const before = i;
			skip();
			if (masked[i] === '.' || (masked[i] === '?' && masked[i + 1] === '.')) {
				i = masked[i] === '?' ? i - 1 : i - 1;
				if (masked[i] === '?') i--;
				skip();
				continue;
			}
			i = before;
			return start;
		}
		if (/\w/.test(masked[i] ?? '')) {
			while (i >= 0 && /\w/.test(masked[i])) i--;
			start = i + 1;
			skip();
			if (masked[i] === '.') {
				i--;
				if (masked[i] === '?') i--;
				skip();
				continue;
			}
			// The creation a type name belongs to (`new List<T> { ... }`).
			if (masked.slice(i - 2, i + 1) === 'new' && !/\w/.test(masked[i - 3] ?? '') && masked.slice(start, start + 3) !== 'new') return i - 2;
			return start;
		}
		return start;
	}
	return start;
}

/** Splits text at top-level occurrences of the separator pattern (outside brackets); the pattern must be sticky (/y). */
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

const SECRET_SUFFIX = /(?:^|[a-z0-9_])(?:Password|Passwords|Passphrase|Secret|Secrets|Credential|Credentials|Jwt|ApiKey|PrivateKey|SigningKey|PseudonymKey|HmacKey|MasterKey|RecoveryCode|RecoveryCodes|TotpCode|TotpCodes|OtpCode|TotpSeed|OtpSeed|TotpUri|OtpAuthUri)\d*$/;
const SECRET_WHOLE = /^(?:password|passwords|passphrase|secret|secrets|credential|credentials|jwt|apiKey|recoveryCode|recoveryCodes|totpCode|totpSeed|totpUri|token)\d*$/;
/** A compound token (AccessToken, refreshToken, PreviewToken); a bare Token, a CancellationToken or a stopping token is not. */
const TOKEN_SUFFIX = /^(?!(?:[Cc]ancellation|[Ss]topping|[Cc]ancel|[Ss]hutdown|[Ss]top|[Cc]t)Tokens?$)[A-Za-z0-9_]*[a-z0-9]Tokens?$/;
/** Names of messages, flags and checks about a secret, not a secret: InvalidCredentials, MustChangePassword, RequireTotpPerRequest. */
const NOT_A_SECRET = /^(?:Invalid|Bad|Wrong(?=[A-Z]\w*(?:Message|Error))|Unknown|Missing|Must|Require|Requires|Has|Is|Can|Check|Validate|Verify|Set|With|Reset|Change|Rotate|Revoke|Protect|Unprotect)[A-Z]/;
/** Calls whose result is a secret by their name: one that issues it, a TOTP code, a protected value read back. */
const ISSUES_SECRET = /^(?:(?:Create|Issue|New|Generate|Mint|Sign)\w*(?:Token|Password|Secret|Credential|Jwt)s?|Code|CodeNow|TotpCode|OtpCode|CurrentCode|Unprotect|Decrypt)$/;
/** Calls whose result is their receiver's text, or a part or an element of it. */
const TRANSPARENT = new Set(['Substring', 'Trim', 'TrimStart', 'TrimEnd', 'ToLower', 'ToLowerInvariant', 'ToUpper', 'ToUpperInvariant', 'ToString', 'Replace', 'Remove', 'Insert', 'PadLeft', 'PadRight', 'AsSpan', 'AsMemory', 'Split', 'First', 'FirstOrDefault', 'Last', 'LastOrDefault', 'Single', 'SingleOrDefault', 'ElementAt', 'ElementAtOrDefault', 'ToArray', 'ToList', 'ToHashSet', 'Take', 'TakeLast', 'Skip', 'SkipLast', 'Order', 'OrderBy', 'OrderDescending', 'OrderByDescending', 'Distinct', 'Reverse', 'Where', 'GetString', 'GetValueOrDefault']);

/** Whether a C# identifier names a secret. */
export function secretName(name) {
	if (NOT_A_SECRET.test(name)) return false;
	return SECRET_WHOLE.test(name) || SECRET_SUFFIX.test(name) || TOKEN_SUFFIX.test(name);
}

/** Whether the value of an expression (masked text) is a secret: what a failing assertion would print. */
export function secretValue(expression, depth = 0) {
	if (depth > 20) return false;
	let e = expression.trim();
	for (;;) {
		const before = e;
		e = e.replace(/^(?:await|checked|unchecked)\s+/, '').replace(/!+$/, '').replace(/\s+as\s+[\w.<>,\s?[\]]+$/, '').trim();
		if (e.startsWith('(') && closing(e, 0) === e.length - 1) e = e.slice(1, -1).trim();
		else if (/^\(\s*[\w.<>,\s?[\]]+\)\s*[\w(@$"]/.test(e) && closing(e, 0) > 0) e = e.slice(closing(e, 0) + 1).trim(); // a cast
		if (e === before) break;
	}
	if (!e) return false;
	// A boolean: a negation, a comparison, a logical operator, a type test. A lambda: a delegate.
	if (/^!(?!=)/.test(e) || /^(?:nameof|typeof|sizeof|default)\b/.test(e)) return false;
	if (splitTop(e, /==|!=|<=|>=|&&|\|\||=>|\s+is\s+|\s<\s|\s>\s/y).length > 1) return false;
	// A value that may be any of its operands: `a ?? b`, `c ? a : b`, `a + b`.
	for (const separator of [/\?\?/y, /\?(?![.[])|:/y, /\+/y]) {
		const parts = splitTop(e, separator);
		if (parts.length > 1) return parts.some((part) => secretValue(part, depth + 1));
	}
	if (/^new\b/.test(e) && creationEnd(e) >= e.length - 1) {
		// An object or a collection the assertion would print with its members: a secret among its arguments or initialisers.
		// (A creation followed by a member access, `new JsonWebToken(t).Claims`, is read as a chain below.)
		for (let at = e.search(/[({[]/); at >= 0 && at < e.length; ) {
			const group = e.slice(at);
			if (items(group).some((item) => secretValue(item.replace(/^\s*\w+\s*=(?!=)/, ''), depth + 1))) return true;
			const close = closing(group, 0);
			if (close < 0) break;
			const next = e.slice(at + close + 1).search(/[({[]/);
			at = next < 0 ? -1 : at + close + 1 + next;
		}
		return false;
	}
	if (/^[$@]*"/.test(e)) {
		// A string literal: its interpolation holes are the values it prints.
		return [...e.matchAll(/\{/g)].some((m) => {
			const close = closing(e, m.index);
			return close > 0 && secretValue(e.slice(m.index + 1, close).replace(/[,:][^,:]*$/, ''), depth + 1);
		});
	}
	if (e.startsWith('[')) return items(e).some((item) => secretValue(item.replace(/^\.\./, ''), depth + 1)); // [a, ..b]
	const last = e[e.length - 1];
	if (last === ']') {
		const open = opening(e, e.length - 1);
		return open > 0 && secretValue(e.slice(0, open), depth + 1); // an element, an index or a range of it
	}
	if (last === ')') {
		const open = opening(e, e.length - 1);
		if (open <= 0) return false;
		let callee = e.slice(0, open).trim();
		if (callee.endsWith('>')) {
			const lt = typeArgumentsOpening(callee, callee.length - 1);
			if (lt > 0) callee = callee.slice(0, lt).trim();
		}
		const m = /(?:^|(.*?)\s*\??\.\s*)(\w+)$/s.exec(callee);
		if (!m) return false;
		const [, receiver, name] = m;
		const args = items(e.slice(open));
		if (ISSUES_SECRET.test(name)) return true;
		if (['Concat', 'Join', 'Format', 'Append', 'Prepend'].includes(name))
			return args.some((arg) => secretValue(arg, depth + 1)) || (receiver !== undefined && secretValue(receiver, depth + 1));
		if (TRANSPARENT.has(name)) {
			if (receiver && receiver.trim()) return secretValue(receiver, depth + 1);
			return args.some((arg) => secretValue(arg, depth + 1)); // string.Concat(a, b), Enumerable.First(x)
		}
		return false;
	}
	const name = /(\w+)$/.exec(e)?.[1];
	return name !== undefined && !/^\d/.test(name) && secretName(name);
}

/** The offset of the last character of the creation that starts `e` (`new T<U>(...) { ... }`, `new[] { ... }`), or -1. */
function creationEnd(e) {
	const m = /^new\s*(?:[\w.]+\s*(?:<[^<>]*(?:<[^<>]*>[^<>]*)*>)?\s*\??)?/.exec(e);
	let i = m ? m[0].length : 0;
	let end = -1;
	for (;;) {
		while (i < e.length && /\s/.test(e[i])) i++;
		if (i >= e.length || !'([{'.includes(e[i])) return end;
		const close = closing(e, i);
		if (close < 0) return -1;
		end = close;
		i = close + 1;
	}
}

/** The top-level items of a bracketed list starting at offset 0 (arguments, initialisers, elements). */
function items(text) {
	const close = closing(text, 0);
	if (close < 0) return [];
	return splitTop(text.slice(1, close), /,/y).map((item) => item.trim()).filter(Boolean);
}

/** Assertions that print nothing of a secret subject: they fail only on null or empty, or the subject is a boolean. */
const PRESENCE = new Set(['NotBeNull', 'NotBeNullOrEmpty', 'NotBeNullOrWhiteSpace', 'NotBeEmpty', 'BeTrue', 'BeFalse',
	// A delegate's assertions: they print the exception, never the delegate.
	'Throw', 'ThrowAsync', 'ThrowExactly', 'ThrowExactlyAsync', 'NotThrow', 'NotThrowAsync', 'NotThrowAfter', 'NotThrowAfterAsync', 'CompleteWithin', 'CompleteWithinAsync', 'NotCompleteWithinAsync', 'WithMessage', 'WithInnerException', 'WithInnerExceptionExactly', 'WithParameterName']);

/**
 * Reads of a secret its name does not carry, matched on the source (the masked text has no string contents): a JSON member
 * named for one, a header that carries one, the Authorization header's value.
 */
const SECRET_READ = /(?:GetProperty\(\s*"(?:clientSecret|totpSecret|totpSeed|totpCode|password|secret|accessToken|refreshToken|credential|apiKey|temporaryPassword)"\s*\)(?:\s*\.\s*GetString\(\s*\))?|GetValues\(\s*"(?:X-TOTP-Code|Authorization|X-Api-Key|X-Ariva-Device-Key|X-Ariva-Display-Key)"\s*\)(?:\s*\.\s*(?:Single|First)\(\s*\))?|Headers\s*\.\s*Authorization\s*!?(?:\s*\.\s*(?:Parameter|ToString\(\s*\))\s*!?)?)\s*!?\s*$/;

/** Findings in one file's source: { line, reason, text } for every assertion that would print a secret when it fails. */
export function findings(source) {
	const masked = mask(source);
	const lineOf = (offset) => source.slice(0, offset).split('\n').length;
	const result = [];
	for (const m of masked.matchAll(/\.\s*Should\s*\(\s*\)/g)) {
		const start = subjectStart(masked, m.index);
		const report = (reason) => {
			const from = start < 0 ? m.index : start;
			const end = masked.indexOf(';', m.index);
			result.push({ line: lineOf(from), reason, text: source.slice(from, end < 0 ? m.index + m[0].length : end).replace(/\s+/g, ' ').trim().slice(0, 140) });
		};
		const subject = start < 0 ? '' : masked.slice(start, m.index);
		if (!subject.trim()) {
			// Fail closed: an assertion whose subject the check cannot read could print anything.
			report('asserts a subject the check cannot read: assign it to a variable first');
			continue;
		}
		const subjectIsSecret = secretValue(subject) || SECRET_READ.test(source.slice(start, m.index));
		// The chain: `.Name(args)` or `.Name<T>(args)` calls, `.And.`, until `.Which`, `.Subject`, `.Where`, or anything else.
		let i = m.index + m[0].length;
		let reported = false;
		while (!reported) {
			const call = /^\s*\.\s*(?:And\s*\.\s*)?(\w+)\s*(?:<[^<>()]*(?:<[^<>()]*>[^<>()]*)*>)?\s*\(/.exec(masked.slice(i, i + 400));
			if (!call || ['Which', 'Subject', 'Where', 'Satisfy'].includes(call[1])) break;
			const open = i + call[0].length - 1;
			const close = closing(masked, open);
			if (close < 0) break;
			const name = call[1];
			if (subjectIsSecret && !PRESENCE.has(name)) {
				report(`asserts ${subject.replace(/\s+/g, ' ').trim().slice(0, 60)} with ${name}, which prints the secret when it fails: assert a boolean, a count or a shape`);
				reported = true;
				break;
			}
			for (const arg of items(masked.slice(open, close + 1))) {
				if (/^\s*(?:\([^()]*\)|\w+)\s*=>/.test(arg)) continue; // a predicate: printed as its source text, not its values
				if (secretValue(arg)) {
					report(`passes ${arg.replace(/\s+/g, ' ').trim().slice(0, 60)} to ${name}, which prints it when the assertion fails: compare as a boolean`);
					reported = true;
					break;
				}
			}
			i = close + 1;
		}
	}
	return result;
}

/** Every .cs file of the test projects (folders named Ariva.*Tests under Platform), outside bin and obj. */
function testFiles() {
	const files = [];
	const walk = (dir, inTests) => {
		for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
			const file = path.join(dir, entry.name);
			if (entry.isDirectory()) {
				if (!SKIP_DIRS.has(entry.name) && entry.name !== '.private') walk(file, inTests || /^Ariva\.\w*Tests$/.test(entry.name));
			} else if (inTests && entry.isFile() && file.endsWith('.cs')) files.push(file);
		}
	};
	walk(path.join(ROOT, 'Platform'), false);
	return files;
}

export function check(files) {
	const all = [];
	for (const file of files) {
		for (const f of findings(fs.readFileSync(file, 'utf8'))) all.push({ file: path.relative(ROOT, file), ...f });
	}
	return all;
}

// Bad patterns each produce a finding; good ones (booleans, counts, shapes, presence, derived values) none.
const SELF_TEST = {
	bad: [
		'signIn.RefreshToken.Should().HaveLength(43, "256 random bits");',
		'second.Data.RefreshToken.Should().Be(first.Data.RefreshToken);',
		'refreshed.Data.RefreshToken.Should().NotBe(signIn.RefreshToken);',
		'view.ClientSecret.Should().StartWith("ics_");',
		'view.TotpUri.Should().StartWith("otpauth://totp/").And.Contain(view.Client.ClientId);',
		'stored.Should().NotContain(view.TotpSecret).And.NotContain(view.ClientSecret);',
		'uri.Should().Contain("secret=" + view.TotpSecret);',
		'uri.Should().Contain($"secret={view.TotpSecret}");',
		'credential.Password.Should().HaveLength(32);',
		'credential.RecoveryCodes.Should().HaveCount(10);',
		'confirmed.Data.RecoveryCodes.Should().HaveCount(10).And.OnlyHaveUniqueItems();',
		'first.Credential.Should().StartWith("ardp_").And.HaveLength(48).And.NotBe(second.Credential);',
		'first.Prefix.Should().Be(first.Credential[..13]);',
		'device.CredentialHash.Should().NotContain(issued.Credential[13..]);',
		'credential.Should().NotBeNullOrEmpty().And.StartWith("ardk_");',
		'(await _host.ReadAsync<string>("SELECT 1", id)).Should().NotBe(Password);',
		'_authenticator.Contexts.Should().ContainSingle().Which.PresentedRefreshToken.Should().Be(old);',
		'passwords.Should().OnlyHaveUniqueItems();',
		'parsed.Password.Should().Be("p;Include Error Detail=true;x=");',
		'reader.Password.Should().Be(ReaderPassword);',
		'preview.PreviewToken.Should().BeNull();',
		'x.Should().Be(1, "because {0}", client.ClientSecret);',
		'x.Should().Be(1, $"the secret {view.TotpSecret} is refused");',
		'body.Should().NotContain(credential.Substring(13));',
		'body.Should().NotContain(credential.Trim());',
		'code.Should().Be(Code(c.TotpSecret));',
		'issued.Should().Be(CreateToken(user));',
		'result.Should().Be(token.AccessToken ?? "");',
		'result.Should().Be(rotate ? newSecret : oldSecret);',
		'outbound.Unprotect(newSecret).Should().Be(Secret);',
		'secret.Should().HaveCount(20, "160-bit secret");',
		'apiKey.Should().Be(OutboundApiKey);',
		'list.Should().Equal(new[] { first.Credential, second.Credential });',
		'list.Should().BeEquivalentTo([issued.Credential]);',
		'string.Concat(a.Secret, b).Should().Be(x);',
		'view.ClientSecret!.Should().StartWith("ics_");',
		'view?.ClientSecret.Should().StartWith("ics_");',
		'stored.Should().NotContain(view.TotpSecret,\n    "the stored row is protected");',
		'body.Should().NotContain("""\n  x\n  """ + client.ClientSecret);',
		'body.Should().NotContain($$"""{{client.ClientSecret}}""");',
		'stored.Should().NotContain(@"x" + view.TotpSecret);',
		'(credential + "x").Should().NotBeEmpty().And.EndWith("x");',
		'token.Should().MatchRegex("^[!-~]+$");',
		'File.ReadAllText(log).Should().NotContain(Canary).And.NotContain(token);',
		'Totp.Code(seed, step).Should().Be(expected);',
		'x.Should().Be(Totp.Code(Totp.FromBase32(AmanSeed), clock.Now));',
		'seeds.Unprotect(newSeed).Should().Be(Seed);',
		'text.Should().NotContain(Observer1).And.NotContain(Password1);',
		'(await _host.LoginAsync("x", Password)).Should().Be(x, Password);',
		// The masking and the subject reading hold: a verbatim quote, a directive's apostrophe, literals and initializers;
		// reads that carry a secret under a member or header name; a subject the check cannot read fails closed.
		'var s = @"""";\nview.ClientSecret.Should().StartWith("ics_");',
		"#region the observers' passwords\nreader.Password.Should().Be(x);\n#endregion",
		'$"{view.ClientSecret}".Should().StartWith("ics_");',
		'$"{a}{b}".Should().Contain(client.ClientSecret);',
		'new[] { a.Credential, b.Credential }.Should().OnlyHaveUniqueItems();',
		'new List<string> { a.Credential }.Should().HaveCount(1);',
		'new Settings { Password = ReaderPassword }.Should().BeEquivalentTo(x);',
		'body.GetProperty("clientSecret").GetString().Should().Be("x");',
		'stub.Requests[1].Headers.Authorization!.Parameter.Should().Be("t1");',
		'stub.Requests[1].Headers.Authorization!.ToString().Should().Be("Bearer o1");',
		'request.Headers.GetValues("X-TOTP-Code").Single().Should().HaveLength(6);',
		'x ?.Should().Be(1);'
	],
	good: [
		'(second.Data.RefreshToken == first.Data.RefreshToken).Should().BeTrue("a second tab gets the same successor");',
		'signIn.RefreshToken.Length.Should().Be(43, "256 random bits, base64url");',
		'view.ClientSecret.StartsWith("ics_", StringComparison.Ordinal).Should().BeTrue();',
		'stored.Contains(view.TotpSecret, StringComparison.Ordinal).Should().BeFalse("the stored row is protected");',
		'credential.RecoveryCodes.Count.Should().Be(10);',
		'credential.RecoveryCodes.Distinct().Count().Should().Be(10);',
		'preview.PreviewToken.Should().NotBeNullOrEmpty();',
		'created.Data.ClientSecret.Should().NotBeNull();',
		'created.Data.User.MustChangePassword.Should().BeTrue();',
		'DeviceCredentials.IsWellFormed(issued.Credential).Should().BeTrue();',
		'DeviceCredentials.Matches(issued.Credential, device.CredentialHash).Should().BeTrue();',
		'device.CredentialHash.Should().Be(DeviceCredentials.Hash(issued.Credential));',
		'SessionOf(first.Token.AccessToken).Should().Be(sessionId, "the session continues");',
		'(await _host.SessionStateAsync(SessionOf(signIn.Token.AccessToken))).Should().Be(SessionState.Revoked);',
		'third.ErrorMessages.Should().Equal(ISvcAuthenticator.InvalidCredentials);',
		'TestContext.Current.CancellationToken.Should().NotBe(default);',
		'thrown.CancellationToken.Should().Be(cancelled.Token, "the message\'s own token");',
		'stopping.Token.Should().Be(first);',
		'tokens.Should().Be(1, "the token is cached");',
		'client.TokensValidFromUtc.Should().Be(Now);',
		'client.TokenVersion.Should().Be(version + 1);',
		'first.Data.Token.RecoveryCodesRemaining.Should().Be(9);',
		'passwords.All(p => p.Length == 23).Should().BeTrue();',
		'list.Should().Contain(x => x.Credential == issued.Credential);',
		'(await oldPassword.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InvalidPassword);',
		'await shortPassword.Should().ThrowAsync<PostgresException>().WithMessage("*at least 16 characters*");',
		'// signIn.RefreshToken.Should().Be(x);\nx.Should().Be(1);',
		'x.Should().Be(1, "the RefreshToken.Should().Be(x) of a comment");',
		'(stored == view.TotpSecret).Should().BeFalse("the stored row is protected");',
		'view.Client.RequireTotpPerRequest.Should().BeTrue();',
		'auth.TotpRequired.Should().BeTrue();',
		'connection.TokenPath.Should().BeNull();',
		'OutboundRules.CheckSecret(request, kind).Should().BeEmpty();',
		'(await _host.ReadAsync<long>("SELECT count(*) FROM audit WHERE position(@secret IN payload::text) > 0", null, credential[13..])).Should().Be(0);',
		'credential.Password.Length.Should().Be(32);',
		'x.Should().Be(nameof(client.ClientSecret));',
		'scenario.Seed.Should().Be(9303);',
		'zone.ZoneKey.Should().Be("A-VIS");',
		'response.StatusCode.Should().Be(HttpStatusCode.OK);',
		'(rotated.Data.Credential != credential).Should().BeTrue("a rotation issues a new credential");',
		'var code = NewCode();\ncode.Should().Be("Z-1");',
		'TestContext.Current.CancellationToken.Should().NotBe(cts.Token);',
		'(Totp.Code(seed, step) == expected).Should().BeTrue("the RFC vector");',
		'Totp.Match(RfcSecret, Totp.Code(RfcSecret, step), now, null).Should().Be(step);',
		'(await _host.LoginAsync("x", Password)).HasErrors.Should().BeFalse();',
		'"plain".Should().StartWith("p");',
		'$"{zone.Code}-x".Should().Be("A-x");',
		'@"C:\\x".Should().EndWith("x");',
		'"""\n  raw\n  """.Should().Contain("raw");',
		'new[] { 1, 2 }.Should().HaveCount(2);',
		'new List<int> { 1 }.Should().ContainSingle();',
		'new JsonWebToken(result.Data.Token.AccessToken).GetClaim("sid").Value.Should().Be(sessionId);',
		'new Settings { Password = ReaderPassword }.Problems(database).Should().BeEmpty();',
		'body.GetProperty("code").GetString().Should().Be("S-01");',
		'stub.Requests[1].Headers.Authorization!.Scheme.Should().Be("Bearer");',
		"#region the observers' codes\nx.Should().Be(1);\n#endregion"
	]
};

function selfTest() {
	const problems = [];
	SELF_TEST.bad.forEach((source, i) => {
		if (findings(source).length === 0) problems.push(`bad ${i + 1} not found: ${source.replace(/\n/g, ' / ')}`);
	});
	SELF_TEST.good.forEach((source, i) => {
		const found = findings(source);
		if (found.length > 0) problems.push(`good ${i + 1} flagged (${found[0].reason}): ${source.replace(/\n/g, ' / ')}`);
	});
	// The real tree is read from files: prove it with a temporary copy of a bad test.
	const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-secret-assertions-'));
	try {
		const file = path.join(dir, 'BadTests.cs');
		fs.writeFileSync(file, `public class BadTests\n{\n    public void T()\n    {\n        ${SELF_TEST.bad[0]}\n    }\n}\n`);
		const found = check([file]);
		if (found.length !== 1 || found[0].line !== 5) problems.push(`a bad test file is not found on its line (${JSON.stringify(found)})`);
	} finally {
		fs.rmSync(dir, { recursive: true, force: true });
	}
	if (problems.length) {
		console.error(`secret assertion check self-test FAILED:\n${problems.join('\n')}`);
		return false;
	}
	console.log(`secret assertion check self-test: ${SELF_TEST.bad.length} bad patterns found, ${SELF_TEST.good.length} good ones quiet.`);
	return true;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
	const args = process.argv.slice(2);
	if (args.includes('--self-test')) process.exit(selfTest() ? 0 : 1);
	const files = args.length ? args.map((f) => path.resolve(f)) : testFiles();
	// Fail closed when the test projects moved or the walk broke: a check over no file finds nothing.
	const unitAndIntegration = files.filter((f) => /[\\/]Ariva\.(?:Unit|Integration)Tests[\\/]/.test(f));
	const each = ['UnitTests', 'IntegrationTests'].every((project) => files.some((f) => f.includes(`Ariva.${project}`)));
	if (!args.length && (unitAndIntegration.length < 100 || !each)) {
		console.error(`secret assertion check: ${unitAndIntegration.length} files found in Ariva.UnitTests and Ariva.IntegrationTests, fewer than 100 or one project missing: the test projects moved or the walk broke.`);
		process.exit(1);
	}
	const found = check(files);
	for (const f of found) console.log(`${f.file}:${f.line}: ${f.reason}: ${f.text}`);
	if (found.length) {
		console.error(`\n${found.length} C# assertions would print a secret when they fail (ARV-249, CWE-532): assert a boolean, a count or a shape (\`string.Equals(a, b, StringComparison.Ordinal).Should().BeTrue()\`, \`secret.Length.Should().Be(43)\`, \`text.Contains(secret).Should().BeFalse()\`), never the value.`);
		process.exit(1);
	}
	console.log(`C# test assertions: no secret value asserted in ${files.length} files.`);
}
