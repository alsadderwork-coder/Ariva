// ARV-182: the scheduled backup's encryptor (Charts/platform/files/backup/ariva-age.py, the age v1 format written with
// pyca/cryptography because the TimescaleDB image has no age binary) against age itself and against an independent reading of
// the format in Node's own crypto. For payloads of 0, 1, 64 KiB less one, 64 KiB, 64 KiB and one, two chunks exactly, and three
// chunks and some: the file decrypts to the plaintext (with age and with the Node reading), --digest reports the SHA-256 and size
// of exactly the file written, and a byte changed in the payload or the header, a lost last chunk or another identity is refused.
// The encryptor refuses an identity as the recipient, a wrong checksum and a recipient with a trailing line break (without a
// traceback) without repeating them, and --check writes nothing.
//   node Platform/Cloud/Ariva.K8s/tests/backup-age.mjs
// Needs a Python 3 with pyca/cryptography (python3, /usr/bin/python3 or ARIVA_PYTHON) and age 1.1 or later
// (https://age-encryption.org) on PATH. In a gate (ARIVA_GATE, set by scripts/verify.mjs, or CI, TF_BUILD, GITHUB_ACTIONS)
// either missing fails the check: the interop with age itself is the proof (the ARV-182 review). Elsewhere a missing Python
// skips the check with a notice and a missing age runs the Node reading alone, saying so.
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const encryptor = path.resolve(here, '..', 'Helm', 'Charts', 'platform', 'files', 'backup', 'ariva-age.py');
const CHARSET = 'qpzry9x8gf2tvdw0s3jn54khce6mua7l';
const CHUNK = 64 * 1024;

function polymod(values) {
	const generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
	let check = 1;
	for (const value of values) {
		const top = check >>> 25;
		check = (((check & 0x1ffffff) << 5) ^ value) >>> 0;
		for (let i = 0; i < 5; i++) if ((top >>> i) & 1) check = (check ^ generator[i]) >>> 0;
	}
	return check;
}

/** Bech32 (not Bech32m) of bytes under a human-readable part, as age writes recipients and identities. */
function bech32(hrp, bytes) {
	const groups = [];
	let accumulator = 0;
	let bits = 0;
	for (const byte of bytes) {
		accumulator = ((accumulator << 8) | byte) & 0xffff;
		bits += 8;
		while (bits >= 5) {
			bits -= 5;
			groups.push((accumulator >>> bits) & 31);
		}
	}
	if (bits) groups.push((accumulator << (5 - bits)) & 31);
	const expanded = [...hrp].map((c) => c.charCodeAt(0) >> 5).concat([0], [...hrp].map((c) => c.charCodeAt(0) & 31));
	const mod = polymod([...expanded, ...groups, 0, 0, 0, 0, 0, 0]) ^ 1;
	for (let i = 0; i < 6; i++) groups.push((mod >>> (5 * (5 - i))) & 31);
	return `${hrp}1${groups.map((g) => CHARSET[g]).join('')}`;
}

const raw = (key, type) => Buffer.from(key.export({ format: 'jwk' })[type], 'base64url');
const hkdf = (secret, salt, info) => Buffer.from(crypto.hkdfSync('sha256', secret, salt, Buffer.from(info), 32));
const unb64 = (text) => Buffer.from(text, 'base64');

function open(key, nonce, sealed) {
	const decipher = crypto.createDecipheriv('chacha20-poly1305', key, nonce, { authTagLength: 16 });
	decipher.setAuthTag(sealed.subarray(sealed.length - 16));
	return Buffer.concat([decipher.update(sealed.subarray(0, sealed.length - 16)), decipher.final()]);
}

/** The age v1 format read with an X25519 identity: one stanza, the header MAC, every chunk with its counter, the last one flagged. */
function decrypt(file, identity) {
	const end = file.indexOf('\n---') + 4;
	const lines = file.subarray(0, end).toString('latin1').split('\n');
	if (lines[0] !== 'age-encryption.org/v1' || lines.length !== 4 || !lines[1].startsWith('-> X25519 ')) throw new Error('not one X25519 stanza');
	const share = unb64(lines[1].split(' ')[2]);
	const mine = raw(crypto.createPublicKey(identity), 'x');
	const shared = crypto.diffieHellman({ privateKey: identity, publicKey: crypto.createPublicKey({ key: { kty: 'OKP', crv: 'X25519', x: share.toString('base64url') }, format: 'jwk' }) });
	const fileKey = open(hkdf(shared, Buffer.concat([share, mine]), 'age-encryption.org/v1/X25519'), Buffer.alloc(12), unb64(lines[2]));
	const macEnd = file.indexOf('\n', end);
	const mac = crypto.createHmac('sha256', hkdf(fileKey, Buffer.alloc(0), 'header')).update(file.subarray(0, end)).digest();
	if (file[end] !== 0x20 || !crypto.timingSafeEqual(unb64(file.subarray(end + 1, macEnd).toString('latin1')), mac)) throw new Error('header MAC');
	const nonce = file.subarray(macEnd + 1, macEnd + 17);
	const payload = file.subarray(macEnd + 17);
	const key = hkdf(fileKey, nonce, 'payload');
	const size = CHUNK + 16;
	const count = Math.max(1, Math.ceil(payload.length / size));
	const parts = [];
	for (let i = 0; i < count; i++) {
		const chunkNonce = Buffer.alloc(12);
		chunkNonce.writeUIntBE(i, 5, 6);
		chunkNonce[11] = i === count - 1 ? 1 : 0;
		parts.push(open(key, chunkNonce, payload.subarray(i * size, (i + 1) * size)));
	}
	return Buffer.concat(parts);
}

const pythons = [process.env.ARIVA_PYTHON, 'python3', '/usr/bin/python3'].filter(Boolean);
// The probe uses what the encryptor uses (a Python may import the package and still lack its bindings).
const PROBE = 'from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey; from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305; X25519PrivateKey.generate()';
const python = pythons.find((candidate) => spawnSync(candidate, ['-I', '-c', PROBE], { stdio: 'ignore', shell: false }).status === 0);
const age = spawnSync('age', ['--version'], { encoding: 'utf8', shell: false }).status === 0 ? 'age' : null;
const gate = Boolean(process.env.ARIVA_GATE || process.env.CI || process.env.TF_BUILD || process.env.GITHUB_ACTIONS);
if (gate && (!python || !age)) {
	console.error(`FAIL backup age interop: ${[!python && 'no Python 3 with pyca/cryptography (set ARIVA_PYTHON)', !age && 'no age on PATH (age 1.1 or later)'].filter(Boolean).join(' and ')}; ` +
		'a gate proves the scheduled backup\'s encryptor against age itself (docs/harness/README.md)');
	process.exit(1);
}
if (!python) {
	console.warn('backup age interop: SKIPPED (no Python 3 with pyca/cryptography; set ARIVA_PYTHON). ScheduledBackupTests runs the encryptor in the pinned image.');
	process.exit(0);
}
const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-backup-age-'));
fs.chmodSync(folder, 0o700);
const failures = [];
try {
	const { privateKey, publicKey } = crypto.generateKeyPairSync('x25519');
	const recipient = bech32('age', raw(publicKey, 'x'));
	const identityText = bech32('age-secret-key-', raw(privateKey, 'd')).toUpperCase();
	const identityFile = path.join(folder, 'identity.txt');
	fs.writeFileSync(identityFile, `${identityText}\n`, { mode: 0o600 });
	const other = crypto.generateKeyPairSync('x25519').privateKey;
	const encrypt = (input, args = ['--recipient', recipient]) => spawnSync(python, ['-I', encryptor, ...args], { input, maxBuffer: 1 << 26, shell: false });
	const refusedByAge = (bytes) => {
		if (!age) return true;
		const file = path.join(folder, 'probe.age');
		fs.writeFileSync(file, bytes, { mode: 0o600 });
		return spawnSync(age, ['--decrypt', '--identity', identityFile, file], { stdio: ['ignore', 'pipe', 'pipe'], shell: false }).status !== 0;
	};
	const refusedByNode = (bytes, key = privateKey) => {
		try {
			decrypt(bytes, key);
			return false;
		} catch {
			return true;
		}
	};
	const sizes = [0, 1, CHUNK - 1, CHUNK, CHUNK + 1, 2 * CHUNK, 3 * CHUNK + 77];
	for (const size of sizes) {
		const plain = crypto.randomBytes(size);
		const digest = path.join(folder, `${size}.digest`);
		const result = encrypt(plain, ['--recipient', recipient, '--digest', digest]);
		if (result.status !== 0) {
			failures.push(`${size} bytes: the encryptor failed (${String(result.stderr).trim().split('\n').pop()})`);
			continue;
		}
		const sealed = result.stdout;
		// The SHA-256 and size of the stream, as the runner holds the published file to them.
		const reported = fs.readFileSync(digest, 'utf8');
		if (reported !== `${crypto.createHash('sha256').update(sealed).digest('hex')} ${sealed.length}\n`) failures.push(`${size} bytes: --digest reports ${reported.trim()}, not the file written`);
		if (!sealed.subarray(0, 32).toString('latin1').startsWith('age-encryption.org/v1\n-> X25519 ')) failures.push(`${size} bytes: not an age v1 file to one X25519 recipient`);
		if (!refusedByNode(sealed) && !decrypt(sealed, privateKey).equals(plain)) failures.push(`${size} bytes: the Node reading gives other bytes`);
		if (refusedByNode(sealed)) failures.push(`${size} bytes: the Node reading refuses it`);
		if (age) {
			const file = path.join(folder, `${size}.age`);
			fs.writeFileSync(file, sealed, { mode: 0o600 });
			const opened = spawnSync(age, ['--decrypt', '--identity', identityFile, file], { maxBuffer: 1 << 26, shell: false });
			if (opened.status !== 0 || !Buffer.from(opened.stdout).equals(plain)) failures.push(`${size} bytes: age does not give the plaintext back (${String(opened.stderr).trim()})`);
		}
		if (!refusedByNode(sealed, other)) failures.push(`${size} bytes: another identity opens it`);
		const payloadByte = Buffer.from(sealed);
		payloadByte[payloadByte.length - 20] ^= 1;
		if (!refusedByNode(payloadByte) || !refusedByAge(payloadByte)) failures.push(`${size} bytes: a changed payload byte is not refused`);
		const headerByte = Buffer.from(sealed);
		headerByte[40] ^= 1;
		if (!refusedByNode(headerByte) || !refusedByAge(headerByte)) failures.push(`${size} bytes: a changed header byte is not refused`);
		if (size > CHUNK) {
			const cut = sealed.subarray(0, sealed.length - ((sealed.length - (sealed.indexOf('\n', sealed.indexOf('\n---') + 4) + 17)) % (CHUNK + 16) || CHUNK + 16));
			if (!refusedByNode(cut) || !refusedByAge(cut)) failures.push(`${size} bytes: a file without its last chunk is not refused`);
		}
	}
	const identity = encrypt(Buffer.from('x'), ['--recipient', identityText]);
	if (identity.status !== 2 || String(identity.stdout).length || String(identity.stderr).toUpperCase().includes(identityText)) failures.push('an identity given as the recipient is not refused without being repeated');
	const broken = encrypt(Buffer.from('x'), ['--recipient', recipient.slice(0, -1) + (recipient.endsWith('q') ? 'p' : 'q')]);
	if (broken.status !== 2 || !String(broken.stderr).includes('wrong checksum')) failures.push('a recipient with a wrong checksum is not refused');
	const checked = encrypt(Buffer.alloc(0), ['--recipient', recipient, '--check']);
	if (checked.status !== 0 || checked.stdout.length) failures.push('--check of a good recipient fails or writes something');
	// The ARV-182 review's finding 8: a recipient with a trailing line break is refused as one, not a crash.
	for (const [label, args] of [['a trailing line break', ['--recipient', `${recipient}\n`, '--check']], ['a trailing line break while encrypting', ['--recipient', `${recipient}\n`]]]) {
		const newline = encrypt(Buffer.from('x'), args);
		if (newline.status !== 2 || /Traceback/.test(String(newline.stderr)) || !String(newline.stderr).includes('is not an age X25519 recipient')) failures.push(`a recipient with ${label} is not refused cleanly (exit ${newline.status})`);
	}
	const existing = path.join(folder, 'existing.digest');
	fs.writeFileSync(existing, 'x', { mode: 0o600 });
	if (encrypt(Buffer.from('x'), ['--recipient', recipient, '--digest', existing]).status !== 1) failures.push('--digest overwrites a file that exists');
	if (encrypt(Buffer.from('x'), ['--recipient', recipient, '--digest', 'relative/digest']).status !== 2) failures.push('--digest takes a relative path');
} finally {
	fs.rmSync(folder, { recursive: true, force: true });
}
if (failures.length) {
	console.error(`FAIL backup age interop:\n  ${failures.join('\n  ')}`);
	process.exit(1);
}
console.log(`PASS backup age interop: 7 payload sizes decrypted by ${age ? 'age and ' : ''}an independent Node reading of the format, --digest the stream's SHA-256 and size, ` +
	'every change and another identity refused, an identity as the recipient, a wrong checksum and a trailing line break refused without echo or traceback, --check silent ' +
	`(${python}${age ? '' : '; age not installed, the Node reading only'})`);
