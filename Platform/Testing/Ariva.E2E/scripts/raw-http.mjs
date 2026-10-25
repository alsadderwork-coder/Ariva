#!/usr/bin/env node
// ARV-164b: a request whose body the server must refuse unread (a Content-Length over the endpoint's or Kestrel's limit, or a
// protected route that answers 401 before anything binds the body). fetch streams such a body; once the server has answered
// and closed the connection, a later write can fail (EPIPE, or a reset) and destroy the socket before the answer, already
// received, is read, so a test saw "fetch failed" or EPIPE instead of the 413 or 401 the server sent. `announced` writes the
// request head with the announced Content-Length and none of the body, then reads the whole answer: an answer proves the
// server decided without reading the body (it was never sent), and a close, a reset or no answer within the time fails.
// The specs use it through tests/support/raw-http.ts; four security tests depend on it failing closed, so:
//   node scripts/raw-http.mjs --self-test    prove it reads whole answers and refuses everything else (local servers only)

import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import tls from 'node:tls';
import { fileURLToPath } from 'node:url';

const HEAD_END = Buffer.from('\r\n\r\n');
const TOKEN = /^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/;
// Headers the client writes itself: a caller's own would give the request two lengths or two hosts.
const OWN = new Set(['host', 'content-length', 'transfer-encoding']);

/** A header value or the request target with a CR, LF or NUL would split the request: a test's mistake, refused. */
function plain(text, what) {
	if (/[\r\n\0]/.test(text)) throw new Error(`${what} holds a line break or NUL`);
	return text;
}

/** The body of a chunked answer once its last chunk and the empty line after any trailers have come, else undefined. */
function dechunk(data) {
	const parts = [];
	let at = 0;
	for (;;) {
		const line = data.indexOf('\r\n', at);
		if (line < 0) return undefined;
		const size = Number.parseInt(data.subarray(at, line).toString('latin1').split(';')[0].trim(), 16);
		if (!Number.isFinite(size) || size < 0) throw new Error('a malformed chunk size');
		if (size === 0) return data.indexOf(HEAD_END, line) >= 0 ? Buffer.concat(parts) : undefined;
		const start = line + 2;
		if (data.length < start + size + 2) return undefined;
		parts.push(data.subarray(start, start + size));
		at = start + size + 2;
	}
}

/**
 * Sends `method url` with only its head: the headers given, Host and the announced Content-Length, never the body. Resolves
 * with the whole final answer (an interim 1xx answer is skipped); rejects when the connection closes or resets before it,
 * when none comes within `timeoutMs`, or before connecting when the request would be malformed.
 * @param {string} method
 * @param {string} url
 * @param {{ length: number, headers?: Record<string, string>, timeoutMs?: number }} announcement
 */
export async function announced(method, url, { length, headers = {}, timeoutMs = 15_000 }) {
	if (!Number.isSafeInteger(length) || length < 0) throw new Error('the announced length is not a whole number of bytes');
	if (!TOKEN.test(method)) throw new Error('the method is not a token');
	const target = new URL(url);
	const secure = target.protocol === 'https:';
	if (!secure && target.protocol !== 'http:') throw new Error(`not an http or https URL: ${target.protocol}`);
	const port = Number(target.port || (secure ? 443 : 80));
	// URL keeps the brackets of an IPv6 host ([::1]); a socket wants the address alone, and TLS names only a host name.
	const host = target.hostname.replace(/^\[(.*)\]$/, '$1');
	const lines = [`${method} ${plain(`${target.pathname}${target.search}`, 'the path')} HTTP/1.1`, `Host: ${target.host}`];
	for (const [name, value] of Object.entries(headers)) {
		if (!TOKEN.test(name)) throw new Error('a header name is not a token');
		if (OWN.has(name.toLowerCase())) throw new Error(`the client writes ${name} itself`);
		lines.push(`${name}: ${plain(String(value), `header ${name}`)}`);
	}
	lines.push(`Content-Length: ${length}`, '', '');

	return new Promise((resolve, reject) => {
		const socket = secure
			? tls.connect({ host, port, ...(net.isIP(host) ? {} : { servername: host }) })
			: net.connect({ host, port });
		let received = Buffer.alloc(0);
		let settled = false;
		const finish = (error, answer) => {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			socket.destroy();
			if (error) reject(error);
			else resolve(answer);
		};
		const timer = setTimeout(() => finish(new Error(`no whole answer from ${target.origin}${target.pathname} within ${timeoutMs} ms`)), timeoutMs);

		/** The final answer, once its head and its whole body have come (ended: the server closed the connection). */
		const answerOf = (ended) => {
			for (;;) {
				const end = received.indexOf(HEAD_END);
				if (end < 0) return undefined;
				const [statusLine, ...fields] = received.subarray(0, end).toString('latin1').split('\r\n');
				const status = /^HTTP\/1\.[01] (\d{3})/.exec(statusLine);
				if (!status) throw new Error(`not an HTTP answer: ${statusLine.slice(0, 80)}`);
				// An interim answer (100 Continue and the like) carries no body: the final one follows it.
				if (status[1].startsWith('1')) {
					received = received.subarray(end + HEAD_END.length);
					continue;
				}
				const named = {};
				for (const field of fields) {
					const colon = field.indexOf(':');
					if (colon <= 0) continue;
					const name = field.slice(0, colon).trim().toLowerCase();
					const value = field.slice(colon + 1).trim();
					named[name] = Object.hasOwn(named, name) ? `${named[name]}, ${value}` : value;
				}
				const rest = received.subarray(end + HEAD_END.length);
				let body;
				if (/\bchunked\b/i.test(named['transfer-encoding'] ?? '')) body = dechunk(rest);
				else if (Object.hasOwn(named, 'content-length')) {
					const size = Number(named['content-length']);
					if (!Number.isSafeInteger(size) || size < 0) throw new Error('a malformed Content-Length');
					body = rest.length >= size ? rest.subarray(0, size) : undefined;
				} else if (ended) body = rest;
				if (body === undefined) return undefined;
				const text = body.toString('utf8');
				return {
					status: () => Number(status[1]),
					headers: () => ({ ...named }),
					text: async () => text,
					json: async () => JSON.parse(text),
					url: () => url
				};
			}
		};
		const settle = (ended) => {
			try {
				const answer = answerOf(ended);
				if (answer) finish(undefined, answer);
				else if (ended) finish(new Error(`the connection closed before a whole answer (${received.length} bytes received)`));
			} catch (error) {
				finish(error);
			}
		};

		socket.once(secure ? 'secureConnect' : 'connect', () => socket.write(lines.join('\r\n')));
		socket.on('data', (chunk) => {
			received = Buffer.concat([received, chunk]);
			settle(false);
		});
		socket.on('end', () => settle(true));
		socket.on('error', (error) => finish(new Error(`the connection failed before a whole answer: ${error.message}`)));
		socket.on('close', () => settle(true));
	});
}

/** A local server that answers each connection with `handle(socket, request head)`; resolves with its port. */
function server(handle, family = '127.0.0.1') {
	return new Promise((resolve, reject) => {
		const s = net.createServer((socket) => {
			let head = Buffer.alloc(0);
			socket.on('data', (chunk) => {
				const first = head.indexOf(HEAD_END) < 0;
				head = Buffer.concat([head, chunk]);
				if (first && head.indexOf(HEAD_END) >= 0) handle(socket, head.toString('latin1'));
			});
			socket.on('error', () => {});
		});
		s.once('error', reject);
		s.listen(0, family, () => resolve({ port: s.address().port, close: () => s.close() }));
	});
}

const problem = JSON.stringify({ status: 413, title: 'Too large' });
const answers = {
	length: (socket) => socket.end(`HTTP/1.1 413 Payload Too Large\r\nContent-Type: application/problem+json\r\nContent-Length: ${problem.length}\r\nConnection: close\r\nConstructor: kept\r\n\r\n${problem}`),
	chunked: (socket) => {
		socket.write('HTTP/1.1 401 Unauthorized\r\nTransfer-Encoding: chunked\r\n\r\n5\r\n{"sta\r\n');
		setTimeout(() => socket.end('9\r\ntus":401}\r\n0\r\n\r\n'), 30);
	},
	answerThenReset: (socket) => socket.write(`HTTP/1.1 413 Payload Too Large\r\nContent-Length: ${problem.length}\r\n\r\n${problem}`, () => socket.resetAndDestroy()),
	reset: (socket) => socket.resetAndDestroy(),
	cut: (socket) => socket.end('HTTP/1.1 413 Payload Too Large\r\nContent-Length: 100\r\n\r\n{"status":'),
	silent: () => {},
	interim: (socket) => socket.end(`HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 413 Payload Too Large\r\nContent-Length: ${problem.length}\r\n\r\n${problem}`),
	echoHead: (socket, head) => {
		const body = JSON.stringify(head);
		socket.end(`HTTP/1.1 200 OK\r\nContent-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`);
	}
};

async function selfTest() {
	const failures = [];
	const check = (name, ok, detail = '') => {
		console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` (${detail})` : ''}`);
		if (!ok) failures.push(name);
	};
	const outcome = (promise) => promise.then((a) => ({ answer: a }), (e) => ({ error: e.message }));
	const servers = {};
	for (const [name, handle] of Object.entries(answers)) servers[name] = await server(handle);
	const at = (name, p = '/x?q=1') => `http://127.0.0.1:${servers[name].port}${p}`;
	try {
		const a = await outcome(announced('POST', at('length'), { length: 21 * 1024 * 1024, headers: { 'Content-Type': 'application/json' } }));
		check('a 413 with a Content-Length is read whole', a.answer?.status() === 413 && a.answer.headers()['connection'] === 'close' && (await a.answer.json()).status === 413, a.error);
		check('a header named like an Object member is read as itself', a.answer?.headers()['constructor'] === 'kept');
		const c = await outcome(announced('POST', at('chunked'), { length: 2 * 1024 * 1024 }));
		check('a chunked 401 over two writes is read whole', c.answer?.status() === 401 && (await c.answer.text()) === '{"status":401}', c.error);
		let resetsRead = 0;
		for (let i = 0; i < 20; i++) if ((await outcome(announced('POST', at('answerThenReset'), { length: 5 }))).answer?.status() === 413) resetsRead++;
		check('an answer followed by a reset is read', resetsRead === 20, `${resetsRead} of 20`);
		const i = await outcome(announced('POST', at('interim'), { length: 5 }));
		check('an interim 100 is skipped for the final answer', i.answer?.status() === 413, i.error);
		const head = await outcome(announced('POST', at('echoHead', '/p/a?b=1'), { length: 7, headers: { Authorization: 'Bearer t' } }));
		const sent = head.answer ? JSON.parse(await head.answer.text()) : '';
		check('the head carries the path, Host, the headers given and the length, and no body', /^POST \/p\/a\?b=1 HTTP\/1\.1\r\nHost: 127\.0\.0\.1:\d+\r\nAuthorization: Bearer t\r\nContent-Length: 7\r\n\r\n$/.test(sent), JSON.stringify(sent).slice(0, 120));
		for (const [name, run, expected] of [
			['a reset before any answer', () => announced('POST', at('reset'), { length: 5 }), /connection failed/],
			['a close inside the answer', () => announced('POST', at('cut'), { length: 5 }), /closed before a whole answer/],
			['no answer in time', () => announced('POST', at('silent'), { length: 5, timeoutMs: 300 }), /within 300 ms/],
			['a refused connection', () => announced('POST', 'http://127.0.0.1:1/', { length: 5 }), /connection failed/],
			['a line break in a header value', () => announced('POST', at('length'), { length: 5, headers: { X: 'a\r\nB: c' } }), /line break/],
			['a header name that is not a token', () => announced('POST', at('length'), { length: 5, headers: { 'X Y': 'a' } }), /not a token/],
			['a Content-Length of the caller', () => announced('POST', at('length'), { length: 5, headers: { 'content-length': '1' } }), /writes content-length itself/],
			['a Host of the caller', () => announced('POST', at('length'), { length: 5, headers: { Host: 'x' } }), /writes Host itself/],
			['a length that is a string', () => announced('POST', at('length'), { length: '1\r\nX-Evil: 1' }), /whole number/],
			['a negative length', () => announced('POST', at('length'), { length: -1 }), /whole number/],
			['a fractional length', () => announced('POST', at('length'), { length: 1.5 }), /whole number/],
			['a method that is not a token', () => announced('POST /x HTTP/1.1\r\nX:', at('length'), { length: 1 }), /not a token/],
			['a scheme other than http and https', () => announced('POST', 'file:///etc/passwd', { length: 1 }), /not an http or https URL/]
		]) {
			const r = await outcome(run());
			check(`refused: ${name}`, r.error !== undefined && expected.test(r.error), r.error ?? 'resolved');
		}
		let v6;
		try {
			v6 = await server(answers.length, '::1');
		} catch {
			console.log('SKIP  an IPv6 loopback host in brackets (no ::1 here)');
		}
		if (v6) {
			const r = await outcome(announced('POST', `http://[::1]:${v6.port}/`, { length: 5 }));
			check('an IPv6 loopback host in brackets', r.answer?.status() === 413, r.error);
			v6.close();
		}
	} finally {
		for (const s of Object.values(servers)) s.close();
	}
	if (failures.length) console.error(`raw HTTP client self-test FAILED: ${failures.join('; ')}`);
	else console.log('raw HTTP client self-test: every case read or refused as it should be');
	return failures.length === 0;
}

// Run as a command (by its own path or through a link); imported by the specs, nothing runs.
const self = fileURLToPath(import.meta.url);
if (process.argv[1] && fs.existsSync(process.argv[1]) && [path.resolve(process.argv[1]), fs.realpathSync(process.argv[1])].includes(self)) {
	if (process.argv.length === 3 && process.argv[2] === '--self-test') selfTest().then((ok) => process.exit(ok ? 0 : 1));
	else {
		console.error('Usage: raw-http.mjs --self-test');
		process.exit(2);
	}
}
