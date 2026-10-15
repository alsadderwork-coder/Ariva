#!/usr/bin/env node
// Ariva backup and restore from an operator's machine (ARV-126; wiki 04 section 10). No dependencies beyond Node 22,
// kubectl, age (https://age-encryption.org, v1.1 or later) and ssh-keygen (OpenSSH 8.1 or later). It never runs inside an
// Ariva host.
//
//   node scripts/ops/ariva-ops.mjs backup --namespace <ns> --out <dir> --recipient-file <age recipients> --signing-key <ssh key> [--files]
//   node scripts/ops/ariva-ops.mjs restore --namespace <ns> --in <file.dump.age> --identity <age identity>
//        (--manifest <set.manifest.json> --allowed-signers <file> | --sha256 <hex from your own operations log>)
//   node scripts/ops/ariva-ops.mjs files-restore (the same options, with the .files.tar.age file)
//   node scripts/ops/ariva-ops.mjs keyring --namespace <ns> --certificate <tls.crt> [--certificate <previous.crt>]
//   node scripts/ops/ariva-ops.mjs check --namespace <ns> [--runtime-login <name>] [--reader-login <name>]
//   node scripts/ops/ariva-ops.mjs after-kafka-loss --namespace <ns> --confirm-stream-stopped [--dry-run]
//   node scripts/ops/ariva-ops.mjs --self-test
// Options for every command: --context <kube context>, --pod <database pod> (timescaledb-0), --container <name>
// (timescaledb), --plan (print the commands without running them).
//
// What it guarantees:
// - The database work runs in the TimescaleDB pod through scripts/ops/ariva-db.sh (copied to the pod's /tmp), as the
//   migration login with the password the pod holds; no password passes through this machine or a command line.
// - Every backup file is encrypted with age to the given recipients while it streams; nothing unencrypted is written here.
//   Files are created 0600 and renamed into place only when complete. The manifest names each file with its SHA-256 and is
//   signed with the operator's SSH key (ssh-keygen -Y sign, namespace ariva-backup).
// - A restore runs the dump's SQL as the database superuser, so the archive's origin is the gate. age is encryption to a
//   public recipient: it proves a file was not altered after it was encrypted, not who encrypted it. The origin comes from
//   the manifest's signature, verified against an allowed-signers file the operator keeps outside the backup store, before
//   any hash in the manifest is used; the only alternative is a SHA-256 the operator passes from their own log. The file is
//   then copied into a private folder (0700), the copy is hashed, and both decryption passes read that copy only, so the
//   file cannot be swapped between the check and the restore; the folder is removed afterwards, also on failure. Every
//   refusal comes before any kubectl step (CWE-269, CWE-345, CWE-367).
// - The table of contents check in the pod (kinds, names and owners; not the SQL) and ariva-db.sh check after the restore
//   are defence in depth, never the gate.
// - Commands are argument arrays started without a shell; every name is checked against a strict pattern first (CWE-78).
// - The Data Protection certificate's private key is never read: keyring takes certificates only (their SHA-256).

import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const DB_SCRIPT = path.join(HERE, 'ariva-db.sh');
const POD_SCRIPT = '/tmp/ariva-db.sh';
const SIGNATURE_NAMESPACE = 'ariva-backup';
const MANIFEST_FORMAT = 'ariva-backup-set/1';
const VERIFIED_COPY = '@verified-copy';

// The programs this tool starts. The self-test replaces kubectl and age with stand-ins; ssh-keygen is always the real one.
const TOOLS = { kubectl: ['kubectl'], age: ['age'], 'ssh-keygen': ['ssh-keygen'] };
const OUT = { log: (...lines) => console.log(...lines), interactive: true };
const TEMP_FOLDERS = new Set();

function tool(cmd, args) {
  const [program, ...before] = TOOLS[cmd] ?? [cmd];
  return [program, [...before, ...args]];
}

// ---------------------------------------------------------------------------------------------
// validation (CWE-78): fixed shapes only

export const PATTERNS = {
  namespace: /^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$/,
  pod: /^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$/,
  container: /^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$/,
  filesTarget: /^((deploy|deployment|pod)\/)?[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$/,
  context: /^[A-Za-z0-9][A-Za-z0-9._@:/-]{0,252}$/,
  login: /^[a-z_][a-z0-9_]{0,62}$/,
  filesRoot: /^\/[A-Za-z0-9._/-]{1,200}$/,
  sha256: /^[0-9a-f]{64}$/,
  principal: /^[A-Za-z0-9._@+-]{1,128}$/
};

export class UsageError extends Error {}

export class Refused extends Error {}

export function check(kind, value) {
  if (typeof value !== 'string' || !PATTERNS[kind].test(value) || (kind === 'filesRoot' && value.includes('..')))
    throw new UsageError(`--${kind.replace(/[A-Z]/g, c => '-' + c.toLowerCase())} is not a valid value`);
  return value;
}

// A local path given on the command line: no control character, never read as an option, made absolute.
export function localPath(value, what) {
  if (typeof value !== 'string' || value.length === 0 || value.length > 4096 || /[\u0000-\u001f\u007f]/.test(value))
    throw new UsageError(`${what} is not a usable path`);
  return path.resolve(value);
}

const DEFAULTS = { pod: 'timescaledb-0', container: 'timescaledb', filesTarget: 'deploy/api-main-deployment', filesContainer: 'api-main', filesRoot: '/app/data/files' };

const VALUE_OPTIONS = new Set(['--namespace', '--context', '--pod', '--container', '--out', '--recipient-file', '--signing-key', '--in', '--identity',
  '--sha256', '--manifest', '--signature', '--allowed-signers', '--certificate', '--runtime-login', '--reader-login', '--files-target',
  '--files-container', '--files-root']);
const FLAG_OPTIONS = new Set(['--files', '--confirm-stream-stopped', '--dry-run', '--plan']);

export function parse(argv) {
  const [command, ...rest] = argv;
  const options = { command, certificates: [] };
  for (let i = 0; i < rest.length; i++) {
    const name = rest[i];
    if (FLAG_OPTIONS.has(name)) { options[camel(name)] = true; continue; }
    if (!VALUE_OPTIONS.has(name) || i + 1 >= rest.length) throw new UsageError(`unknown or incomplete option ${String(name).slice(0, 40)}`);
    const value = rest[++i];
    if (name === '--certificate') options.certificates.push(value);
    else options[camel(name)] = value;
  }
  return options;
}

function camel(option) {
  return option.slice(2).replace(/-([a-z])/g, (_, c) => c.toUpperCase());
}

// ---------------------------------------------------------------------------------------------
// plans: what runs, as argument arrays

function kubectl(o) {
  const base = ['--namespace', check('namespace', o.namespace)];
  if (o.context !== undefined) base.push('--context', check('context', o.context));
  return base;
}

function inPod(o, interactive, scriptArgs) {
  return ['kubectl', [...kubectl(o), 'exec', ...(interactive ? ['-i'] : []), check('pod', o.pod ?? DEFAULTS.pod), '-c', check('container', o.container ?? DEFAULTS.container),
    '--', 'bash', POD_SCRIPT, ...scriptArgs]];
}

function copyScript(o) {
  // dd writes stdin to the file (no shell, nothing echoed); the pod's /tmp is an emptyDir.
  return { cmd: ['kubectl', [...kubectl(o), 'exec', '-i', check('pod', o.pod ?? DEFAULTS.pod), '-c', check('container', o.container ?? DEFAULTS.container),
    '--', 'dd', `of=${POD_SCRIPT}`, 'status=none']], stdinFile: DB_SCRIPT };
}

function inFilesPod(o, interactive, tarArgs) {
  return ['kubectl', [...kubectl(o), 'exec', ...(interactive ? ['-i'] : []), check('filesTarget', o.filesTarget ?? DEFAULTS.filesTarget),
    '-c', check('container', o.filesContainer ?? DEFAULTS.filesContainer), '--', 'tar', '-C', check('filesRoot', o.filesRoot ?? DEFAULTS.filesRoot), ...tarArgs]];
}

function logins(o) {
  return [
    ...(o.runtimeLogin === undefined ? [] : ['--runtime-login', check('login', o.runtimeLogin)]),
    ...(o.readerLogin === undefined ? [] : ['--reader-login', check('login', o.readerLogin)])
  ];
}

function stamp(now) {
  return now.toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
}

// Where the expected SHA-256 of a restore's input comes from: the signed manifest, or the operator's own log.
function secureStep(o, label) {
  const file = localPath(o.in, '--in');
  const signed = o.manifest !== undefined || o.allowedSigners !== undefined || o.signature !== undefined;
  if (signed === (o.sha256 !== undefined))
    throw new UsageError('give --manifest and --allowed-signers (the backup\'s signed manifest), or --sha256 from your own operations log; exactly one of the two');
  if (!signed) return { kind: 'secure', file, label, sha256: check('sha256', String(o.sha256).toLowerCase()) };
  const manifest = localPath(o.manifest, '--manifest');
  return {
    kind: 'secure', file, label, manifest,
    signature: o.signature === undefined ? `${manifest}.sig` : localPath(o.signature, '--signature'),
    allowedSigners: localPath(o.allowedSigners, '--allowed-signers')
  };
}

export function plan(o, now = new Date()) {
  switch (o.command) {
    case 'backup': {
      const out = localPath(o.out, '--out');
      const recipients = localPath(o.recipientFile, '--recipient-file');
      const signingKey = localPath(o.signingKey, '--signing-key');
      const name = `ariva-${check('namespace', o.namespace)}-${stamp(now)}`;
      const manifest = path.join(out, `${name}.manifest.json`);
      const steps = [
        { kind: 'run', ...copyScript(o) },
        { kind: 'capture', cmd: inPod(o, false, ['manifest']), key: 'manifest' },
        { kind: 'encrypt', from: inPod(o, false, ['backup', '--output', '-']), recipients, to: path.join(out, `${name}.dump.age`), label: 'database' }
      ];
      if (o.files)
        steps.push({ kind: 'encrypt', from: inFilesPod(o, false, ['-cf', '-', '.']), recipients, to: path.join(out, `${name}.files.tar.age`), label: 'files' });
      steps.push({ kind: 'manifest', to: manifest }, { kind: 'sign', file: manifest, key: signingKey });
      return { out, recipients, signingKey, steps };
    }
    case 'restore': {
      const identity = localPath(o.identity, '--identity');
      return {
        steps: [
          secureStep(o, 'database'),
          { kind: 'run', ...copyScript(o) },
          { kind: 'decrypt', identity, file: VERIFIED_COPY, into: inPod(o, true, ['check-dump', '--input', '-']), label: 'check' },
          { kind: 'decrypt', identity, file: VERIFIED_COPY, into: inPod(o, true, ['restore', '--input', '-', '--dump-checked', ...logins(o)]), label: 'restore' }
        ]
      };
    }
    case 'files-restore': {
      const identity = localPath(o.identity, '--identity');
      return {
        steps: [
          secureStep(o, 'files'),
          { kind: 'decrypt', identity, file: VERIFIED_COPY, into: inFilesPod(o, true, ['-xf', '-', '--no-same-owner', '--no-same-permissions', '--no-overwrite-dir']), label: 'files' }
        ]
      };
    }
    case 'keyring': {
      if (o.certificates.length === 0) throw new UsageError('give --certificate with the Data Protection certificate (tls.crt), once per certificate');
      const thumbprints = o.certificates.map(file => certificateSha256(localPath(file, '--certificate')));
      return { steps: [{ kind: 'run', ...copyScript(o) }, { kind: 'run', cmd: inPod(o, false, ['keyring', ...thumbprints.flatMap(t => ['--certificate-sha256', t])]) }] };
    }
    case 'check':
      return { steps: [{ kind: 'run', ...copyScript(o) }, { kind: 'run', cmd: inPod(o, false, ['check', ...logins(o)]) }] };
    case 'after-kafka-loss': {
      if (!o.confirmStreamStopped) throw new UsageError('scale api-stream to 0 first, then pass --confirm-stream-stopped');
      return { steps: [{ kind: 'run', ...copyScript(o) }, { kind: 'run', cmd: inPod(o, false, ['after-kafka-loss', '--confirm-stream-stopped', ...(o.dryRun ? ['--dry-run'] : [])]) }] };
    }
    default:
      throw new UsageError('unknown command; use backup, restore, files-restore, keyring, check, after-kafka-loss or --self-test');
  }
}

// The SHA-256 of a certificate (DER), from its PEM. A file holding a private key is refused: the key is never needed here.
export function certificateSha256(file) {
  const text = fs.readFileSync(file, 'utf8');
  if (/PRIVATE KEY/.test(text)) throw new UsageError(`${file} holds a private key; give the certificate (tls.crt) only`);
  return new crypto.X509Certificate(text).fingerprint256.replace(/:/g, '').toUpperCase();
}

export function sha256File(file) {
  const hash = crypto.createHash('sha256');
  const fd = fs.openSync(file, 'r');
  try {
    const buffer = Buffer.alloc(1 << 20);
    let read;
    while ((read = fs.readSync(fd, buffer, 0, buffer.length, null)) > 0) hash.update(buffer.subarray(0, read));
  } finally {
    fs.closeSync(fd);
  }
  return hash.digest('hex');
}

// ---------------------------------------------------------------------------------------------
// the signed manifest (origin) and the private copy (no swap between the check and the restore)

function privateFolder() {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-ops-'));
  fs.chmodSync(folder, 0o700);
  TEMP_FOLDERS.add(folder);
  return folder;
}

function removeFolder(folder) {
  fs.rmSync(folder, { recursive: true, force: true });
  TEMP_FOLDERS.delete(folder);
}

function readCapped(file, maxBytes, what) {
  const stat = fs.statSync(file, { throwIfNoEntry: false });
  if (!stat?.isFile()) throw new Refused(`${what} ${file} does not exist: refused`);
  if (stat.size > maxBytes) throw new Refused(`${what} ${file} is larger than ${maxBytes} bytes: refused`);
  return fs.readFileSync(file);
}

function sshKeygen(args, options = {}) {
  const [program, all] = tool('ssh-keygen', args);
  const result = spawnSync(program, all, { shell: false, windowsHide: true, ...options });
  if (result.error) throw new Error(`ssh-keygen could not be started (${result.error.code ?? result.error.message}); OpenSSH 8.1 or later is needed`);
  return result;
}

// The principals the allowed-signers file names (first field, comma-separated); patterns are not used as identities.
export function principals(allowedSignersText) {
  const names = new Set();
  for (const line of allowedSignersText.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (trimmed === '' || trimmed.startsWith('#')) continue;
    for (const name of trimmed.split(/\s+/)[0].split(',')) if (PATTERNS.principal.test(name)) names.add(name);
  }
  return [...names];
}

// Verifies the manifest's signature against the allowed signers, on private copies of what was read, so the bytes that
// are verified are the bytes that are used. Returns the expected SHA-256 of the input file, or refuses.
function verifiedSha256(step, folder) {
  const manifest = readCapped(step.manifest, 1 << 20, 'the manifest');
  const signature = readCapped(step.signature, 64 << 10, 'the manifest\'s signature');
  const allowed = readCapped(step.allowedSigners, 1 << 20, 'the allowed signers file');
  const signatureCopy = path.join(folder, 'manifest.sig');
  const allowedCopy = path.join(folder, 'allowed_signers');
  fs.writeFileSync(signatureCopy, signature, { mode: 0o600 });
  fs.writeFileSync(allowedCopy, allowed, { mode: 0o600 });
  const signer = principals(allowed.toString('utf8')).find(name =>
    sshKeygen(['-Y', 'verify', '-f', allowedCopy, '-I', name, '-n', SIGNATURE_NAMESPACE, '-s', signatureCopy], { input: manifest, stdio: ['pipe', 'pipe', 'pipe'] }).status === 0);
  if (!signer) throw new Refused(`the manifest's signature does not verify against ${step.allowedSigners} (namespace ${SIGNATURE_NAMESPACE}): refused`);

  let set;
  try {
    set = JSON.parse(manifest.toString('utf8'));
  } catch {
    throw new Refused('the signed manifest is not JSON: refused');
  }
  if (set?.format !== MANIFEST_FORMAT || !Array.isArray(set.files)) throw new Refused(`the signed manifest is not ${MANIFEST_FORMAT}: refused`);
  const entry = set.files.find(f => f?.file === path.basename(step.file));
  if (!entry) throw new Refused(`the signed manifest does not name ${path.basename(step.file)}: refused`);
  if (entry.label !== step.label) throw new Refused(`the signed manifest names ${entry.file} as ${entry.label}, not ${step.label}: refused`);
  if (typeof entry.sha256 !== 'string' || !PATTERNS.sha256.test(entry.sha256)) throw new Refused('the signed manifest has no valid SHA-256 for the file: refused');
  OUT.log(`manifest signature verified: signed by ${signer}`);
  return entry.sha256;
}

// ---------------------------------------------------------------------------------------------
// execution

function start([cmd, args], stdio) {
  const [program, all] = tool(cmd, args);
  const child = spawn(program, all, { shell: false, stdio, windowsHide: true });
  // A side that ends early breaks the pipe; its exit code reports the failure, so a broken pipe must not end this process.
  child.stdin?.on('error', () => {});
  child.stdout?.on('error', () => {});
  const done = new Promise((resolve, reject) => {
    child.on('error', e => reject(new Error(`${cmd} could not be started (${e.code ?? e.message}); is it installed and on PATH?`)));
    child.on('close', code => resolve(code));
  });
  return { child, done };
}

async function expectZero(label, ...codes) {
  const results = await Promise.all(codes);
  if (results.some(code => code !== 0)) throw new Error(`${label} failed (exit codes ${results.join(', ')})`);
}

async function runStep(step, state) {
  switch (step.kind) {
    case 'run': {
      const { child, done } = start(step.cmd, [step.stdinFile ? 'pipe' : 'ignore', 'inherit', 'inherit']);
      if (step.stdinFile) fs.createReadStream(step.stdinFile).pipe(child.stdin);
      await expectZero(step.cmd[1].slice(-4).join(' '), done);
      return;
    }
    case 'capture': {
      const { child, done } = start(step.cmd, ['ignore', 'pipe', 'inherit']);
      const chunks = [];
      child.stdout.on('data', c => chunks.push(c));
      await expectZero('manifest', done);
      state[step.key] = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      return;
    }
    case 'encrypt': {
      // The stream goes from the pod straight into age; only ciphertext reaches this machine's disk.
      const partial = `${step.to}.partial`;
      const source = start(step.from, ['ignore', 'pipe', 'inherit']);
      const age = start(['age', ['--encrypt', '--recipients-file', step.recipients, '--output', partial]], ['pipe', 'inherit', 'inherit']);
      source.child.stdout.pipe(age.child.stdin);
      try {
        await expectZero(`${step.label} backup`, source.done, age.done);
      } catch (e) {
        fs.rmSync(partial, { force: true });
        throw e;
      }
      fs.chmodSync(partial, 0o600);
      fs.renameSync(partial, step.to);
      const sha256 = sha256File(step.to);
      state.files.push({ label: step.label, file: path.basename(step.to), bytes: fs.statSync(step.to).size, sha256 });
      OUT.log(`${step.label}: ${step.to}\n  sha256 ${sha256}`);
      return;
    }
    case 'manifest': {
      const manifest = { format: MANIFEST_FORMAT, database: state.manifest, files: state.files };
      fs.writeFileSync(step.to, JSON.stringify(manifest, null, 2) + '\n', { mode: 0o600, flag: 'wx' });
      OUT.log(`manifest: ${step.to}`);
      return;
    }
    case 'sign': {
      const result = sshKeygen(['-Y', 'sign', '-f', step.key, '-n', SIGNATURE_NAMESPACE, step.file],
        { stdio: OUT.interactive ? 'inherit' : ['ignore', 'pipe', 'pipe'] });
      if (result.status !== 0)
        throw new Error(`the manifest could not be signed (ssh-keygen exit ${result.status}). The backup files are complete, but a restore refuses an unsigned manifest: sign it with ssh-keygen -Y sign -f <key> -n ${SIGNATURE_NAMESPACE} ${step.file}`);
      fs.chmodSync(`${step.file}.sig`, 0o600);
      OUT.log(`signature: ${step.file}.sig\nKeep the allowed-signers file outside the backup store; a restore verifies the manifest against it. The sha256 values above may also go in the operations log (the only other way to restore).`);
      return;
    }
    case 'secure': {
      // Before anything leaves this machine: the expected hash from a verified signature (or the operator's own log), then a
      // private copy whose own hash must match; only that copy is decrypted afterwards.
      state.folder ??= privateFolder();
      const expected = step.sha256 ?? verifiedSha256(step, state.folder);
      const copy = path.join(state.folder, 'verified.age');
      fs.copyFileSync(step.file, copy, fs.constants.COPYFILE_EXCL);
      fs.chmodSync(copy, 0o600);
      const actual = sha256File(copy);
      if (actual !== expected) throw new Refused(`${step.file} has SHA-256 ${actual}, not the expected ${expected}: refused`);
      state.copy = copy;
      OUT.log(`verified: a private copy of ${path.basename(step.file)} has the expected SHA-256; only the copy is decrypted`);
      return;
    }
    case 'decrypt': {
      const file = step.file === VERIFIED_COPY ? state.copy : step.file;
      if (!file) throw new Error('no verified copy to decrypt');
      const age = start(['age', ['--decrypt', '--identity', step.identity, file]], ['ignore', 'pipe', 'inherit']);
      const sink = start(step.into, ['pipe', 'inherit', 'inherit']);
      age.child.stdout.pipe(sink.child.stdin);
      await expectZero(step.label, age.done, sink.done);
      return;
    }
    default:
      throw new Error(`unknown step ${step.kind}`);
  }
}

function prepare(o, p) {
  if (o.command === 'backup') {
    if (!fs.statSync(p.out, { throwIfNoEntry: false })?.isDirectory()) throw new UsageError('--out must be an existing folder');
    if (!fs.statSync(p.signingKey, { throwIfNoEntry: false })?.isFile()) throw new UsageError('--signing-key must name the operator\'s SSH signing key (or its public half, with the key in ssh-agent)');
    const recipients = fs.readFileSync(p.recipients, 'utf8');
    if (/AGE-SECRET-KEY-/.test(recipients)) throw new UsageError('--recipient-file holds an age identity (a private key); give the public recipients only');
    if (!/^age1[0-9a-z]{58}\s*$/m.test(recipients)) throw new UsageError('--recipient-file has no age recipient (age1...)');
  }
  for (const step of p.steps) {
    for (const file of [step.kind === 'secure' ? step.file : undefined, step.identity].filter(Boolean))
      if (!fs.statSync(file, { throwIfNoEntry: false })?.isFile()) throw new UsageError(`${file} does not exist`);
  }
}

export async function main(argv) {
  if (argv[0] === '--self-test') return selfTest();
  const o = parse(argv);
  const p = plan(o);
  if (o.plan) {
    for (const step of p.steps) OUT.log(JSON.stringify(step));
    return 0;
  }
  prepare(o, p);
  const state = { files: [] };
  try {
    for (const step of p.steps) await runStep(step, state);
  } finally {
    if (state.folder) removeFolder(state.folder);
  }
  return 0;
}

// ---------------------------------------------------------------------------------------------
// self-test (run by node scripts/verify.mjs security): validation, the planned argument arrays, and the signed path end to
// end with stand-ins for kubectl and age (no cluster, nothing decrypted for real) and the real ssh-keygen.

const FAKE_KUBECTL = `import fs from 'node:fs';
import crypto from 'node:crypto';
const args = process.argv.slice(2);
const remote = args.slice(args.indexOf('--') + 1);
const record = entry => fs.appendFileSync(process.env.ARIVA_OPS_FAKE_LOG, JSON.stringify(entry) + '\\n');
const readAll = async () => { const chunks = []; for await (const chunk of process.stdin) chunks.push(chunk); return Buffer.concat(chunks); };
record({ tool: 'kubectl', remote });
if (remote[0] === 'dd') {
  await readAll();
  if (process.env.ARIVA_OPS_FAKE_SWAP) fs.writeFileSync(process.env.ARIVA_OPS_FAKE_SWAP, 'a file put in place of the verified one');
} else if (remote[0] === 'bash' && remote[2] === 'manifest') {
  process.stdout.write(JSON.stringify({ format: 'ariva-backup-manifest/1', takenUtc: '2026-10-15T03:04:05Z' }));
} else if (remote[0] === 'bash' && remote[2] === 'backup') {
  process.stdout.write('pg_dump custom archive');
} else if (remote[0] === 'tar' && remote.includes('-cf')) {
  process.stdout.write('tar archive');
} else {
  const data = await readAll();
  record({ received: remote[0] === 'tar' ? 'tar' : remote[2], sha256: crypto.createHash('sha256').update(data).digest('hex') });
}
`;

const FAKE_AGE = `import fs from 'node:fs';
const args = process.argv.slice(2);
const header = Buffer.from('age stand-in of the ariva-ops self-test\\n');
if (args[0] === '--encrypt') {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  fs.writeFileSync(args[args.indexOf('--output') + 1], Buffer.concat([header, ...chunks]));
} else {
  const file = args[args.length - 1];
  fs.appendFileSync(process.env.ARIVA_OPS_FAKE_LOG, JSON.stringify({ tool: 'age', file }) + '\\n');
  const data = fs.readFileSync(file);
  if (!data.subarray(0, header.length).equals(header)) { process.stderr.write('not an age file\\n'); process.exit(1); }
  process.stdout.write(data.subarray(header.length));
}
`;

async function selfTest() {
  let checks = 0;
  const ok = (value, message) => { assert.ok(value, message); checks++; };
  const refuses = (fn, message) => { assert.throws(fn, UsageError, message); checks++; };

  for (const [kind, good, bad] of [
    ['namespace', ['ariva-k8s-prd', 'a', 'ariva1'], ['Ariva', '-ariva', 'ariva-', 'a;b', 'a b', '$(id)', 'a'.repeat(64), '']],
    ['pod', ['timescaledb-0'], ['timescaledb_0', '../x', '--help']],
    ['filesTarget', ['deploy/api-main-deployment', 'pod/api-main-0', 'api-main-0'], ['deploy/../x', 'svc/x', 'deploy/', '`id`']],
    ['context', ['dev', 'arn:aws:eks:me-central-1:123:cluster/ariva', 'user@cluster'], ['-x', 'a b', 'a;b', '']],
    ['login', ['ariva_app'], ['Ariva', 'a-b', "x';--"]],
    ['filesRoot', ['/app/data/files'], ['app/data', '/app/../etc', '/app data', '/']],
    ['sha256', ['a'.repeat(64)], ['A'.repeat(64), 'a'.repeat(63), 'g'.repeat(64)]]
  ]) {
    for (const value of good) ok(check(kind, value) === value, `${kind} accepts ${value}`);
    for (const value of bad) refuses(() => check(kind, value), `${kind} refuses ${value}`);
  }
  assert.deepEqual(principals('ops-a,ops-b namespaces="ariva-backup" ssh-ed25519 AAAA\n# note\n*.example ssh-ed25519 BBBB\n'), ['ops-a', 'ops-b']); checks++;
  refuses(() => localPath('a\nb', 'x'), 'a path with a control character');
  refuses(() => parse(['backup', '--namespace']), 'an option without its value');
  refuses(() => parse(['backup', '--shell', 'x']), 'an unknown option');
  refuses(() => plan(parse(['frobnicate'])), 'an unknown command');
  refuses(() => plan(parse(['after-kafka-loss', '--namespace', 'ariva'])), 'the Kafka recovery without the stream stopped');
  refuses(() => plan(parse(['restore', '--namespace', 'ariva', '--in', 'x', '--identity', 'y', '--sha256', 'nothex'])), 'a malformed SHA-256');
  refuses(() => plan(parse(['restore', '--namespace', 'ariva', '--in', 'x', '--identity', 'y'])), 'a restore with neither a signed manifest nor a SHA-256');
  refuses(() => plan(parse(['restore', '--namespace', 'ariva', '--in', 'x', '--identity', 'y', '--sha256', 'a'.repeat(64), '--manifest', 'm', '--allowed-signers', 's'])),
    'a restore with both');
  refuses(() => plan(parse(['restore', '--namespace', 'ariva', '--in', 'x', '--identity', 'y', '--manifest', 'm'])), 'a manifest without the allowed signers');
  refuses(() => plan(parse(['backup', '--namespace', 'ariva', '--out', '.', '--recipient-file', 'r'])), 'a backup without a signing key');

  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'ariva-ops-test-'));
  const saved = { TMPDIR: process.env.TMPDIR, TEMP: process.env.TEMP, TMP: process.env.TMP, log: process.env.ARIVA_OPS_FAKE_LOG, out: OUT.log, interactive: OUT.interactive, tools: { ...TOOLS } };
  try {
    // Plans: what the commands are, as argument arrays.
    const recipients = path.join(temp, 'recipients.txt');
    fs.writeFileSync(recipients, 'age1' + 'q'.repeat(58) + '\n');
    const at = new Date('2026-10-15T03:04:05.678Z');
    const backupPlan = plan(parse(['backup', '--namespace', 'ariva-k8s-prd', '--context', 'prd', '--out', temp, '--recipient-file', recipients,
      '--signing-key', path.join(temp, 'k'), '--files']), at);
    const encrypts = backupPlan.steps.filter(s => s.kind === 'encrypt');
    ok(encrypts.length === 2, 'the database and the files are encrypted');
    ok(encrypts[0].to === path.join(temp, 'ariva-ariva-k8s-prd-20261015T030405Z.dump.age'), 'the backup file name');
    assert.deepEqual(encrypts[0].from, ['kubectl', ['--namespace', 'ariva-k8s-prd', '--context', 'prd', 'exec', 'timescaledb-0', '-c', 'timescaledb', '--',
      'bash', POD_SCRIPT, 'backup', '--output', '-']]); checks++;
    assert.deepEqual(encrypts[1].from, ['kubectl', ['--namespace', 'ariva-k8s-prd', '--context', 'prd', 'exec', 'deploy/api-main-deployment', '-c', 'api-main',
      '--', 'tar', '-C', '/app/data/files', '-cf', '-', '.']]); checks++;
    assert.deepEqual(backupPlan.steps.slice(-2).map(s => s.kind), ['manifest', 'sign']); checks++;
    ok(backupPlan.steps[0].stdinFile === DB_SCRIPT && backupPlan.steps[0].cmd[1].includes(`of=${POD_SCRIPT}`), 'the script reaches the pod on stdin, no shell');
    fs.writeFileSync(path.join(temp, 'identity.txt'), 'AGE-SECRET-KEY-1' + 'Q'.repeat(58) + '\n');
    fs.writeFileSync(path.join(temp, 'k'), 'key');
    refuses(() => prepare(parse(['backup']), { out: temp, recipients: path.join(temp, 'identity.txt'), signingKey: path.join(temp, 'k'), steps: [] }),
      'an identity given as recipients');
    const restorePlan = plan(parse(['restore', '--namespace', 'ariva', '--in', 'x.dump.age', '--identity', 'id', '--manifest', 'm.json', '--allowed-signers', 'allowed',
      '--runtime-login', 'ariva_app']));
    assert.deepEqual(restorePlan.steps.map(s => s.kind), ['secure', 'run', 'decrypt', 'decrypt']); checks++;
    ok(restorePlan.steps[0].signature === path.resolve('m.json.sig'), 'the signature is read beside the manifest');
    ok(restorePlan.steps.filter(s => s.kind === 'decrypt').every(s => s.file === VERIFIED_COPY), 'both passes decrypt the verified copy only');
    assert.deepEqual(restorePlan.steps[3].into[1].slice(-7), [POD_SCRIPT, 'restore', '--input', '-', '--dump-checked', '--runtime-login', 'ariva_app']); checks++;
    for (const p of [backupPlan, restorePlan]) {
      for (const step of p.steps) {
        for (const cmd of [step.cmd, step.from, step.into].filter(Boolean)) {
          ok(cmd[0] === 'kubectl', 'only kubectl is planned (age and ssh-keygen are started with their own fixed arguments)');
          ok(cmd[1].every(a => typeof a === 'string' && !/[\r\n]/.test(a)), 'arguments are single-line strings');
          const remote = cmd[1].slice(cmd[1].indexOf('--') + 1);
          ok((remote[0] === 'bash' && remote[1] === POD_SCRIPT) || (remote[0] === 'dd' && remote[1] === `of=${POD_SCRIPT}`) || remote[0] === 'tar',
            'the pod runs the script file, dd or tar, never a command string');
        }
      }
    }

    const certificate = path.join(temp, 'tls.crt');
    fs.writeFileSync(certificate, SELF_TEST_CERTIFICATE);
    const thumbprint = certificateSha256(certificate);
    ok(thumbprint === '5F5CA77443021ECA47897BC9395F6F063393DA08729B6A62B8B5B3C2E8521B75', 'the certificate SHA-256 as openssl x509 -fingerprint -sha256 prints it');
    const keyring = plan(parse(['keyring', '--namespace', 'ariva', '--certificate', certificate]));
    assert.deepEqual(keyring.steps[1].cmd[1].slice(-3), ['keyring', '--certificate-sha256', thumbprint]); checks++;
    fs.writeFileSync(path.join(temp, 'tls.key'), '-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n');
    refuses(() => certificateSha256(path.join(temp, 'tls.key')), 'a private key given as the certificate');

    // The signed path end to end: the stand-ins record every call, and only private folders under this test's own temporary
    // root are created (it must be empty after every run, a refused one included).
    const tmpRoot = path.join(temp, 'tmp');
    fs.mkdirSync(tmpRoot);
    process.env.TMPDIR = process.env.TEMP = process.env.TMP = tmpRoot;
    const log = path.join(temp, 'calls.jsonl');
    process.env.ARIVA_OPS_FAKE_LOG = log;
    fs.writeFileSync(path.join(temp, 'fake-kubectl.mjs'), FAKE_KUBECTL);
    fs.writeFileSync(path.join(temp, 'fake-age.mjs'), FAKE_AGE);
    Object.assign(TOOLS, { kubectl: [process.execPath, path.join(temp, 'fake-kubectl.mjs')], age: [process.execPath, path.join(temp, 'fake-age.mjs')] });
    OUT.log = () => {};
    OUT.interactive = false;
    const calls = () => (fs.existsSync(log) ? fs.readFileSync(log, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l)) : []);
    const clear = () => fs.rmSync(log, { force: true });
    const sha = text => crypto.createHash('sha256').update(text).digest('hex');

    const keygen = args => {
      const r = sshKeygen(args, { stdio: ['ignore', 'pipe', 'pipe'] });
      assert.equal(r.status, 0, `ssh-keygen ${args[0]} ${args[1]}: ${r.stderr}`);
    };
    for (const name of ['a', 'b']) keygen(['-q', '-t', 'ed25519', '-N', '', '-C', `ops-${name}`, '-f', path.join(temp, `key-${name}`)]);
    const publicKey = name => fs.readFileSync(path.join(temp, `key-${name}.pub`), 'utf8').trim().split(/\s+/).slice(0, 2).join(' ');
    const allowed = path.join(temp, 'allowed_signers');
    fs.writeFileSync(allowed, `ops-a namespaces="${SIGNATURE_NAMESPACE}" ${publicKey('a')}\n`);
    const otherNamespace = path.join(temp, 'allowed_signers_other');
    fs.writeFileSync(otherNamespace, `ops-a namespaces="something-else" ${publicKey('a')}\n`);
    const identity = path.join(temp, 'identity.txt');
    const out = path.join(temp, 'backups');
    fs.mkdirSync(out);

    await main(['backup', '--namespace', 'ariva', '--out', out, '--recipient-file', recipients, '--signing-key', path.join(temp, 'key-a'), '--files']);
    const produced = name => path.join(out, fs.readdirSync(out).find(f => f.endsWith(name)));
    const dump = produced('.dump.age');
    const tarFile = produced('.files.tar.age');
    const manifest = produced('.manifest.json');
    ok(fs.existsSync(`${manifest}.sig`), 'the backup signs its manifest');
    ok(fs.readdirSync(tmpRoot).length === 0, 'a backup leaves no private folder behind');
    const restore = extra => ['restore', '--namespace', 'ariva', '--in', dump, '--identity', identity, ...extra];
    const signed = (m = manifest, a = allowed) => ['--manifest', m, '--allowed-signers', a];

    clear();
    await main(restore(signed()));
    const restored = calls();
    const ages = restored.filter(c => c.tool === 'age');
    ok(ages.length === 2 && ages.every(c => c.file !== dump && c.file.startsWith(tmpRoot)), 'both passes decrypt a private copy, never the file given');
    ok(restored.filter(c => c.received === 'check-dump' || c.received === 'restore').every(c => c.sha256 === sha('pg_dump custom archive'))
      && restored.some(c => c.received === 'restore'), 'the pod receives the backed-up dump in both passes');
    ok(fs.readdirSync(tmpRoot).length === 0, 'the private folder is removed after a restore');
    clear();
    await main(['files-restore', '--namespace', 'ariva', '--in', tarFile, '--identity', identity, ...signed()]);
    ok(calls().some(c => c.received === 'tar' && c.sha256 === sha('tar archive')), 'the files restore receives the backed-up tar');
    clear();
    await main(restore(['--sha256', sha256File(dump)]));
    ok(calls().some(c => c.received === 'restore'), 'a SHA-256 from the operator\'s own log is the one alternative');

    // Refusals: each before any kubectl step, each leaving no private folder.
    const refused = async (argv, pattern, what) => {
      clear();
      await assert.rejects(main(argv), pattern, what);
      ok(!calls().some(c => c.tool === 'kubectl'), `${what}: refused before any kubectl step`);
      ok(fs.readdirSync(tmpRoot).length === 0, `${what}: no private folder left behind`);
    };
    const elsewhere = (name, files) => {
      const folder = path.join(temp, name);
      fs.mkdirSync(folder);
      for (const [file, content] of Object.entries(files)) fs.writeFileSync(path.join(folder, file), content);
      return folder;
    };
    const tampered = elsewhere('tampered-dump', { [path.basename(dump)]: 'a forged dump' });
    await refused(['restore', '--namespace', 'ariva', '--in', path.join(tampered, path.basename(dump)), '--identity', identity, ...signed()],
      /SHA-256 .* not the expected/, 'a file whose SHA-256 differs from the signed manifest');
    await refused(restore(['--sha256', 'f'.repeat(64)]), /SHA-256 .* not the expected/, 'a file whose SHA-256 differs from the one given');
    const text = fs.readFileSync(manifest, 'utf8');
    const altered = elsewhere('altered-manifest', { 'm.json': text.replace(/"sha256": "[0-9a-f]{64}"/, `"sha256": "${sha('a forged dump')}"`),
      'm.json.sig': fs.readFileSync(`${manifest}.sig`) });
    await refused(restore(signed(path.join(altered, 'm.json'))), /does not verify/, 'a manifest altered after it was signed');
    const unsigned = elsewhere('unsigned-manifest', { 'm.json': text });
    await refused(restore(signed(path.join(unsigned, 'm.json'))), /signature .* does not exist/, 'a manifest without its signature');
    const foreign = elsewhere('foreign-signer', { 'm.json': text });
    keygen(['-Y', 'sign', '-f', path.join(temp, 'key-b'), '-n', SIGNATURE_NAMESPACE, path.join(foreign, 'm.json')]);
    await refused(restore(signed(path.join(foreign, 'm.json'))), /does not verify/, 'a manifest signed by a key not in the allowed signers');
    await refused(restore(signed(manifest, otherNamespace)), /does not verify/, 'an allowed signer limited to another namespace');
    await refused(['restore', '--namespace', 'ariva', '--in', tarFile, '--identity', identity, ...signed()], /as files, not database/,
      'the files archive given to the database restore');

    // A file swapped after the check: the restore goes on with the verified copy, never the new file.
    clear();
    process.env.ARIVA_OPS_FAKE_SWAP = dump;
    try {
      await main(restore(signed()));
    } finally {
      delete process.env.ARIVA_OPS_FAKE_SWAP;
    }
    ok(fs.readFileSync(dump, 'utf8') === 'a file put in place of the verified one', 'the stand-in replaced the file after the check');
    ok(calls().filter(c => c.received === 'check-dump' || c.received === 'restore').every(c => c.sha256 === sha('pg_dump custom archive')),
      'both passes restore the verified copy, not the file put in its place');
  } finally {
    Object.assign(TOOLS, saved.tools);
    OUT.log = saved.out;
    OUT.interactive = saved.interactive;
    for (const name of ['TMPDIR', 'TEMP', 'TMP']) {
      if (saved[name] === undefined) delete process.env[name];
      else process.env[name] = saved[name];
    }
    if (saved.log === undefined) delete process.env.ARIVA_OPS_FAKE_LOG;
    else process.env.ARIVA_OPS_FAKE_LOG = saved.log;
    fs.rmSync(temp, { recursive: true, force: true });
  }

  console.log(`ariva-ops self-test: ${checks} checks passed`);
  return 0;
}

// A throwaway self-signed certificate for the fingerprint check (public material only).
const SELF_TEST_CERTIFICATE = `-----BEGIN CERTIFICATE-----
MIIBkjCCATegAwIBAgIUAqabJX7T3KWoqP1Y85svXozNJ1UwCgYIKoZIzj0EAwIw
HjEcMBoGA1UEAwwTYXJpdmEtb3BzIHNlbGYtdGVzdDAeFw0yNjEwMTUwNDExNDha
Fw0zNjEwMTIwNDExNDhaMB4xHDAaBgNVBAMME2FyaXZhLW9wcyBzZWxmLXRlc3Qw
WTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAAQoPKnYtIfgJYdFtU9iDilNr4+I69vw
V/oEAAQ5u15ypQ7HZrsfLv/QuzA/Fl600xrdw9IjuIcww1XUvVAaukgUo1MwUTAd
BgNVHQ4EFgQU2ffvmDxAm6y+8PiG3MKMm9pKXAowHwYDVR0jBBgwFoAU2ffvmDxA
m6y+8PiG3MKMm9pKXAowDwYDVR0TAQH/BAUwAwEB/zAKBggqhkjOPQQDAgNJADBG
AiEAqiQoboUswbJn9+WcHftB7YyOfRajGxoeje1HKqSbi9kCIQCNmYzXd0Du8Sl4
sjzqIGAuGXMSvXCsg3RFkqiQ0RHuQA==
-----END CERTIFICATE-----
`;

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  // A stop by signal still removes the private folders (they hold ciphertext only, but nothing is left behind).
  process.on('exit', () => { for (const folder of [...TEMP_FOLDERS]) removeFolder(folder); });
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => process.exit(130));
  main(process.argv.slice(2)).then(code => process.exit(code), error => {
    console.error(`ariva-ops: ${error.message}`);
    process.exit(error instanceof UsageError ? 2 : 1);
  });
}
