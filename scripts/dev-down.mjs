// Stops the local dependencies started by scripts/dev-up.mjs (ARV-003).
// Usage: node scripts/dev-down.mjs [--volumes]   (--volumes also deletes the database, Kafka and Redis data)
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = ['compose', '-f', 'docker-compose.dev.yml', '--env-file', '.env', 'down'];
if (process.argv.includes('--volumes')) args.push('--volumes');
const result = spawnSync('docker', args, { cwd: ROOT, stdio: 'inherit', shell: false });
process.exit(result.status ?? 1);
