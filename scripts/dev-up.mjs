// Starts the local dependencies (ARV-003), cross-platform:
//   1. creates .env from .env.example on first run, replacing every "generate" value with a random password;
//   2. writes Platform/Backplane/Ariva.Api.Common/appsettings.local.json so the hosts in vm-local reach them
//      (git-ignored, docker-ignored, copied only when it exists);
//   3. runs docker compose up --wait, which returns once every health check passes.
// Usage: node scripts/dev-up.mjs [--no-wait]
import { randomBytes } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ENV = path.join(ROOT, '.env');
const LOCAL_SETTINGS = path.join(ROOT, 'Platform', 'Backplane', 'Ariva.Api.Common', 'appsettings.local.json');

function readEnv(file) {
	const values = {};
	for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
		const m = line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/);
		if (m) values[m[1]] = m[2];
	}
	return values;
}

// URL-safe random passwords: 24 bytes, base64url, no characters that need quoting in YAML, shells or URIs.
const newPassword = () => randomBytes(24).toString('base64url');
const template = fs.readFileSync(path.join(ROOT, '.env.example'), 'utf8');
if (!fs.existsSync(ENV)) {
	const filled = template.replace(/^([A-Z0-9_]+)=generate$/gm, (_, key) => `${key}=${newPassword()}`);
	fs.writeFileSync(ENV, filled, { mode: 0o600 });
	console.log('dev-up: created .env with new random passwords (git-ignored).');
} else {
	// Keys added to .env.example after .env was created (for example ARIVA_DB_APP_PASSWORD in ARV-006).
	const existing = readEnv(ENV);
	const missing = [...template.matchAll(/^([A-Z0-9_]+)=(.*)$/gm)].filter((m) => !(m[1] in existing));
	if (missing.length > 0) {
		const lines = missing.map((m) => `${m[1]}=${m[2] === 'generate' ? newPassword() : m[2]}`);
		fs.appendFileSync(ENV, `\n${lines.join('\n')}\n`);
		console.log(`dev-up: added ${missing.map((m) => m[1]).join(', ')} to .env.`);
	}
}

// Values this script draws itself when .env lacks them (added after .env.example's keys): the validation reader login
// (ARV-104g1), which only the hosts use, never compose.
const drawn = { ARIVA_DB_VALIDATION_READER_PASSWORD: newPassword };
const before = readEnv(ENV);
const missingDrawn = Object.keys(drawn).filter((key) => !before[key]);
if (missingDrawn.length > 0) {
	fs.appendFileSync(ENV, `\n${missingDrawn.map((key) => `${key}=${drawn[key]()}`).join('\n')}\n`);
	console.log(`dev-up: added ${missingDrawn.join(', ')} to .env.`);
}

const env = readEnv(ENV);
const settings = {
	// Hosts connect as the DML-only runtime login; the owner login is used for migrations only (ARV-006). In vm-local
	// the hosts migrate at startup and create the runtime login with this password.
	Database: {
		Host: 'localhost',
		Port: Number(env.ARIVA_DB_PORT || 5433),
		Username: 'ariva_app',
		Password: env.ARIVA_DB_APP_PASSWORD,
		Migration: { Username: 'ariva', Password: env.ARIVA_DB_PASSWORD },
		// ARV-104g1: the validation reader login, the only login that reads the shadow nowcast; the first host to migrate
		// creates it. On a developer machine every host loads this file (as it loads the migration login, since vm-local hosts
		// migrate themselves); in the clusters only api-main and the migration job get it.
		ValidationReader: { Username: 'ariva_validation', Password: env.ARIVA_DB_VALIDATION_READER_PASSWORD }
	},
	Kafka: { Enabled: true, BootstrapServers: `localhost:${env.ARIVA_KAFKA_PORT || 19092}`, Topics: { ReplicationFactor: 1, MinInSyncReplicas: 1 } },
	Redis: { Enabled: true, ConnectionString: `localhost:${env.ARIVA_REDIS_PORT || 16379},password=${env.ARIVA_REDIS_PASSWORD}` },
	// Alert emails (ARV-040) go to the compose smtp4dev; its web UI is on ARIVA_SMTP_UI_PORT (default 5080).
	Email: {
		Enabled: true,
		FromAddress: 'no-reply@ariva.local',
		Smtp: { Host: 'localhost', Port: Number(env.ARIVA_SMTP_PORT || 2525), Security: 'None', AllowInsecure: true }
	}
};
fs.writeFileSync(LOCAL_SETTINGS, JSON.stringify(settings, null, 2) + '\n', { mode: 0o600 });
console.log(`dev-up: wrote ${path.relative(ROOT, LOCAL_SETTINGS)} (git-ignored).`);
console.log(`dev-up: read-only database URI for the postgres-dev MCP: postgresql://ariva_readonly:<ARIVA_DB_READONLY_PASSWORD>@localhost:${env.ARIVA_DB_PORT || 5433}/ariva`);

const args = ['compose', '-f', 'docker-compose.dev.yml', '--env-file', '.env', 'up', '-d'];
if (!process.argv.includes('--no-wait')) args.push('--wait', '--wait-timeout', '300');
const result = spawnSync('docker', args, { cwd: ROOT, stdio: 'inherit', shell: false });
if (result.error) {
	console.error(`dev-up: could not run docker (${result.error.message}). Install Docker Desktop or Rancher Desktop.`);
	process.exit(1);
}
process.exit(result.status ?? 1);
