#!/usr/bin/env node
// TLS check of an Ariva site's hosts (ARV-084; the release gate after .github/workflows/release-dev.yml's release, wiki 04).
// No dependencies beyond Node 22. It reads, it never changes anything: TLS handshakes, GET /, one POST without a body and a
// plain HTTP GET / per host.
//
//   node scripts/ops/tls-check.mjs --host <name>[=refuse|redirect] [--host ...] [--ingresses <file>] [--ca <PEM file>]
//        [--resolve <name>=<address>] [--port 443] [--http-port 80] [--timeout <seconds, 10>] [--summary <file>]
//   node scripts/ops/tls-check.mjs --self-test
//
// --host names a host and what it does with plain HTTP: refuse (the machine hosts, the default) or redirect (the web host and
// the Cronz dashboard). --ingresses reads the hosts from `kubectl get ingress -o json`: every Ingress the Ariva chart rendered
// carries ariva.dalil.tech/plain-http, and only those are checked. --ca verifies the certificates against that CA only
// (otherwise Node's bundled Mozilla roots, unless NODE_EXTRA_CA_CERTS or --use-system-ca adds others); --resolve connects to an address of your choice for a name (split DNS, before DNS exists).
// --summary appends a Markdown table (for example $GITHUB_STEP_SUMMARY).
//
// A host fails when:
// - it accepts TLS 1.0 or TLS 1.1 (offered at OpenSSL security level 0, so a server that would take them does);
// - it accepts a TLS 1.2 suite other than ECDHE with AES-GCM or ChaCha20-Poly1305 (the Mozilla intermediate list): CBC, RC4,
//   3DES or NULL suites, or AEAD suites with static RSA or DHE key exchange;
// - a TLS 1.2 or TLS 1.3 handshake fails, or its certificate does not verify for the host name (every handshake verifies it,
//   none skips verification; with a certificate that does not verify the host fails on that alone, and no weaker protocol, suite
//   or HTTPS request is offered to it);
// - GET / over HTTPS has no Strict-Transport-Security, more than one, a max-age under a year (31536000), no includeSubDomains,
//   or preload;
// - a response the ingress generates itself, the 413 to a POST that announces a body over every proxy-body-size (1 GiB, none
//   sent, refused before any pod sees it), lacks that HSTS, X-Content-Type-Options: nosniff or a Content-Security-Policy with
//   frame-ancestors 'none';
// - over plain HTTP a refuse host answers a redirect or a success instead of an error (a closed port is a refusal too), a
//   redirect host does not redirect to https://<host>, or either answers without nosniff and frame-ancestors 'none' or with
//   Strict-Transport-Security (RFC 6797 section 7.2: HTTPS only).
// Exit code 0 when every host passes, 1 when one fails, 2 on a usage error. --self-test runs the checks against throwaway local
// servers (Node https and http servers with chosen protocols, suites, certificates and headers), so CI proves every rule
// without a cluster (ci.yml security gate, node scripts/verify.mjs security).

import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import tls from 'node:tls';
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';

export const HSTS_MIN_AGE = 31536000;
/** The Mozilla intermediate TLS 1.2 suites (Server Side TLS guidelines 6.0), the controller values' ssl-ciphers. */
export const ALLOWED_TLS12 = [
	'ECDHE-ECDSA-AES128-GCM-SHA256', 'ECDHE-RSA-AES128-GCM-SHA256', 'ECDHE-ECDSA-AES256-GCM-SHA384',
	'ECDHE-RSA-AES256-GCM-SHA384', 'ECDHE-ECDSA-CHACHA20-POLY1305', 'ECDHE-RSA-CHACHA20-POLY1305'
];
const MODES = ['refuse', 'redirect'];
const HOST = /^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$/;
const PLAIN_HTTP_ANNOTATION = 'ariva.dalil.tech/plain-http';
const BODY_PROBE_BYTES = 1024 * 1024 * 1024;
const FRAME_ANCESTORS = /(^|;)\s*frame-ancestors\s+'none'\s*(;|$)/i;

export class UsageError extends Error {}

// ---------------------------------------------------------------------------------------------
// TLS suites

/**
 * The TLS 1.2 suites a host must refuse, from the ones this OpenSSL can offer: every non-TLS 1.3 suite outside ALLOWED_TLS12,
 * split into the ones without AEAD (CBC, RC4, 3DES, NULL) and the AEAD ones without ECDHE (static RSA, DHE, ARIA, CCM, ...).
 * PSK and SRP suites need a key the check does not have, so they are left out.
 */
export function refusedSuites() {
	const allowed = new Set(ALLOWED_TLS12.map((name) => name.toLowerCase()));
	const candidates = tls.getCiphers().filter((name) => !name.startsWith('tls_') && !allowed.has(name) && !/psk|srp|anon|adh|aecdh/i.test(name));
	const aead = (name) => /gcm|chacha20|ccm/i.test(name);
	return { nonAead: candidates.filter((name) => !aead(name)).map((name) => name.toUpperCase()), noEcdhe: candidates.filter(aead).map((name) => name.toUpperCase()) };
}

/** The codes OpenSSL and Node give a certificate that does not verify (the handshake itself got that far). */
const CERTIFICATE_ERRORS = new Set([
	'CERT_HAS_EXPIRED', 'CERT_NOT_YET_VALID', 'CERT_REVOKED', 'CERT_REJECTED', 'CERT_UNTRUSTED', 'CERT_SIGNATURE_FAILURE',
	'CERT_CHAIN_TOO_LONG', 'DEPTH_ZERO_SELF_SIGNED_CERT', 'SELF_SIGNED_CERT_IN_CHAIN', 'UNABLE_TO_GET_ISSUER_CERT',
	'UNABLE_TO_GET_ISSUER_CERT_LOCALLY', 'UNABLE_TO_VERIFY_LEAF_SIGNATURE', 'INVALID_CA', 'PATH_LENGTH_EXCEEDED',
	'INVALID_PURPOSE', 'HOSTNAME_MISMATCH', 'ERR_TLS_CERT_ALTNAME_INVALID'
]);

/**
 * One TLS handshake, then closes. Resolves { ok, protocol, cipher, error, certificate }. The certificate is always verified
 * (against `--ca`, or Node's bundled roots): a certificate that does not verify ends the handshake with certificate set to its
 * code, so the check judges the certificate first and offers the other protocols and suites only to a host whose certificate
 * verifies. The handshakes send no application data.
 */
export function handshake(target, { minVersion, maxVersion, ciphers }) {
	return new Promise((resolve) => {
		let settled = false;
		const done = (result) => {
			if (settled) return;
			settled = true;
			socket.destroy();
			resolve(result);
		};
		const socket = tls.connect({
			host: target.address ?? target.host, port: target.port, servername: target.host, minVersion, maxVersion, ciphers,
			ca: target.ca, rejectUnauthorized: true, ALPNProtocols: ['http/1.1']
		});
		socket.setTimeout(target.timeoutMs, () => done({ ok: false, error: 'timeout' }));
		socket.once('secureConnect', () => done({ ok: true, protocol: socket.getProtocol(), cipher: socket.getCipher()?.name }));
		socket.once('error', (error) => {
			const code = error.code ?? error.message;
			done(CERTIFICATE_ERRORS.has(code) ? { ok: false, certificate: code, error: code } : { ok: false, error: code });
		});
	});
}

// ---------------------------------------------------------------------------------------------
// HTTP probes

/** One request; resolves { status, headers (name to list of values), location } or { error }. */
function request(target, { secure, method = 'GET', headers = {} }) {
	return new Promise((resolve) => {
		const module = secure ? https : http;
		const options = {
			host: target.address ?? target.host, port: secure ? target.port : target.httpPort, method, path: '/', agent: false,
			headers: { Host: target.host, 'User-Agent': 'ariva-tls-check', Connection: 'close', ...headers }, timeout: target.timeoutMs
		};
		if (secure) Object.assign(options, { servername: target.host, ca: target.ca, rejectUnauthorized: true, minVersion: 'TLSv1.2' });
		let settled = false;
		const done = (result) => {
			if (settled) return;
			settled = true;
			resolve(result);
		};
		const req = module.request(options, (res) => {
			const values = {};
			for (let i = 0; i < res.rawHeaders.length; i += 2) (values[res.rawHeaders[i].toLowerCase()] ??= []).push(res.rawHeaders[i + 1]);
			done({ status: res.statusCode, headers: values, location: res.headers.location });
			res.destroy();
			req.destroy();
		});
		req.on('timeout', () => { done({ error: 'timeout' }); req.destroy(); });
		req.on('error', (error) => done({ error: error.code ?? error.message }));
		if (method === 'POST') req.flushHeaders();
		else req.end();
	});
}

/** Findings for the Strict-Transport-Security values of one response (rawHeaders, so a second header is seen). */
export function hstsFindings(values, where) {
	if (!values?.length) return [`${where} has no Strict-Transport-Security`];
	if (values.length > 1) return [`${where} has more than one Strict-Transport-Security (${values.join(' | ')})`];
	const parts = values[0].split(';').map((part) => part.trim()).filter(Boolean);
	const findings = [];
	const maxAge = parts.map((part) => /^max-age\s*=\s*"?(\d+)"?$/i.exec(part)).find(Boolean);
	if (!maxAge || Number(maxAge[1]) < HSTS_MIN_AGE) findings.push(`${where} has Strict-Transport-Security max-age ${maxAge?.[1] ?? 'unset'}, under a year (${HSTS_MIN_AGE})`);
	if (!parts.some((part) => /^includesubdomains$/i.test(part))) findings.push(`${where} has Strict-Transport-Security without includeSubDomains`);
	if (parts.some((part) => /^preload$/i.test(part))) findings.push(`${where} has Strict-Transport-Security with preload`);
	return findings;
}

/** Findings for the headers an ingress-generated response must carry: nosniff and frame-ancestors 'none'. */
function generatedHeaderFindings(headers, where) {
	const findings = [];
	if (!(headers['x-content-type-options'] ?? []).some((value) => value.trim().toLowerCase() === 'nosniff')) findings.push(`${where} lacks X-Content-Type-Options: nosniff`);
	if (!(headers['content-security-policy'] ?? []).some((value) => FRAME_ANCESTORS.test(value))) findings.push(`${where} lacks a Content-Security-Policy with frame-ancestors 'none'`);
	return findings;
}

// ---------------------------------------------------------------------------------------------
// the check

/**
 * Checks one host. target: { host, mode, address?, port, httpPort, ca?, timeoutMs }. Returns { findings, notes }.
 */
export async function checkHost(target, suites = refusedSuites()) {
	const findings = [];
	const notes = [];
	const offer = async (label, options, accepted) => {
		const result = await handshake(target, options);
		if (result.ok) findings.push(accepted(result));
		return result;
	};
	// The certificate first: every handshake verifies it, so a host whose certificate does not verify is judged on that alone
	// (it fails the check) and is offered nothing weaker.
	const tls13 = await handshake(target, { minVersion: 'TLSv1.3', maxVersion: 'TLSv1.3' });
	const tls12 = await handshake(target, { minVersion: 'TLSv1.2', maxVersion: 'TLSv1.2' });
	const certificate = tls13.certificate ?? tls12.certificate;
	const verified = certificate === undefined && (tls13.ok || tls12.ok);
	if (certificate !== undefined) {
		findings.push(`the certificate does not verify for ${target.host} (${certificate})`);
		notes.push('no request sent over HTTPS and no weaker protocol or suite offered: the certificate does not verify');
	} else {
		if (!tls12.ok) findings.push(`TLS 1.2 fails (${tls12.error})`);
		else if (!ALLOWED_TLS12.includes(tls12.cipher)) findings.push(`TLS 1.2 negotiates ${tls12.cipher}, not one of the Mozilla intermediate suites`);
		else notes.push(`TLS 1.2 ${tls12.cipher}`);
		if (!tls13.ok) findings.push(`TLS 1.3 fails (${tls13.error})`);
		else notes.push(`TLS 1.3 ${tls13.cipher}`);
	}
	if (verified) {
		await offer('TLS 1.0', { minVersion: 'TLSv1', maxVersion: 'TLSv1', ciphers: 'DEFAULT:@SECLEVEL=0' }, (result) => `accepts TLS 1.0 (${result.cipher})`);
		await offer('TLS 1.1', { minVersion: 'TLSv1.1', maxVersion: 'TLSv1.1', ciphers: 'DEFAULT:@SECLEVEL=0' }, (result) => `accepts TLS 1.1 (${result.cipher})`);
		// Every refused suite the server takes, one handshake each until it takes none of the rest.
		for (const [kind, list] of [['without AEAD', suites.nonAead], ['without ECDHE', suites.noEcdhe]]) {
			let remaining = [...list];
			while (remaining.length) {
				const result = await handshake(target, { minVersion: 'TLSv1.2', maxVersion: 'TLSv1.2', ciphers: `${remaining.join(':')}:@SECLEVEL=0` });
				if (!result.ok || !remaining.includes(result.cipher)) break;
				findings.push(`accepts the TLS 1.2 suite ${result.cipher} (${kind})`);
				remaining = remaining.filter((name) => name !== result.cipher);
			}
		}
		notes.push('certificate verified');
		const page = await request(target, { secure: true });
		if (page.error) findings.push(`GET / over HTTPS fails (${page.error})`);
		else findings.push(...hstsFindings(page.headers['strict-transport-security'], `GET / (${page.status})`));
		const generated = await request(target, { secure: true, method: 'POST', headers: { 'Content-Length': String(BODY_PROBE_BYTES), 'Content-Type': 'application/octet-stream' } });
		if (generated.error) findings.push(`the body limit probe fails (${generated.error})`);
		else if (generated.status !== 413) findings.push(`a POST announcing ${BODY_PROBE_BYTES} bytes is answered ${generated.status}, not the ingress's 413`);
		else findings.push(...hstsFindings(generated.headers['strict-transport-security'], 'the ingress\'s 413'), ...generatedHeaderFindings(generated.headers, 'the ingress\'s 413'));
	}
	const plain = await request(target, { secure: false });
	if (plain.error) {
		if (target.mode === 'redirect') findings.push(`plain HTTP fails (${plain.error}) instead of redirecting to HTTPS`);
		else notes.push(`plain HTTP refused (${plain.error})`);
	} else {
		const where = `plain HTTP ${plain.status}`;
		if (target.mode === 'refuse') {
			if (plain.status >= 300 && plain.status < 400) findings.push(`answers plain HTTP with a redirect (${plain.status} to ${plain.location ?? 'nowhere'}); a machine host refuses it`);
			else if (plain.status < 400) findings.push(`serves plain HTTP (${plain.status}); a machine host refuses it`);
			else notes.push(`plain HTTP refused (${plain.status})`);
		} else if (![301, 302, 307, 308].includes(plain.status)) findings.push(`does not redirect plain HTTP to HTTPS (${plain.status})`);
		else if (!String(plain.location ?? '').toLowerCase().startsWith(`https://${target.host}`)) findings.push(`redirects plain HTTP to ${plain.location ?? 'nowhere'}, not https://${target.host}`);
		else notes.push(`plain HTTP redirected (${plain.status})`);
		findings.push(...generatedHeaderFindings(plain.headers, where));
		if (plain.headers['strict-transport-security']) findings.push(`${where} sends Strict-Transport-Security over plain HTTP (RFC 6797 section 7.2)`);
	}
	return { findings, notes };
}

/**
 * The client must be able to offer what the hosts must refuse: TLS 1.0, TLS 1.1 and a suite without AEAD, proven against a
 * loopback server that takes them. Otherwise a refusal would prove nothing, and the check fails instead.
 */
export async function clientCapabilityFindings() {
	const ca = issueCertificates();
	const server = tls.createServer({ key: ca.leaf.key, cert: ca.leaf.cert, minVersion: 'TLSv1', maxVersion: 'TLSv1.3', ciphers: 'ALL:@SECLEVEL=0' });
	await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
	const target = { host: 'capability.ariva.test', address: '127.0.0.1', port: server.address().port, ca: ca.ca, timeoutMs: 5000 };
	const findings = [];
	for (const [label, options] of [
		['TLS 1.0', { minVersion: 'TLSv1', maxVersion: 'TLSv1', ciphers: 'DEFAULT:@SECLEVEL=0' }],
		['TLS 1.1', { minVersion: 'TLSv1.1', maxVersion: 'TLSv1.1', ciphers: 'DEFAULT:@SECLEVEL=0' }],
		['a TLS 1.2 suite without AEAD', { minVersion: 'TLSv1.2', maxVersion: 'TLSv1.2', ciphers: 'ECDHE-ECDSA-AES128-SHA256:@SECLEVEL=0' }]
	]) {
		const result = await handshake(target, options);
		if (!result.ok) findings.push(`this Node (OpenSSL ${process.versions.openssl}) cannot offer ${label} (${result.error}), so it cannot prove a host refuses it`);
	}
	server.close();
	return findings;
}

/** The hosts of `kubectl get ingress -o json`: those of every Ingress with ariva.dalil.tech/plain-http, with its mode. */
export function hostsFromIngresses(json) {
	const hosts = new Map();
	for (const item of json?.items ?? []) {
		const mode = item?.metadata?.annotations?.[PLAIN_HTTP_ANNOTATION];
		if (mode === undefined) continue;
		if (!MODES.includes(mode)) throw new UsageError(`Ingress ${item.metadata?.name}: ${PLAIN_HTTP_ANNOTATION} is ${mode}, neither refuse nor redirect`);
		for (const host of (item.spec?.rules ?? []).map((rule) => rule.host).filter(Boolean)) {
			if (hosts.has(host) && hosts.get(host) !== mode) throw new UsageError(`host ${host}: its Ingresses disagree on plain HTTP (${hosts.get(host)} and ${mode})`);
			hosts.set(host, mode);
		}
	}
	return hosts;
}

export function parseCommandLine(argv) {
	const { values } = parseArgs({
		args: argv, allowPositionals: false, strict: true,
		options: {
			host: { type: 'string', multiple: true }, ingresses: { type: 'string' }, ca: { type: 'string' }, resolve: { type: 'string', multiple: true },
			port: { type: 'string' }, 'http-port': { type: 'string' }, timeout: { type: 'string' }, summary: { type: 'string' }, 'self-test': { type: 'boolean' }
		}
	});
	const hosts = new Map();
	if (values.ingresses) for (const [host, mode] of hostsFromIngresses(JSON.parse(fs.readFileSync(values.ingresses, 'utf8')))) hosts.set(host, mode);
	for (const entry of values.host ?? []) {
		const [host, mode = 'refuse'] = entry.split('=');
		if (!MODES.includes(mode)) throw new UsageError(`--host ${entry}: the mode is refuse or redirect`);
		hosts.set(host.toLowerCase(), mode);
	}
	for (const host of hosts.keys()) if (!HOST.test(host)) throw new UsageError(`${host} is not a host name`);
	const addresses = new Map();
	for (const entry of values.resolve ?? []) {
		const [host, address] = entry.split('=');
		if (!HOST.test(String(host).toLowerCase()) || !net.isIP(String(address))) throw new UsageError(`--resolve ${entry}: expected <host>=<IP address>`);
		addresses.set(host.toLowerCase(), address);
	}
	const port = (value, fallback, label) => {
		const number = value === undefined ? fallback : Number(value);
		if (!Number.isInteger(number) || number < 1 || number > 65535) throw new UsageError(`${label} must be a port number`);
		return number;
	};
	const timeout = values.timeout === undefined ? 10 : Number(values.timeout);
	if (!(timeout > 0 && timeout <= 120)) throw new UsageError('--timeout is 1 to 120 seconds');
	return {
		selfTest: values['self-test'] === true, hosts, addresses, ca: values.ca ? fs.readFileSync(values.ca, 'utf8') : undefined,
		port: port(values.port, 443, '--port'), httpPort: port(values['http-port'], 80, '--http-port'), timeoutMs: timeout * 1000, summary: values.summary
	};
}

async function main(argv) {
	const options = parseCommandLine(argv);
	if (options.selfTest) return selfTest();
	if (options.hosts.size === 0) throw new UsageError('name the hosts (--host, --ingresses)');
	const capability = await clientCapabilityFindings();
	if (capability.length) {
		for (const finding of capability) console.error(`tls-check: ${finding}`);
		return 1;
	}
	const suites = refusedSuites();
	const rows = [];
	let failed = false;
	for (const [host, mode] of options.hosts) {
		const target = { host, mode, address: options.addresses.get(host), port: options.port, httpPort: options.httpPort, ca: options.ca, timeoutMs: options.timeoutMs };
		const { findings, notes } = await checkHost(target, suites);
		failed ||= findings.length > 0;
		rows.push({ host, mode, findings, notes });
		console.log(`tls-check: ${host} (${mode}): ${findings.length ? 'FAIL' : 'PASS'} (${notes.join('; ')})`);
		for (const finding of findings) console.log(`  - ${finding}`);
	}
	if (options.summary) {
		const cell = (text) => String(text).replace(/\|/g, '\\|');
		fs.appendFileSync(options.summary, ['## TLS check (ARV-084)', '', '| Host | Plain HTTP | Result | Findings |', '|---|---|---|---|',
			...rows.map((row) => `| ${row.host} | ${row.mode} | ${row.findings.length ? 'FAIL' : 'PASS'} | ${cell(row.findings.join('; ') || row.notes.join('; '))} |`), ''].join('\n') + '\n');
	}
	return failed ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// certificates for the self-test and the capability probe: X.509 v3 built with node:crypto, no tools

const der = (tag, ...parts) => {
	const body = Buffer.concat(parts);
	let length;
	if (body.length < 0x80) length = Buffer.from([body.length]);
	else {
		const bytes = [];
		for (let n = body.length; n > 0; n >>= 8) bytes.unshift(n & 0xff);
		length = Buffer.from([0x80 | bytes.length, ...bytes]);
	}
	return Buffer.concat([Buffer.from([tag]), length, body]);
};
const sequence = (...parts) => der(0x30, ...parts);
const integer = (bytes) => {
	let value = Buffer.from(bytes);
	while (value.length > 1 && value[0] === 0 && !(value[1] & 0x80)) value = value.subarray(1);
	return der(0x02, value[0] & 0x80 ? Buffer.concat([Buffer.from([0]), value]) : value);
};
const objectId = (dotted) => {
	const [first, second, ...rest] = dotted.split('.').map(Number);
	const bytes = [40 * first + second];
	for (const part of rest) {
		const encoded = [part & 0x7f];
		for (let value = part >> 7; value > 0; value >>= 7) encoded.unshift((value & 0x7f) | 0x80);
		bytes.push(...encoded);
	}
	return der(0x06, Buffer.from(bytes));
};
const bitString = (bytes, unused = 0) => der(0x03, Buffer.from([unused]), bytes);
const time = (date) => {
	const two = (n) => String(n).padStart(2, '0');
	const rest = `${two(date.getUTCMonth() + 1)}${two(date.getUTCDate())}${two(date.getUTCHours())}${two(date.getUTCMinutes())}${two(date.getUTCSeconds())}Z`;
	const year = date.getUTCFullYear();
	return year >= 1950 && year < 2050 ? der(0x17, Buffer.from(`${String(year).slice(2)}${rest}`)) : der(0x18, Buffer.from(`${year}${rest}`));
};
const commonName = (name) => sequence(der(0x31, sequence(objectId('2.5.4.3'), der(0x0c, Buffer.from(name)))));
const extension = (id, critical, value) => sequence(objectId(id), ...(critical ? [der(0x01, Buffer.from([0xff]))] : []), der(0x04, value));
/** KeyUsage as a DER bit string: the bits as one byte, unused trailing bits counted. */
const keyUsage = (byte) => {
	let unused = 0;
	while (unused < 7 && !(byte & (1 << unused))) unused += 1;
	return bitString(Buffer.from([byte]), unused);
};

function certificate({ subject, issuer, publicKey, signingKey, isCa, names, notBefore, notAfter }) {
	const rsa = signingKey.asymmetricKeyType === 'rsa';
	const algorithm = rsa ? sequence(objectId('1.2.840.113549.1.1.11'), der(0x05)) : sequence(objectId('1.2.840.10045.4.3.2'));
	const leafUsage = publicKey.asymmetricKeyType === 'rsa' ? 0xa0 : 0x80;
	const extensions = [
		extension('2.5.29.19', true, isCa ? sequence(der(0x01, Buffer.from([0xff]))) : sequence()),
		extension('2.5.29.15', true, keyUsage(isCa ? 0x06 : leafUsage))
	];
	if (names) extensions.push(extension('2.5.29.17', false, sequence(...names.map((name) => der(0x82, Buffer.from(name))))));
	const tbs = sequence(der(0xa0, integer(Buffer.from([2]))), integer(crypto.randomBytes(16)), algorithm, commonName(issuer), sequence(time(notBefore), time(notAfter)),
		commonName(subject), publicKey.export({ type: 'spki', format: 'der' }), der(0xa3, sequence(...extensions)));
	const signed = sequence(tbs, algorithm, bitString(crypto.sign('sha256', tbs, signingKey)));
	return `-----BEGIN CERTIFICATE-----\n${signed.toString('base64').match(/.{1,64}/g).join('\n')}\n-----END CERTIFICATE-----\n`;
}

/**
 * A throwaway CA and certificates it issues, valid from an hour ago for a day: leaf (ECDSA P-256, for the names given), rsa
 * (RSA 2048, same names), and on request expired, wrongName and an untrusted one from a CA of its own.
 */
export function issueCertificates(names = ['capability.ariva.test']) {
	const now = Date.now();
	const valid = { notBefore: new Date(now - 3600e3), notAfter: new Date(now + 86400e3) };
	const caKeys = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
	const caCert = certificate({ subject: 'Ariva TLS check test CA', issuer: 'Ariva TLS check test CA', publicKey: caKeys.publicKey, signingKey: caKeys.privateKey, isCa: true, ...valid });
	const issue = (type, extra) => {
		const keys = type === 'rsa' ? crypto.generateKeyPairSync('rsa', { modulusLength: 2048 }) : crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
		const cert = certificate({ subject: names[0], issuer: 'Ariva TLS check test CA', publicKey: keys.publicKey, signingKey: caKeys.privateKey, names, ...valid, ...extra });
		return { key: keys.privateKey.export({ type: 'pkcs8', format: 'pem' }), cert: cert + caCert };
	};
	const result = { ca: caCert, leaf: issue('ec'), rsa: issue('rsa') };
	result.expired = issue('ec', { notBefore: new Date(now - 3 * 86400e3), notAfter: new Date(now - 86400e3) });
	result.wrongName = issue('ec', { names: ['other.ariva.test'], subject: 'other.ariva.test' });
	const otherKeys = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
	const otherCa = certificate({ subject: 'Another CA', issuer: 'Another CA', publicKey: otherKeys.publicKey, signingKey: otherKeys.privateKey, isCa: true, ...valid });
	const untrustedKeys = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
	result.untrusted = { key: untrustedKeys.privateKey.export({ type: 'pkcs8', format: 'pem' }),
		cert: certificate({ subject: names[0], issuer: 'Another CA', publicKey: untrustedKeys.publicKey, signingKey: otherKeys.privateKey, names, ...valid }) + otherCa };
	return result;
}

// ---------------------------------------------------------------------------------------------
// self-test: throwaway local servers, one per case, each with exactly the findings it must give

const GOOD_HSTS = `max-age=${HSTS_MIN_AGE}; includeSubDomains`;
const SECURE_HEADERS = { 'Strict-Transport-Security': GOOD_HSTS, 'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': "frame-ancestors 'none'" };
const PLAIN_HEADERS = { 'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': "frame-ancestors 'none'" };

/**
 * A stand-in for an Ariva host behind its ingress: an https server (protocols, suites and certificate of the case; GET / with
 * the page headers, a POST over the body limit answered by the "ingress" 413 with the generated headers) and a plain http
 * server (refuse: 403; redirect: 308 to https://<host>/), each overridable by the case.
 */
async function standIn(certificates, host, mode, overrides) {
	const servers = [];
	const tlsOptions = { key: certificates.leaf.key, cert: certificates.leaf.cert, minVersion: 'TLSv1.2', maxVersion: 'TLSv1.3', ciphers: ALLOWED_TLS12.join(':'), ...overrides.tls };
	const secure = https.createServer(tlsOptions, (req, res) => {
		if (req.method === 'POST' && Number(req.headers['content-length']) > 10 * 1024 * 1024 && !overrides.noBodyLimit) {
			res.writeHead(413, present(SECURE_HEADERS, overrides.generated, { Connection: 'close' }));
			res.end();
			return;
		}
		res.writeHead(404, present(SECURE_HEADERS, overrides.page, { Connection: 'close' }));
		res.end();
	});
	servers.push(secure);
	await new Promise((resolve) => secure.listen(0, '127.0.0.1', resolve));
	let httpPort;
	if (overrides.plainClosed) {
		const probe = net.createServer();
		await new Promise((resolve) => probe.listen(0, '127.0.0.1', resolve));
		httpPort = probe.address().port;
		await new Promise((resolve) => probe.close(resolve));
	} else {
		const plain = http.createServer((req, res) => {
			const answer = overrides.plain ?? (mode === 'redirect' ? { status: 308, headers: { Location: `https://${host}/` } } : { status: 403, headers: {} });
			res.writeHead(answer.status, present(PLAIN_HEADERS, answer.headers, { Connection: 'close' }));
			res.end();
		});
		servers.push(plain);
		await new Promise((resolve) => plain.listen(0, '127.0.0.1', resolve));
		httpPort = plain.address().port;
	}
	return { target: { host, mode, address: '127.0.0.1', port: secure.address().port, httpPort, ca: certificates.ca, timeoutMs: 5000 }, close: () => servers.forEach((server) => server.close()) };
}

const remove = (name) => ({ [name]: undefined });
/** Response headers without the ones a case removed (undefined). */
const present = (...sets) => Object.fromEntries(Object.entries(Object.assign({}, ...sets)).filter(([, value]) => value !== undefined));

/** [name, mode, overrides, the findings expected (substrings, one per finding, in order)] */
function selfTestCases(certificates) {
	return [
		['a host that refuses plain HTTP, as the chart serves it', 'refuse', {}, []],
		['a host that redirects plain HTTP, as the chart serves it', 'redirect', {}, []],
		['a refuse host whose plain HTTP port is closed', 'refuse', { plainClosed: true }, []],
		['an RSA certificate with the Mozilla suites', 'refuse', { tls: { key: certificates.rsa.key, cert: certificates.rsa.cert } }, []],
		['TLS 1.0 accepted', 'refuse', { tls: { minVersion: 'TLSv1', ciphers: `${ALLOWED_TLS12.join(':')}:ECDHE-ECDSA-AES128-SHA:@SECLEVEL=0`, secureOptions: crypto.constants.SSL_OP_NO_TLSv1_1 } },
			['accepts TLS 1.0', 'accepts the TLS 1.2 suite ECDHE-ECDSA-AES128-SHA (without AEAD)']],
		['TLS 1.1 accepted', 'refuse', { tls: { minVersion: 'TLSv1.1', ciphers: `${ALLOWED_TLS12.join(':')}:ECDHE-ECDSA-AES256-SHA:@SECLEVEL=0` } },
			['accepts TLS 1.1', 'accepts the TLS 1.2 suite ECDHE-ECDSA-AES256-SHA (without AEAD)']],
		['a CBC suite in TLS 1.2', 'refuse', { tls: { ciphers: `${ALLOWED_TLS12.join(':')}:ECDHE-ECDSA-AES128-SHA256` } }, ['accepts the TLS 1.2 suite ECDHE-ECDSA-AES128-SHA256 (without AEAD)']],
		['two CBC suites in TLS 1.2', 'refuse', { tls: { ciphers: `${ALLOWED_TLS12.join(':')}:ECDHE-ECDSA-AES128-SHA256:ECDHE-ECDSA-AES256-SHA384` } },
			['accepts the TLS 1.2 suite ECDHE-ECDSA-AES', 'accepts the TLS 1.2 suite ECDHE-ECDSA-AES']],
		['static RSA key exchange in TLS 1.2', 'refuse', { tls: { key: certificates.rsa.key, cert: certificates.rsa.cert, ciphers: `${ALLOWED_TLS12.join(':')}:AES128-GCM-SHA256` } },
			['accepts the TLS 1.2 suite AES128-GCM-SHA256 (without ECDHE)']],
		['no TLS 1.3', 'refuse', { tls: { maxVersion: 'TLSv1.2' } }, ['TLS 1.3 fails']],
		['no TLS 1.2', 'refuse', { tls: { minVersion: 'TLSv1.3' } }, ['TLS 1.2 fails']],
		['a certificate from another CA', 'refuse', { tls: { key: certificates.untrusted.key, cert: certificates.untrusted.cert } }, ['the certificate does not verify']],
		['a certificate for another name', 'refuse', { tls: { key: certificates.wrongName.key, cert: certificates.wrongName.cert } }, ['the certificate does not verify for tls-check.ariva.test (ERR_TLS_CERT_ALTNAME_INVALID)']],
		['an expired certificate', 'refuse', { tls: { key: certificates.expired.key, cert: certificates.expired.cert } }, ['the certificate does not verify for tls-check.ariva.test (CERT_HAS_EXPIRED)']],
		['no HSTS on the page', 'refuse', { page: remove('Strict-Transport-Security') }, ['GET / (404) has no Strict-Transport-Security']],
		['six months of HSTS', 'refuse', { page: { 'Strict-Transport-Security': 'max-age=15724800; includeSubDomains' } }, ['max-age 15724800, under a year']],
		['HSTS without includeSubDomains', 'refuse', { page: { 'Strict-Transport-Security': `max-age=${HSTS_MIN_AGE}` } }, ['without includeSubDomains']],
		['HSTS with preload', 'refuse', { page: { 'Strict-Transport-Security': `${GOOD_HSTS}; preload` } }, ['with preload']],
		['two HSTS headers', 'refuse', { page: { 'Strict-Transport-Security': ['max-age=15724800; includeSubDomains', GOOD_HSTS] } }, ['has more than one Strict-Transport-Security']],
		['an ingress 413 without HSTS', 'refuse', { generated: remove('Strict-Transport-Security') }, ["the ingress's 413 has no Strict-Transport-Security"]],
		['an ingress 413 without nosniff', 'refuse', { generated: remove('X-Content-Type-Options') }, ["the ingress's 413 lacks X-Content-Type-Options: nosniff"]],
		['an ingress 413 without frame-ancestors', 'refuse', { generated: { 'Content-Security-Policy': "default-src 'none'" } }, ["the ingress's 413 lacks a Content-Security-Policy with frame-ancestors 'none'"]],
		['no body limit at the ingress', 'refuse', { noBodyLimit: true }, ['is answered 404, not the ingress\'s 413']],
		['a machine host that redirects plain HTTP', 'refuse', { plain: { status: 308, headers: { Location: 'https://tls-check.ariva.test/' } } }, ['answers plain HTTP with a redirect (308']],
		['a machine host that serves plain HTTP', 'refuse', { plain: { status: 200, headers: {} } }, ['serves plain HTTP (200)']],
		['a refusal without nosniff', 'refuse', { plain: { status: 403, headers: remove('X-Content-Type-Options') } }, ['plain HTTP 403 lacks X-Content-Type-Options: nosniff']],
		['a refusal with HSTS', 'refuse', { plain: { status: 403, headers: { 'Strict-Transport-Security': GOOD_HSTS } } }, ['sends Strict-Transport-Security over plain HTTP']],
		['a browser host that refuses plain HTTP', 'redirect', { plain: { status: 403, headers: {} } }, ['does not redirect plain HTTP to HTTPS (403)']],
		['a browser host that redirects elsewhere', 'redirect', { plain: { status: 308, headers: { Location: 'http://tls-check.ariva.test/' } } }, ['redirects plain HTTP to http://tls-check.ariva.test/, not https://tls-check.ariva.test']],
		['a browser host whose plain HTTP port is closed', 'redirect', { plainClosed: true }, ['plain HTTP fails (ECONNREFUSED) instead of redirecting']],
		['a redirect without frame-ancestors', 'redirect', { plain: { status: 308, headers: { Location: 'https://tls-check.ariva.test/', 'Content-Security-Policy': undefined } } },
			["plain HTTP 308 lacks a Content-Security-Policy with frame-ancestors 'none'"]]
	];
}

async function selfTest() {
	const failures = [];
	const host = 'tls-check.ariva.test';
	const certificates = issueCertificates([host]);
	const capability = await clientCapabilityFindings();
	if (capability.length) failures.push(...capability);
	const suites = refusedSuites();
	if (!suites.nonAead.includes('ECDHE-ECDSA-AES128-SHA256') || !suites.noEcdhe.includes('AES128-GCM-SHA256') || ALLOWED_TLS12.some((name) => [...suites.nonAead, ...suites.noEcdhe].includes(name))) {
		failures.push('refusedSuites does not hold the CBC and static RSA suites, or holds an allowed one');
	}
	const cases = selfTestCases(certificates);
	for (const [label, mode, overrides, expected] of cases) {
		const stand = await standIn(certificates, host, mode, overrides);
		try {
			const { findings } = await checkHost(stand.target, suites);
			const matched = expected.length === findings.length && expected.every((rule, index) => findings[index]?.includes(rule));
			if (!matched) failures.push(`${label}: expected ${expected.length ? expected.join(' | ') : 'no finding'}, found ${findings.length ? findings.join(' | ') : 'none'}`);
		} finally {
			stand.close();
		}
	}
	// The command line: modes, names and addresses are checked; the Ingresses give the chart's hosts and modes only.
	const usage = [
		[['--host', 'a.example=allow'], 'the mode is refuse or redirect'], [['--host', 'a b'], 'is not a host name'],
		[['--host', 'a.example', '--resolve', 'a.example=not-an-address'], 'expected <host>=<IP address>'], [['--host', 'a.example', '--port', '0'], '--port must be a port number'],
		[['--host', 'a.example', '--timeout', '0'], '--timeout is 1 to 120 seconds']
	];
	for (const [argv, message] of usage) {
		try {
			parseCommandLine(argv);
			failures.push(`${argv.join(' ')}: accepted`);
		} catch (error) {
			if (!(error instanceof UsageError) || !error.message.includes(message)) failures.push(`${argv.join(' ')}: ${error.message}, not "${message}"`);
		}
	}
	const ingresses = (items) => ({ items: items.map(([name, host, mode]) => ({ metadata: { name, annotations: mode ? { [PLAIN_HTTP_ANNOTATION]: mode } : {} }, spec: { rules: [{ host }] } })) });
	try {
		assert.deepEqual([...hostsFromIngresses(ingresses([['api-main', 'api.example', 'refuse'], ['web', 'web.example', 'redirect'], ['web-api', 'web.example', 'redirect'], ['solver', 'acme.example', undefined]]))],
			[['api.example', 'refuse'], ['web.example', 'redirect']]);
		assert.throws(() => hostsFromIngresses(ingresses([['web', 'web.example', 'redirect'], ['web-api', 'web.example', 'refuse']])), /disagree on plain HTTP/);
		assert.throws(() => hostsFromIngresses(ingresses([['web', 'web.example', 'open']])), /neither refuse nor redirect/);
	} catch (error) {
		failures.push(`hostsFromIngresses: ${error.message}`);
	}
	if (failures.length) {
		console.error('tls-check self-test FAILED');
		for (const failure of failures) console.error(`  ${failure}`);
		return 1;
	}
	console.log(`tls-check self-test: ${cases.length} stand-in hosts gave exactly their findings (protocols, suites, certificates, HSTS, the ingress's 413, plain HTTP), ` +
		`${usage.length + 3} command line and Ingress cases, and this Node (OpenSSL ${process.versions.openssl}) can offer TLS 1.0, TLS 1.1 and suites without AEAD.`);
	return 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === fs.realpathSync(process.argv[1])) {
	main(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (error) => {
		console.error(`tls-check: ${error.message}`);
		process.exitCode = error instanceof UsageError ? 2 : 1;
	});
}
