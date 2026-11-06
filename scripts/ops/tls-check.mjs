#!/usr/bin/env node
// TLS check of an Ariva site's hosts (ARV-084; the release gate after github_archived/workflows/release-dev.yml's release, wiki 04).
// No dependencies beyond Node 22. It reads, it never changes anything: TLS handshakes, GET /, GET /health/readiness, one POST
// without a body and a plain HTTP GET per path of each host.
//
//   node scripts/ops/tls-check.mjs --host <name>[<path>][=refuse|redirect] [--host ...] [--ingresses <file>] [--ca <PEM file>]
//        [--resolve <name>=<address>] [--port 443] [--http-port 80] [--timeout <seconds, 10>] [--summary <file>]
//   node scripts/ops/tls-check.mjs --self-test
//
// --host names a host and what it does with plain HTTP: refuse (the machine endpoints, the default) or redirect (the web app
// and the Cronz dashboard), at / or at the path written after the name (ARV-199: --host ariva.example/api=refuse; the mode
// holds per path, as ingress-nginx redirects per location). --ingresses reads the hosts and paths from `kubectl get ingress
// -o json`: every Ingress the Ariva chart rendered carries ariva.dalil.tech/plain-http, and only those are checked, at every
// path they route. --ca verifies the certificates against that CA only
// (otherwise Node's bundled Mozilla roots, unless NODE_EXTRA_CA_CERTS or --use-system-ca adds others); --resolve connects to an address of your choice for a name (split DNS, before DNS exists).
// --summary appends a Markdown table (for example $GITHUB_STEP_SUMMARY).
//
// A host fails when:
// - it accepts TLS 1.0 or TLS 1.1 (offered at OpenSSL security level 0, so a server that would take them does; an answer that
//   brings a certificate that does not verify, other than the host's main one, counts as accepted, as it does for a suite);
// - it accepts a TLS 1.2 suite other than ECDHE with AES-GCM or ChaCha20-Poly1305 (the Mozilla intermediate list): CBC, RC4,
//   3DES or NULL suites, or AEAD suites with static RSA or DHE key exchange;
// - a TLS 1.2 or TLS 1.3 handshake fails, or its certificate does not verify for the host name (every handshake verifies it,
//   none skips verification; with a certificate that does not verify the host fails on that alone, and no weaker protocol, suite
//   or HTTPS request is offered to it);
// - GET / over HTTPS has no Strict-Transport-Security, more than one, a max-age under a year (31536000), no includeSubDomains,
//   or preload; or, when an Ingress routes / (always with --host), sets no affinity cookie or one that is not __Host-ariva-...
//   with Secure, Path=/, SameSite=Strict and no Domain (ARV-199: the chart's name and attributes, as the live controller
//   writes them; a 403 without one, a source range refusing before routing, is noted instead);
// - a response the ingress generates itself, the 413 to a POST that announces a body over every proxy-body-size (1 GiB, none
//   sent, refused before any pod sees it), lacks that HSTS, X-Content-Type-Options: nosniff or a Content-Security-Policy with
//   frame-ancestors 'none', object-src 'none' and base-uri 'none' (ASVS 5.0 V3.4.3, ARV-204);
// - GET /health/readiness over HTTPS is answered anything but 404 or 403 (the ingress routes the health probes, which stay on
//   the pods' own ports; ARV-085), or its 404 lacks that HSTS, nosniff or the policy (frame-ancestors, object-src and base-uri
//   'none', ARV-204);
// - over plain HTTP, at each of its paths (and, for a path other than /, that path with a trailing slash, the location real
//   clients reach), a refuse path answers anything but the ingress's 403 (a closed port is a refusal too; any other status may
//   come from the pod, a sign the chart's refusal is not applied), a redirect path does not redirect to https://<host>, or
//   either answers without nosniff and the policy (frame-ancestors, object-src and base-uri 'none', ARV-204) or with
//   Strict-Transport-Security (RFC 6797 section 7.2).
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
/** A path the chart routes and the plain HTTP probe requests as it is: a literal prefix (the chart refuses regular expressions). */
const PROBE_PATH = /^\/[A-Za-z0-9._~/-]*$/;
/** A literal path without . or .. segments or an empty one, so it cannot normalise into another location (as Kubernetes refuses for Prefix paths). */
const probePath = (path) => PROBE_PATH.test(path) && !/(^|\/)\.\.?(\/|$)/.test(path) && !path.includes('//');
/** The key of a path as nginx locates it: /api and /api/ are one Prefix location, so the trailing slash is dropped (not from /). */
const pathKey = (path) => (path.length > 1 ? path.replace(/\/+$/, '') || '/' : path);
/** The chart's affinity cookie (ARV-199): __Host-ariva-<name>; a name such as ariva-<name> or __Secure-ariva-<name> is the same cookie misnamed. */
const AFFINITY_COOKIE = /^(?:__[A-Za-z]+-)?ariva-/;
const BODY_PROBE_BYTES = 1024 * 1024 * 1024;
/** ARV-084, ARV-204: what the ingress's own policy names, each 'none' (ASVS 5.0 V3.4.3: object-src and base-uri on every policy). */
const INGRESS_POLICY = new Map([
	['frame-ancestors', /(^|;)\s*frame-ancestors\s+'none'\s*(;|$)/i],
	['object-src', /(^|;)\s*object-src\s+'none'\s*(;|$)/i],
	['base-uri', /(^|;)\s*base-uri\s+'none'\s*(;|$)/i]
]);
/** A health probe path (ARV-085): the chart's server-snippet answers it 404 on every host, before any location. */
export const HEALTH_PROBE = '/health/readiness';

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
function request(target, { secure, method = 'GET', headers = {}, path = '/' }) {
	return new Promise((resolve) => {
		const module = secure ? https : http;
		const options = {
			host: target.address ?? target.host, port: secure ? target.port : target.httpPort, method, path, agent: false,
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

/**
 * Findings for the headers an ingress-generated response must carry: nosniff, and frame-ancestors, object-src and base-uri 'none'
 * in its policies (each policy is enforced, so a directive in any of them counts).
 */
function generatedHeaderFindings(headers, where) {
	const findings = [];
	if (!(headers['x-content-type-options'] ?? []).some((value) => value.trim().toLowerCase() === 'nosniff')) findings.push(`${where} lacks X-Content-Type-Options: nosniff`);
	const policies = headers['content-security-policy'] ?? [];
	const missing = [...INGRESS_POLICY].filter(([, pattern]) => !policies.some((value) => pattern.test(value))).map(([name]) => `${name} 'none'`);
	if (missing.length) findings.push(`${where} lacks a Content-Security-Policy with ${missing.join(', ')}`);
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
	// A probe that ends on a certificate error was accepted: the server answered the version and suite offered before the
	// certificate was judged (only a host presenting another certificate for them can get there, its main one having verified).
	const offer = async (label, options, accepted) => {
		const result = await handshake(target, options);
		if (result.ok) findings.push(accepted(result));
		else if (result.certificate !== undefined) findings.push(`accepts ${label}, with a certificate that does not verify (${result.certificate})`);
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
				if (result.certificate !== undefined) {
					// Which suite it took is lost with the socket: one finding for the rest, and no further offer.
					findings.push(`accepts a TLS 1.2 suite ${kind} from ${remaining.join(', ')}, with a certificate that does not verify (${result.certificate})`);
					break;
				}
				if (!result.ok || !remaining.includes(result.cipher)) break;
				findings.push(`accepts the TLS 1.2 suite ${result.cipher} (${kind})`);
				remaining = remaining.filter((name) => name !== result.cipher);
			}
		}
		notes.push('certificate verified');
		const page = await request(target, { secure: true });
		if (page.error) findings.push(`GET / over HTTPS fails (${page.error})`);
		else {
			findings.push(...hstsFindings(page.headers['strict-transport-security'], `GET / (${page.status})`));
			const affinity = (page.headers['set-cookie'] ?? []).some((value) => AFFINITY_COOKIE.test(String(value).split('=')[0].trim()));
			if (!plainPaths(target).some((entry) => entry.path === '/')) notes.push('no affinity cookie check: no Ingress of the host routes /');
			// A source range (or ModSecurity) refuses with 403 before the request is routed, so no affinity cookie is written then;
			// run the check from inside the range to read it (wiki 10 section 4.14).
			else if (page.status === 403 && !affinity) notes.push('affinity cookie not read: GET / refused 403 before routing (a source range?)');
			else findings.push(...affinityCookieFindings(page.headers['set-cookie'], `GET / (${page.status})`));
		}
		const generated = await request(target, { secure: true, method: 'POST', headers: { 'Content-Length': String(BODY_PROBE_BYTES), 'Content-Type': 'application/octet-stream' } });
		if (generated.error) findings.push(`the body limit probe fails (${generated.error})`);
		else if (generated.status !== 413) findings.push(`a POST announcing ${BODY_PROBE_BYTES} bytes is answered ${generated.status}, not the ingress's 413`);
		else findings.push(...hstsFindings(generated.headers['strict-transport-security'], 'the ingress\'s 413'), ...generatedHeaderFindings(generated.headers, 'the ingress\'s 413'));
		const probe = await request(target, { secure: true, path: HEALTH_PROBE });
		if (probe.error) findings.push(`GET ${HEALTH_PROBE} over HTTPS fails (${probe.error})`);
		else if (![403, 404].includes(probe.status)) findings.push(`GET ${HEALTH_PROBE} is answered ${probe.status}, not 404: the ingress routes the health probes, which stay on the pods' own ports`);
		else {
			notes.push(`health probes not routed (${probe.status})`);
			findings.push(...hstsFindings(probe.headers['strict-transport-security'], `GET ${HEALTH_PROBE} (${probe.status})`), ...generatedHeaderFindings(probe.headers, `GET ${HEALTH_PROBE} (${probe.status})`));
		}
	}
	for (const { path, mode } of plainPaths(target)) {
		await plainFindings(target, path, mode, findings, notes);
		if (path !== '/') await plainFindings(target, `${path}/`, mode, findings, notes);
	}
	return { findings, notes };
}

/** The paths whose plain HTTP answer is checked, each with its mode: the target's own, or / with the target's mode. */
const plainPaths = (target) => target.paths ?? [{ path: '/', mode: target.mode }];

/**
 * Plain HTTP at one path of a host (ARV-084; per path since ARV-199): a refuse path answers an error or no connection, a
 * redirect path a redirect to https://<host>; either with nosniff and frame-ancestors, object-src and base-uri 'none' (ARV-204)
 * and without HSTS.
 */
async function plainFindings(target, path, mode, findings, notes) {
	const at = path === '/' ? '' : ` ${path}`;
	const plain = await request(target, { secure: false, path });
	if (plain.error) {
		if (mode === 'redirect') findings.push(`plain HTTP${at} fails (${plain.error}) instead of redirecting to HTTPS`);
		else notes.push(`plain HTTP${at} refused (${plain.error})`);
		return;
	}
	const where = `plain HTTP${at} ${plain.status}`;
	if (mode === 'refuse') {
		if (plain.status >= 300 && plain.status < 400) findings.push(`answers plain HTTP${at} with a redirect (${plain.status} to ${plain.location ?? 'nowhere'}); a machine endpoint refuses it`);
		else if (plain.status < 400) findings.push(`serves plain HTTP${at} (${plain.status}); a machine endpoint refuses it`);
		else if (plain.status !== 403) findings.push(`answers plain HTTP${at} with ${plain.status}, not the ingress's 403: the request may have reached the pod`);
		else notes.push(`plain HTTP${at} refused (${plain.status})`);
	} else if (![301, 302, 307, 308].includes(plain.status)) findings.push(`does not redirect plain HTTP${at} to HTTPS (${plain.status})`);
	else if (!String(plain.location ?? '').toLowerCase().startsWith(`https://${target.host}`)) findings.push(`redirects plain HTTP${at} to ${plain.location ?? 'nowhere'}, not https://${target.host}`);
	else notes.push(`plain HTTP${at} redirected (${plain.status})`);
	findings.push(...generatedHeaderFindings(plain.headers, where));
	if (plain.headers['strict-transport-security']) findings.push(`${where} sends Strict-Transport-Security over plain HTTP (RFC 6797 section 7.2)`);
}

/**
 * The affinity cookies of one response's Set-Cookie values (ARV-199): at least one, each named __Host-ariva-..., Secure,
 * Path=/, SameSite=Strict and without Domain. ingress-nginx writes it from the chart's annotations when its sticky balancer
 * routes the request, so this reads what the live controller does with them.
 */
export function affinityCookieFindings(values, where) {
	const cookies = (values ?? []).map((value) => {
		const [pair, ...attributes] = String(value).split(';').map((part) => part.trim());
		const name = pair.slice(0, Math.max(0, pair.indexOf('=')));
		return { name, attributes: new Map(attributes.map((attribute) => {
			const at = attribute.indexOf('=');
			return at < 0 ? [attribute.toLowerCase(), ''] : [attribute.slice(0, at).trim().toLowerCase(), attribute.slice(at + 1).trim()];
		})) };
	}).filter((cookie) => AFFINITY_COOKIE.test(cookie.name));
	if (cookies.length === 0) return [`${where} sets no affinity cookie (__Host-ariva-<name>): the chart's sticky routing is not in effect`];
	const findings = [];
	for (const { name, attributes } of cookies) {
		const cookie = `${where} sets the affinity cookie ${name}`;
		if (!name.startsWith('__Host-')) findings.push(`${cookie} without the __Host- prefix`);
		if (!attributes.has('secure')) findings.push(`${cookie} without Secure`);
		if (attributes.get('path') !== '/') findings.push(`${cookie} with Path ${attributes.get('path') ?? '(none)'}, not /`);
		if (attributes.has('domain')) findings.push(`${cookie} with Domain ${attributes.get('domain')}, which a __Host- cookie must not have`);
		if ((attributes.get('samesite') ?? '').toLowerCase() !== 'strict') findings.push(`${cookie} with SameSite ${attributes.get('samesite') ?? '(none)'}, not Strict`);
	}
	return findings;
}

/** A host's plain HTTP modes for the summary: the mode alone when the host has only /, otherwise each path with its mode. */
export const describePaths = (paths) => (paths.size === 1 && paths.has('/') ? paths.get('/') : [...paths].map(([path, mode]) => `${path} ${mode}`).join(', '));

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

/**
 * The hosts of `kubectl get ingress -o json`: those of every Ingress with ariva.dalil.tech/plain-http, each with the paths its
 * Ingresses route and their modes (ARV-199: one host may redirect / and refuse /api). Two Ingresses that route one path of a
 * host with different modes are a usage error, and so is a path the probe could not request as it is.
 */
export function hostsFromIngresses(json) {
	const hosts = new Map();
	for (const item of json?.items ?? []) {
		const mode = item?.metadata?.annotations?.[PLAIN_HTTP_ANNOTATION];
		if (mode === undefined) continue;
		if (!MODES.includes(mode)) throw new UsageError(`Ingress ${item.metadata?.name}: ${PLAIN_HTTP_ANNOTATION} is ${mode}, neither refuse nor redirect`);
		for (const rule of (item.spec?.rules ?? []).filter((entry) => entry.host)) {
			const paths = hosts.get(rule.host) ?? new Map();
			hosts.set(rule.host, paths);
			for (const routed of (rule.http?.paths ?? []).map((entry) => entry.path ?? '/').concat(rule.http?.paths?.length ? [] : ['/'])) {
				if (!probePath(routed)) throw new UsageError(`Ingress ${item.metadata?.name}: the path ${routed} is not a literal path the probe can request`);
				const path = pathKey(routed);
				if (paths.has(path) && paths.get(path) !== mode) throw new UsageError(`host ${rule.host}: its Ingresses disagree on plain HTTP for ${path} (${paths.get(path)} and ${mode})`);
				paths.set(path, mode);
			}
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
	const hosts = values.ingresses ? hostsFromIngresses(JSON.parse(fs.readFileSync(values.ingresses, 'utf8'))) : new Map();
	for (const entry of values.host ?? []) {
		const [target, mode = 'refuse'] = entry.split('=');
		if (!MODES.includes(mode)) throw new UsageError(`--host ${entry}: the mode is refuse or redirect`);
		const slash = target.indexOf('/');
		const [host, path] = slash < 0 ? [target.toLowerCase(), '/'] : [target.slice(0, slash).toLowerCase(), target.slice(slash)];
		if (!probePath(path)) throw new UsageError(`--host ${entry}: the path is a literal path such as /api`);
		const paths = hosts.get(host) ?? new Map();
		hosts.set(host, paths.set(pathKey(path), mode));
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
	for (const [host, paths] of options.hosts) {
		const mode = describePaths(paths);
		const target = { host, paths: [...paths].map(([path, pathMode]) => ({ path, mode: pathMode })), address: options.addresses.get(host), port: options.port, httpPort: options.httpPort, ca: options.ca, timeoutMs: options.timeoutMs };
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
 * (RSA 2048, same names), expired (ECDSA) and expiredRsa, wrongName, and an untrusted one from a CA of its own.
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
	result.expiredRsa = issue('rsa', { notBefore: new Date(now - 3 * 86400e3), notAfter: new Date(now - 86400e3) });
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
const GOOD_POLICY = "frame-ancestors 'none'; object-src 'none'; base-uri 'none'";
const SECURE_HEADERS = { 'Strict-Transport-Security': GOOD_HSTS, 'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': GOOD_POLICY };
/** The page's affinity cookie as ingress-nginx writes it from the chart's annotations (ARV-199). */
const GOOD_COOKIE = '__Host-ariva-web=4f6b0c; Path=/; Secure; HttpOnly; SameSite=Strict';
const PLAIN_HEADERS = { 'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': GOOD_POLICY };

/**
 * A stand-in for an Ariva host behind its ingress: an https server (protocols, suites and certificate of the case; GET / with
 * the page headers, GET /health/readiness answered by the "ingress" 404 with the generated headers, a POST over the body limit
 * answered by the "ingress" 413 with them) and a plain http server (refuse: 403; redirect: 308 to https://<host> and the path),
 * each overridable by the case. mode is one mode for /, or [path, mode] pairs, the longest matching prefix answering as nginx's
 * Prefix locations do; overrides.plain is an answer, or a function of the path giving one (undefined: the mode's own).
 */
async function standIn(certificates, host, mode, overrides) {
	const paths = typeof mode === 'string' ? [['/', mode]] : mode;
	const modeAt = (url) => paths.filter(([path]) => url === path || url.startsWith(path.endsWith('/') ? path : `${path}/`) || path === '/')
		.sort(([a], [b]) => b.length - a.length)[0]?.[1] ?? 'refuse';
	const servers = [];
	const tlsOptions = { key: certificates.leaf.key, cert: certificates.leaf.cert, minVersion: 'TLSv1.2', maxVersion: 'TLSv1.3', ciphers: ALLOWED_TLS12.join(':'), ...overrides.tls };
	const secure = https.createServer(tlsOptions, (req, res) => {
		if (req.method === 'POST' && Number(req.headers['content-length']) > 10 * 1024 * 1024 && !overrides.noBodyLimit) {
			res.writeHead(413, present(SECURE_HEADERS, overrides.generated, { Connection: 'close' }));
			res.end();
			return;
		}
		if (req.url === HEALTH_PROBE) {
			res.writeHead(overrides.health?.status ?? 404, present(SECURE_HEADERS, overrides.health?.headers, { Connection: 'close' }));
			res.end();
			return;
		}
		res.writeHead(overrides.pageStatus ?? 404, present(SECURE_HEADERS, { 'Set-Cookie': GOOD_COOKIE }, overrides.page, { Connection: 'close' }));
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
			const own = typeof overrides.plain === 'function' ? overrides.plain(req.url) : overrides.plain;
			const answer = own ?? (modeAt(req.url) === 'redirect' ? { status: 308, headers: { Location: `https://${host}${req.url}` } } : { status: 403, headers: {} });
			res.writeHead(answer.status, present(PLAIN_HEADERS, answer.headers, { Connection: 'close' }));
			res.end();
		});
		servers.push(plain);
		await new Promise((resolve) => plain.listen(0, '127.0.0.1', resolve));
		httpPort = plain.address().port;
	}
	return { target: { host, paths: paths.map(([path, pathMode]) => ({ path, mode: pathMode })), address: '127.0.0.1', port: secure.address().port, httpPort, ca: certificates.ca, timeoutMs: 5000 },
		close: () => servers.forEach((server) => server.close()) };
}

const remove = (name) => ({ [name]: undefined });
/** Response headers without the ones a case removed (undefined). */
const present = (...sets) => Object.fromEntries(Object.entries(Object.assign({}, ...sets)).filter(([, value]) => value !== undefined));

/** The web host's paths and modes as the chart renders them (ARV-199). */
const WEB_PATHS = [['/', 'redirect'], ['/api', 'refuse'], ['/hubs', 'refuse']];

/** [name, mode (or [path, mode] pairs), overrides, the findings expected (substrings or patterns, one per finding, in order)] */
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
		// The ECDSA certificate verifies for TLS 1.2 and 1.3; TLS 1.0 and the CBC suite can only use the expired RSA one.
		['an expired certificate behind TLS 1.0 and a CBC suite only', 'refuse', { tls: {
			key: [certificates.leaf.key, certificates.expiredRsa.key], cert: [certificates.leaf.cert, certificates.expiredRsa.cert], minVersion: 'TLSv1',
			ciphers: `${ALLOWED_TLS12.filter((name) => name.includes('ECDSA')).join(':')}:ECDHE-RSA-AES128-SHA:@SECLEVEL=0`, secureOptions: crypto.constants.SSL_OP_NO_TLSv1_1 } },
			['accepts TLS 1.0, with a certificate that does not verify (CERT_HAS_EXPIRED)', /^accepts a TLS 1\.2 suite without AEAD from .*ECDHE-RSA-AES128-SHA\b.*, with a certificate that does not verify \(CERT_HAS_EXPIRED\)$/]],
		['no HSTS on the page', 'refuse', { page: remove('Strict-Transport-Security') }, ['GET / (404) has no Strict-Transport-Security']],
		['six months of HSTS', 'refuse', { page: { 'Strict-Transport-Security': 'max-age=15724800; includeSubDomains' } }, ['max-age 15724800, under a year']],
		['HSTS without includeSubDomains', 'refuse', { page: { 'Strict-Transport-Security': `max-age=${HSTS_MIN_AGE}` } }, ['without includeSubDomains']],
		['HSTS with preload', 'refuse', { page: { 'Strict-Transport-Security': `${GOOD_HSTS}; preload` } }, ['with preload']],
		['two HSTS headers', 'refuse', { page: { 'Strict-Transport-Security': ['max-age=15724800; includeSubDomains', GOOD_HSTS] } }, ['has more than one Strict-Transport-Security']],
		['an ingress 413 without HSTS', 'refuse', { generated: remove('Strict-Transport-Security') }, ["the ingress's 413 has no Strict-Transport-Security"]],
		['an ingress 413 without nosniff', 'refuse', { generated: remove('X-Content-Type-Options') }, ["the ingress's 413 lacks X-Content-Type-Options: nosniff"]],
		['an ingress 413 without frame-ancestors', 'refuse', { generated: { 'Content-Security-Policy': "default-src 'none'" } }, ["the ingress's 413 lacks a Content-Security-Policy with frame-ancestors 'none'"]],
		['an ingress 413 without object-src', 'refuse', { generated: { 'Content-Security-Policy': "frame-ancestors 'none'; base-uri 'none'" } },
			[/^the ingress's 413 lacks a Content-Security-Policy with object-src 'none'$/]],
		['an ingress 413 without base-uri', 'refuse', { generated: { 'Content-Security-Policy': "frame-ancestors 'none'; object-src 'none'" } },
			[/^the ingress's 413 lacks a Content-Security-Policy with base-uri 'none'$/]],
		['an ingress 413 with the directives in two policies', 'refuse', { generated: { 'Content-Security-Policy': ["frame-ancestors 'none'", "object-src 'none'; base-uri 'none'"] } }, []],
		['no body limit at the ingress', 'refuse', { noBodyLimit: true }, ['is answered 404, not the ingress\'s 413']],
		['a host that routes its readiness probe', 'refuse', { health: { status: 200, headers: {} } }, ['GET /health/readiness is answered 200, not 404']],
		['a probe refusal without HSTS', 'redirect', { health: { status: 404, headers: remove('Strict-Transport-Security') } }, ['GET /health/readiness (404) has no Strict-Transport-Security']],
		['a probe refusal without nosniff', 'refuse', { health: { status: 404, headers: remove('X-Content-Type-Options') } }, ['GET /health/readiness (404) lacks X-Content-Type-Options: nosniff']],
		['a probe refusal without frame-ancestors', 'refuse', { health: { status: 403, headers: { 'Content-Security-Policy': "default-src 'none'" } } }, ["GET /health/readiness (403) lacks a Content-Security-Policy with frame-ancestors 'none'"]],
		['a machine host that redirects plain HTTP', 'refuse', { plain: { status: 308, headers: { Location: 'https://tls-check.ariva.test/' } } }, ['answers plain HTTP with a redirect (308']],
		['a machine host that serves plain HTTP', 'refuse', { plain: { status: 200, headers: {} } }, ['serves plain HTTP (200)']],
		['a machine host whose pod answers plain HTTP', 'refuse', { plain: { status: 404, headers: {} } }, ["answers plain HTTP with 404, not the ingress's 403: the request may have reached the pod"]],
		['a refusal without nosniff', 'refuse', { plain: { status: 403, headers: remove('X-Content-Type-Options') } }, ['plain HTTP 403 lacks X-Content-Type-Options: nosniff']],
		['a refusal with HSTS', 'refuse', { plain: { status: 403, headers: { 'Strict-Transport-Security': GOOD_HSTS } } }, ['sends Strict-Transport-Security over plain HTTP']],
		['a browser host that refuses plain HTTP', 'redirect', { plain: { status: 403, headers: {} } }, ['does not redirect plain HTTP to HTTPS (403)']],
		['a browser host that redirects elsewhere', 'redirect', { plain: { status: 308, headers: { Location: 'http://tls-check.ariva.test/' } } }, ['redirects plain HTTP to http://tls-check.ariva.test/, not https://tls-check.ariva.test']],
		['a browser host whose plain HTTP port is closed', 'redirect', { plainClosed: true }, ['plain HTTP fails (ECONNREFUSED) instead of redirecting']],
		['a redirect without frame-ancestors', 'redirect', { plain: { status: 308, headers: { Location: 'https://tls-check.ariva.test/', 'Content-Security-Policy': undefined } } },
			["plain HTTP 308 lacks a Content-Security-Policy with frame-ancestors 'none'"]],
		// ARV-199: the web host redirects its app and refuses /api and /hubs, each path checked against its own mode.
		['a web host that redirects / and refuses /api and /hubs, as the chart serves it', WEB_PATHS, {}, []],
		['a web host whose /api redirects plain HTTP', WEB_PATHS, { plain: (url) => (url === '/api' ? { status: 308, headers: { Location: 'https://tls-check.ariva.test/api' } } : undefined) },
			['answers plain HTTP /api with a redirect (308 to https://tls-check.ariva.test/api); a machine endpoint refuses it']],
		['a web host whose /hubs serves plain HTTP', WEB_PATHS, { plain: (url) => (url === '/hubs' ? { status: 200, headers: {} } : undefined) }, ['serves plain HTTP /hubs (200)']],
		['a web host that refuses its app too', WEB_PATHS, { plain: { status: 403, headers: {} } }, ['does not redirect plain HTTP to HTTPS (403)']],
		['a web host whose /api refusal lacks nosniff', WEB_PATHS, { plain: (url) => (url === '/api' ? { status: 403, headers: remove('X-Content-Type-Options') } : undefined) },
			['plain HTTP /api 403 lacks X-Content-Type-Options: nosniff']],
		['a web host whose /api reaches the pod over plain HTTP', WEB_PATHS, { plain: (url) => (url.startsWith('/api') ? { status: 404, headers: {} } : undefined) },
			["answers plain HTTP /api with 404, not the ingress's 403", "answers plain HTTP /api/ with 404, not the ingress's 403"]],
		['a web host whose /hubs/ location redirects', WEB_PATHS, { plain: (url) => (url === '/hubs/' ? { status: 308, headers: { Location: 'https://tls-check.ariva.test/hubs/' } } : undefined) },
			['answers plain HTTP /hubs/ with a redirect (308']],
		// ARV-199: the affinity cookie as the live controller writes it.
		['no affinity cookie', 'refuse', { page: { 'Set-Cookie': undefined } }, ['GET / (404) sets no affinity cookie']],
		['an affinity cookie without its prefix', 'refuse', { page: { 'Set-Cookie': 'ariva-web=1; Path=/; Secure; SameSite=Strict' } }, ['sets the affinity cookie ariva-web without the __Host- prefix']],
		['an affinity cookie with the __Secure- prefix', 'refuse', { page: { 'Set-Cookie': '__Secure-ariva-web=1; Path=/; Secure; SameSite=Strict' } }, ['sets the affinity cookie __Secure-ariva-web without the __Host- prefix']],
		['an affinity cookie without Secure', 'refuse', { page: { 'Set-Cookie': '__Host-ariva-web=1; Path=/; SameSite=Strict' } }, ['sets the affinity cookie __Host-ariva-web without Secure']],
		['an affinity cookie for one path', 'refuse', { page: { 'Set-Cookie': '__Host-ariva-web=1; Path=/api; Secure; SameSite=Strict' } }, ['with Path /api, not /']],
		['an affinity cookie with a Domain', 'refuse', { page: { 'Set-Cookie': '__Host-ariva-web=1; Path=/; Domain=ariva.test; Secure; SameSite=Strict' } }, ['with Domain ariva.test, which a __Host- cookie must not have']],
		['an affinity cookie sent cross site', 'refuse', { page: { 'Set-Cookie': '__Host-ariva-web=1; Path=/; Secure; SameSite=Lax' } }, ['with SameSite Lax, not Strict']],
		["a pod's own cookie beside the affinity cookie", 'refuse', { page: { 'Set-Cookie': [GOOD_COOKIE, '__Secure-ariva_rt=x; Path=/api/auth; Secure; HttpOnly; SameSite=Strict'] } }, []],
		['a host behind a source range, checked from outside it', 'redirect', { pageStatus: 403, page: { 'Set-Cookie': undefined }, health: { status: 403, headers: {} } }, []],
		['a refused page that still sets a bad affinity cookie', 'refuse', { pageStatus: 403, page: { 'Set-Cookie': 'ariva-web=1; Path=/; Secure; SameSite=Strict' } }, ['sets the affinity cookie ariva-web without the __Host- prefix']]
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
			const matched = expected.length === findings.length && expected.every((rule, index) => (typeof rule === 'string' ? findings[index]?.includes(rule) : rule.test(findings[index] ?? '')));
			if (!matched) failures.push(`${label}: expected ${expected.length ? expected.join(' | ') : 'no finding'}, found ${findings.length ? findings.join(' | ') : 'none'}`);
		} finally {
			stand.close();
		}
	}
	// The command line: modes, names and addresses are checked; the Ingresses give the chart's hosts and modes only.
	const usage = [
		[['--host', 'a.example=allow'], 'the mode is refuse or redirect'], [['--host', 'a b'], 'is not a host name'],
		[['--host', 'a.example', '--resolve', 'a.example=not-an-address'], 'expected <host>=<IP address>'], [['--host', 'a.example', '--port', '0'], '--port must be a port number'],
		[['--host', 'a.example', '--timeout', '0'], '--timeout is 1 to 120 seconds'], [['--host', 'a.example/a pi=refuse'], 'the path is a literal path such as /api'],
		[['--host', 'a.example/api/../health=refuse'], 'the path is a literal path such as /api'], [['--host', 'a.example//api=refuse'], 'the path is a literal path such as /api']
	];
	for (const [argv, message] of usage) {
		try {
			parseCommandLine(argv);
			failures.push(`${argv.join(' ')}: accepted`);
		} catch (error) {
			if (!(error instanceof UsageError) || !error.message.includes(message)) failures.push(`${argv.join(' ')}: ${error.message}, not "${message}"`);
		}
	}
	const ingresses = (items) => ({ items: items.map(([name, host, mode, paths]) => ({ metadata: { name, annotations: mode ? { [PLAIN_HTTP_ANNOTATION]: mode } : {} },
		spec: { rules: [{ host, ...(paths ? { http: { paths: paths.map((path) => ({ path, pathType: 'Prefix' })) } } : {}) }] } })) });
	const hostsOf = (json) => [...hostsFromIngresses(json)].map(([host, paths]) => [host, [...paths]]);
	try {
		assert.deepEqual(hostsOf(ingresses([['api-main', 'api.example', 'refuse'], ['web', 'web.example', 'redirect', ['/']], ['web-api', 'web.example', 'refuse', ['/api']],
			['web-hubs', 'web.example', 'refuse', ['/hubs']], ['solver', 'acme.example', undefined, ['/.well-known/acme-challenge/x']]])),
		[['api.example', [['/', 'refuse']]], ['web.example', [['/', 'redirect'], ['/api', 'refuse'], ['/hubs', 'refuse']]]]);
		assert.throws(() => hostsFromIngresses(ingresses([['web', 'web.example', 'redirect', ['/api']], ['web-api', 'web.example', 'refuse', ['/api']]])), /disagree on plain HTTP for \/api/);
		assert.throws(() => hostsFromIngresses(ingresses([['web', 'web.example', 'redirect', ['/api/']], ['web-api', 'web.example', 'refuse', ['/api']]])), /disagree on plain HTTP for \/api /, 'one location with or without the slash');
		assert.throws(() => hostsFromIngresses(ingresses([['web', 'web.example', 'open']])), /neither refuse nor redirect/);
		assert.throws(() => hostsFromIngresses(ingresses([['web', 'web.example', 'refuse', ['/api(/|$)(.*)']]])), /not a literal path/);
		assert.deepEqual([...parseCommandLine(['--host', 'Web.Example=redirect', '--host', 'web.example/api']).hosts.get('web.example')], [['/', 'redirect'], ['/api', 'refuse']]);
		assert.equal(describePaths(new Map([['/', 'redirect'], ['/api', 'refuse']])), '/ redirect, /api refuse');
		assert.equal(describePaths(new Map([['/', 'refuse']])), 'refuse');
	} catch (error) {
		failures.push(`hostsFromIngresses: ${error.message}`);
	}
	if (failures.length) {
		console.error('tls-check self-test FAILED');
		for (const failure of failures) console.error(`  ${failure}`);
		return 1;
	}
	console.log(`tls-check self-test: ${cases.length} stand-in hosts gave exactly their findings (protocols, suites, certificates, HSTS, the ingress's 413, the private probes, plain HTTP), ` +
		`${usage.length + 8} command line and Ingress cases, and this Node (OpenSSL ${process.versions.openssl}) can offer TLS 1.0, TLS 1.1 and suites without AEAD.`);
	return 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === fs.realpathSync(process.argv[1])) {
	main(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (error) => {
		console.error(`tls-check: ${error.message}`);
		process.exitCode = error instanceof UsageError ? 2 : 1;
	});
}
