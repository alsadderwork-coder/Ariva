// Proves the local dependencies work as the stories expect (ARV-003). Run after scripts/dev-up.mjs:
//   node scripts/dev-check.mjs
// Checks: TimescaleDB extension available; the read-only role can read but not write; Kafka answers on the internal
// listener; Redis refuses unauthenticated commands and accepts the password; smtp4dev listens on both ports.
// Commands run with argument arrays and no shell (CWE-78); passwords come from .env.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const env = Object.fromEntries(
	fs.readFileSync(path.join(ROOT, '.env'), 'utf8').split(/\r?\n/).map((line) => line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/)).filter(Boolean).map((m) => [m[1], m[2]])
);
const compose = ['compose', '-f', 'docker-compose.dev.yml', '--env-file', '.env'];
let failed = false;

function composeExec(service, command, vars = {}) {
	const envArgs = Object.entries(vars).flatMap(([key, value]) => ['-e', `${key}=${value}`]);
	return spawnSync('docker', [...compose, 'exec', '-T', ...envArgs, service, ...command], { cwd: ROOT, encoding: 'utf8', shell: false });
}

function expect(label, ok, detail = '') {
	console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}${ok || !detail ? '' : `\n      ${String(detail).trim().split('\n').slice(-3).join('\n      ')}`}`);
	if (!ok) failed = true;
}

const sql = (user, password, statement) =>
	composeExec('timescaledb', ['psql', '-h', '127.0.0.1', '-U', user, '-d', 'ariva', '-v', 'ON_ERROR_STOP=1', '-tAc', statement], { PGPASSWORD: password });
const asOwner = (statement) => sql('ariva', env.ARIVA_DB_PASSWORD, statement);
const asReadonly = (statement) => sql('ariva_readonly', env.ARIVA_DB_READONLY_PASSWORD, statement);

let r = asOwner('CREATE EXTENSION IF NOT EXISTS timescaledb');
r = asOwner("SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'");
expect(`TimescaleDB extension available (${(r.stdout || '').trim()})`, r.status === 0 && /\d+\.\d+/.test(r.stdout), r.stderr);
r = asOwner('CREATE TABLE IF NOT EXISTS dev_check (id int)');
r = asOwner('INSERT INTO dev_check VALUES (1)');
expect('owner role can create and write', r.status === 0, r.stderr);
r = asReadonly('SELECT count(*) FROM dev_check');
expect('read-only role can read', r.status === 0 && r.stdout.trim() !== '', r.stderr);
r = asReadonly('INSERT INTO dev_check VALUES (2)');
expect('read-only role cannot write', r.status !== 0 && /read-only|permission denied/i.test(r.stderr), r.stderr || r.stdout);
r = asReadonly('CREATE TABLE dev_check_2 (id int)');
expect('read-only role cannot create tables', r.status !== 0, r.stdout);
asOwner('DROP TABLE IF EXISTS dev_check');

r = composeExec('kafka', ['/opt/kafka/bin/kafka-topics.sh', '--bootstrap-server', 'kafka:29092', '--list']);
expect('Kafka answers (KRaft, internal listener)', r.status === 0, r.stderr);

r = composeExec('redis', ['redis-cli', 'ping'], { REDISCLI_AUTH: '' });
expect('Redis refuses unauthenticated commands', /NOAUTH/i.test(r.stdout + r.stderr), r.stdout);
r = composeExec('redis', ['redis-cli', 'ping']);
expect('Redis accepts the password', r.stdout.trim() === 'PONG', r.stderr);

const listens = (port) =>
	new Promise((resolve) => {
		const socket = net.connect({ host: '127.0.0.1', port: Number(port) }, () => { socket.end(); resolve(true); });
		socket.on('error', () => resolve(false));
		socket.setTimeout(5000, () => { socket.destroy(); resolve(false); });
	});
expect('smtp4dev SMTP port answers', await listens(env.ARIVA_SMTP_PORT || 2525));
expect('smtp4dev web UI port answers', await listens(env.ARIVA_SMTP_UI_PORT || 5080));

process.exit(failed ? 1 : 0);
