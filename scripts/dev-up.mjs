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

if (!fs.existsSync(ENV)) {
	const template = fs.readFileSync(path.join(ROOT, '.env.example'), 'utf8');
	// URL-safe random passwords: 24 bytes, base64url, no characters that need quoting in YAML, shells or URIs.
	const filled = template.replace(/^([A-Z0-9_]+)=generate$/gm, (_, key) => `${key}=${randomBytes(24).toString('base64url')}`);
	fs.writeFileSync(ENV, filled, { mode: 0o600 });
	console.log('dev-up: created .env with new random passwords (git-ignored).');
}

const env = readEnv(ENV);
const settings = {
	Database: { Host: 'localhost', Port: Number(env.ARIVA_DB_PORT || 5433), Username: 'ariva', Password: env.ARIVA_DB_PASSWORD },
	Kafka: { BootstrapServers: `localhost:${env.ARIVA_KAFKA_PORT || 19092}` },
	Redis: { ConnectionString: `localhost:${env.ARIVA_REDIS_PORT || 16379},password=${env.ARIVA_REDIS_PASSWORD}` },
	Smtp: { Host: 'localhost', Port: Number(env.ARIVA_SMTP_PORT || 2525), UseTls: false }
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
