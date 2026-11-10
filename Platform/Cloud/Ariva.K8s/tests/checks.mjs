// Pure checks over rendered Kubernetes manifests; chart-security.mjs feeds them helm template output.
// Each finding names the object and the rule, so a failing render points straight at the template to fix.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** ARV-125b: what the SigNoz rules and dashboards may read (shared with MonitoringRulesTests in Ariva.UnitTests). */
export const MONITORING = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), 'monitoring.json'), 'utf8'));
const MONITORING_LABELS = new Set(Object.values(MONITORING.labels).flat());
const MONITORING_FORBIDDEN = new RegExp(MONITORING.forbidden, 'i');
const MONITORING_EXCEPTIONS = new Set(MONITORING.forbiddenExceptions);
const MONITORING_CONFIG_MAPS = new Set(['ariva-monitoring-alerts', 'ariva-monitoring-dashboards']);

const WORKLOADS = new Set(['Deployment', 'StatefulSet', 'DaemonSet', 'Job', 'CronJob']);

function podSpecOf(doc) {
	if (doc.kind === 'CronJob') return doc.spec?.jobTemplate?.spec?.template?.spec;
	return doc.spec?.template?.spec;
}

/** The images Ariva builds (images.yml): tagged by build number. Every other image is pinned by digest (ARV-062, CWE-494). */
const ARIVA_IMAGES = new Set(['api-main', 'api-ingest', 'api-stream', 'api-cronz', 'api-integration', 'simulation', 'web']);

function imageName(image) {
	const withoutDigest = String(image).split('@')[0];
	const lastSlash = withoutDigest.lastIndexOf('/');
	const colon = withoutDigest.indexOf(':', lastSlash + 1);
	return withoutDigest.slice(lastSlash + 1, colon === -1 ? undefined : colon);
}

/** The Ariva images that run a .NET host: each resolves its environment from DOTNET_ENVIRONMENT (ARV-098). */
const DOTNET_IMAGES = new Set([...ARIVA_IMAGES].filter((name) => name !== 'web'));

/**
 * The environment a .NET Ariva host in this container resolves (ArivaEnvironment.Resolve): an --environment argument
 * wins over everything, then DOTNET_ENVIRONMENT as the kubelet builds it (explicit env over envFrom; the last
 * definition of a name wins in each). Returns a string, undefined when nothing names it, or null when the value comes
 * from somewhere the check cannot read (a Secret or a valueFrom), which counts as wrong.
 */
function dotnetEnvironmentOf(container, docs) {
	const words = [...(container.command ?? []), ...(container.args ?? [])].map(String);
	let fromArgs;
	words.forEach((word, index) => {
		// --environment, /environment (=value or the next word) and the bare environment=value, as AddCommandLine reads them.
		const match = /^(?:(?:--|\/)environment(?:=(.*))?|environment=(.*))$/i.exec(word);
		if (match) fromArgs = match[1] ?? match[2] ?? words[index + 1] ?? '';
	});
	if (fromArgs !== undefined) return fromArgs;

	const own = (container.env ?? []).findLast((variable) => variable.name === 'DOTNET_ENVIRONMENT');
	if (own) return own.valueFrom ? null : own.value;

	let fromSources;
	for (const source of container.envFrom ?? []) {
		if (source.prefix) continue;
		if (source.secretRef) {
			fromSources = null;
			continue;
		}
		const name = source.configMapRef?.name;
		const map = docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === name);
		if (map?.data?.DOTNET_ENVIRONMENT !== undefined) fromSources = map.data.DOTNET_ENVIRONMENT;
	}
	return fromSources;
}

/**
 * The value a container's environment gives a variable: its own env over envFrom ConfigMaps (the last definition wins in
 * each). Undefined when nothing names it, null when it comes from somewhere the check cannot read (a Secret, a valueFrom).
 */
function envValueOf(container, docs, name) {
	const own = (container.env ?? []).findLast((variable) => variable.name === name);
	if (own) return own.valueFrom ? null : own.value;
	let fromSources;
	for (const source of container.envFrom ?? []) {
		if (source.prefix) continue;
		if (source.secretRef) {
			fromSources = null;
			continue;
		}
		const map = docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === source.configMapRef?.name);
		if (map?.data?.[name] !== undefined) fromSources = map.data[name];
	}
	return fromSources;
}

/**
 * ARV-165: an API host stops within its 50 s shutdown timeout (the message bus gets 35 s, and a stop during a message's
 * retries needs up to 28.75 s), then disposes and exits, so its pod's grace period is at least 60 s, and the host is told the
 * value (Hosting__GracePeriodSeconds) so it can refuse a shutdown timeout that would not fit. ARV-176: likewise its startup
 * probe's allowance (Hosting__StartupAllowanceSeconds), so it can refuse consumer waits at start that would not fit.
 */
const MIN_GRACE_SECONDS = 60;

/**
 * ARV-176: the least time a startup probe gives a container before the kubelet restarts it: initialDelaySeconds plus
 * periodSeconds times (failureThreshold - 1), with Kubernetes' defaults (0, 10, 3). The prober's ticks are not aligned
 * with the container's start, so the restart comes between this and one period later; the host must fit the earlier.
 */
export function startupAllowance(probe) {
	return Number(probe.initialDelaySeconds ?? 0) + Number(probe.periodSeconds ?? 10) * (Number(probe.failureThreshold ?? 3) - 1);
}

/**
 * Environment variable names that hold credentials: their values come from secrets, never from the manifest (CWE-798). The log
 * pseudonym key (Logging__PseudonymKey, ARV-091a) is one: whoever holds it can recompute a day's pseudonym of every desk code; so
 * are the OTLP collector's sender headers (OTEL_EXPORTER_OTLP_HEADERS, ARV-091a), which carry its key.
 */
const SECRET_ENV = /(PASSWORD|PASSWD|SECRET|TOKEN|PRIVATE_?KEY|API_?KEY|PSEUDONYM_?KEY|OTLP_?\w*HEADERS)/i;

/**
 * ARV-104g1 (CWE-269): the validation reader login, the only login that reads the shadow nowcast, reaches api-main (the
 * validation service) and the database-migration job (which creates it) and no other workload, by name and from its secret.
 */
const READER_ENV = /^Database__ValidationReader__/;
const READER_WORKLOADS = new Set(['Deployment/api-main-deployment', 'Job/database-migration']);
const isReaderSecret = (name) => /validation-reader/i.test(String(name ?? ''));

/** The workload's references to a secret that looks like the reader's: env, envFrom and volumes. */
function readerSecretReferences(pod) {
	const containers = [...(pod.initContainers ?? []), ...(pod.containers ?? [])];
	return [
		...containers.flatMap((container) => (container.env ?? []).map((variable) => variable.valueFrom?.secretKeyRef?.name)),
		...containers.flatMap((container) => (container.envFrom ?? []).map((source) => source.secretRef?.name)),
		...(pod.volumes ?? []).map((volume) => volume.secret?.secretName),
		...(pod.volumes ?? []).flatMap((volume) => (volume.projected?.sources ?? []).map((source) => source.secret?.name))
	].filter(isReaderSecret);
}

/** Findings for the reader login's variables in one workload (see READER_ENV). */
function readerLoginFindings(doc, id, pod, environment) {
	const findings = [];
	const containers = [...(pod.initContainers ?? []), ...(pod.containers ?? [])];
	const allowed = READER_WORKLOADS.has(id);
	if (!allowed && (containers.some((container) => (container.env ?? []).some((variable) => READER_ENV.test(variable.name ?? ''))) || readerSecretReferences(pod).length > 0)) {
		findings.push(`${id}: only api-main-deployment and the database-migration job may get the validation reader login (ARV-104g1)`);
	}
	for (const container of containers) {
		for (const variable of container.env ?? []) {
			if (READER_ENV.test(variable.name ?? '') && !variable.valueFrom?.secretKeyRef) {
				findings.push(`${id} container ${container.name}: ${variable.name}: the validation reader login comes from its secret (secretKeyRef), never a literal`);
			}
		}
	}
	if (!allowed) return findings;
	// The main container carries both values from one secret, keys username and password; production never marks it optional.
	const main = containers.find((container) => DOTNET_IMAGES.has(imageName(container.image ?? ''))) ?? containers[0] ?? {};
	const ref = (name) => (main.env ?? []).find((variable) => variable.name === name)?.valueFrom?.secretKeyRef;
	const user = ref('Database__ValidationReader__Username');
	const password = ref('Database__ValidationReader__Password');
	if (!user || !password || user.key !== 'username' || password.key !== 'password' || !user.name || user.name !== password.name) {
		findings.push(`${id}: must get the validation reader login (Database__ValidationReader__Username and Password, keys username and password) from one secret (ARV-104g1)`);
	} else if (environment === 'k8s-prd' && (user.optional === true || password.optional === true)) {
		findings.push(`${id}: k8s-prd must not mark the validation reader secret optional (ARV-104g1)`);
	}
	return findings;
}

/**
 * ARV-083a (CWE-269, CWE-285; ASVS 5.0 V13.2.2, V13.3.2): each API host connects to the database with a login of its own, and
 * the availability ledger in api-cronz with one more; the database-migration job, which creates them, alone gets them all.
 * - Each API host's .NET container gets Database__Username and Database__Password from one secret (keys username and
 *   password) that no other login comes from; api-cronz gets Database__Availability__Username and Password from one more.
 * - No workload but the host itself and the job refers to a host's login secret (env, envFrom or a volume), and none but
 *   api-cronz and the job to the ledger's; only the job gets Database__HostLogins__*, only api-cronz and the job
 *   Database__Availability__*, and every login variable comes from a secret (secretKeyRef), never a literal.
 * - The job gets Database__HostLogins__<Host>__Username and Password from each host's own secret and the ledger's login from
 *   api-cronz's, and runs without the service account token (automountServiceAccountToken false): it holds every login's
 *   password and calls no Kubernetes API.
 * With hosts (a whole render) every API host and the job must be there with their logins; without (a fixture), what is there is
 * checked.
 */
export const HOST_LOGIN_SECTIONS = { 'api-main': 'Main', 'api-ingest': 'Ingest', 'api-stream': 'Stream', 'api-cronz': 'Cronz', 'api-integration': 'Integration' };
const MIGRATION_JOB = 'Job/database-migration';
const CRONZ = 'Deployment/api-cronz-deployment';
const LEDGER = 'the availability ledger';
const LOGIN_ENV = /^Database__(HostLogins__[A-Za-z]+__|Availability__)?(Username|Password)$/;

/** Every secret a workload refers to: env, envFrom and volumes, of its init containers too. */
function secretReferences(pod) {
	const containers = [...(pod.initContainers ?? []), ...(pod.containers ?? [])];
	return [
		...containers.flatMap((container) => (container.env ?? []).map((variable) => variable.valueFrom?.secretKeyRef?.name)),
		...containers.flatMap((container) => (container.envFrom ?? []).map((source) => source.secretRef?.name)),
		...(pod.volumes ?? []).map((volume) => volume.secret?.secretName),
		...(pod.volumes ?? []).flatMap((volume) => (volume.projected?.sources ?? []).map((source) => source.secret?.name))
	].filter(Boolean);
}

/** The secret a login's two variables (prefix Username and prefix Password) come from, keys username and password, or null. */
function loginSecret(container, prefix) {
	const ref = (suffix) => (container.env ?? []).find((variable) => variable.name === `${prefix}${suffix}`)?.valueFrom?.secretKeyRef;
	const user = ref('Username');
	const password = ref('Password');
	return user && password && user.key === 'username' && password.key === 'password' && user.name && user.name === password.name ? String(user.name) : null;
}

export function hostLoginFindings(docs, { hosts = false } = {}) {
	const findings = [];
	const workloads = docs.filter((doc) => doc && typeof doc === 'object' && WORKLOADS.has(doc.kind))
		.map((doc) => ({ id: `${doc.kind}/${doc.metadata?.name ?? '?'}`, pod: podSpecOf(doc) ?? {} }));
	const hostContainer = (pod) => (pod.containers ?? []).find((container) => DOTNET_IMAGES.has(imageName(container.image ?? ''))) ?? (pod.containers ?? [])[0] ?? {};
	const hasVariable = (container, pattern) => (container.env ?? []).some((variable) => pattern.test(variable.name ?? ''));
	const owners = new Map();
	const hostSecrets = {};
	for (const host of Object.keys(HOST_LOGIN_SECTIONS)) {
		const id = `Deployment/${host}-deployment`;
		const workload = workloads.find((candidate) => candidate.id === id);
		if (!workload) {
			if (hosts) findings.push(`${id}: not rendered; every API host connects with a database login of its own (ARV-083a)`);
			continue;
		}
		const container = hostContainer(workload.pod);
		const secret = loginSecret(container, 'Database__');
		if (!secret) {
			if (hosts || hasVariable(container, /^Database__(Username|Password)$/)) {
				findings.push(`${id}: must get its own database login (Database__Username and Database__Password, keys username and password) from one secret (ARV-083a)`);
			}
			continue;
		}
		if (owners.has(secret)) findings.push(`${id}: its database login secret ${secret} is ${owners.get(secret)}'s too; each host has a login of its own (ARV-083a)`);
		else owners.set(secret, host);
		hostSecrets[host] = secret;
	}
	const cronz = workloads.find((candidate) => candidate.id === CRONZ);
	const ledger = cronz ? loginSecret(hostContainer(cronz.pod), 'Database__Availability__') : null;
	if (cronz && !ledger && (hosts || hasVariable(hostContainer(cronz.pod), /^Database__Availability__/))) {
		findings.push(`${CRONZ}: must get the availability ledger's login (Database__Availability__Username and Password, keys username and password) from one secret (ARV-083a)`);
	} else if (ledger && owners.has(ledger)) {
		findings.push(`${CRONZ}: the availability ledger's login secret ${ledger} is ${owners.get(ledger)}'s too; the ledger has a login of its own (ARV-083a)`);
	} else if (ledger) {
		owners.set(ledger, LEDGER);
	}
	for (const { id, pod } of workloads) {
		const containers = [...(pod.initContainers ?? []), ...(pod.containers ?? [])];
		for (const secret of new Set(secretReferences(pod))) {
			const owner = owners.get(secret);
			if (!owner || id === MIGRATION_JOB || id === (owner === LEDGER ? CRONZ : `Deployment/${owner}-deployment`)) continue;
			findings.push(`${id}: refers to ${owner === LEDGER ? LEDGER : owner}'s database login secret ${secret}; a pod gets its own host's login only (ARV-083a)`);
		}
		if (id !== MIGRATION_JOB && containers.some((container) => hasVariable(container, /^Database__HostLogins__/))) {
			findings.push(`${id}: only the database-migration job gets the host logins (Database__HostLogins__*; ARV-083a)`);
		}
		if (id !== MIGRATION_JOB && id !== CRONZ && containers.some((container) => hasVariable(container, /^Database__Availability__/))) {
			findings.push(`${id}: only api-cronz-deployment and the database-migration job get the availability ledger's login (Database__Availability__*; ARV-083a)`);
		}
		for (const container of containers) {
			for (const variable of container.env ?? []) {
				if (LOGIN_ENV.test(variable.name ?? '') && !variable.valueFrom?.secretKeyRef) {
					findings.push(`${id} container ${container.name}: ${variable.name}: a database login comes from its secret (secretKeyRef), never a literal (ARV-083a)`);
				}
			}
		}
	}
	const job = workloads.find((candidate) => candidate.id === MIGRATION_JOB);
	if (!job) {
		if (hosts) findings.push(`${MIGRATION_JOB}: not rendered; the migration job creates every host's database login (ARV-083a)`);
		return findings;
	}
	if (job.pod.automountServiceAccountToken !== false) {
		findings.push(`${MIGRATION_JOB}: must set automountServiceAccountToken false: it holds every host's database password and calls no Kubernetes API (ARV-083a)`);
	}
	const container = hostContainer(job.pod);
	for (const [host, section] of Object.entries(HOST_LOGIN_SECTIONS)) {
		const secret = loginSecret(container, `Database__HostLogins__${section}__`);
		if (hostSecrets[host] ? secret !== hostSecrets[host] : hosts && !secret) {
			findings.push(`${MIGRATION_JOB}: must get ${host}'s login (Database__HostLogins__${section}__Username and Password) from ${host}'s own secret${hostSecrets[host] ? ` ${hostSecrets[host]}` : ''} (ARV-083a)`);
		}
	}
	const jobLedger = loginSecret(container, 'Database__Availability__');
	if (ledger ? jobLedger !== ledger : hosts && !jobLedger) {
		findings.push(`${MIGRATION_JOB}: must get the availability ledger's login (Database__Availability__Username and Password) from api-cronz's ledger secret${ledger ? ` ${ledger}` : ''} (ARV-083a)`);
	}
	return findings;
}

/**
 * ARV-083b (CWE-269, CWE-285, CWE-522; ASVS 5.0 V13.2.1, V13.2.2, V13.3.1): each API host signs in to Kafka and Redis with
 * credentials of its own, and no pod holds another host's.
 * - Each API host's .NET container gets Kafka__SaslUsername and Kafka__SaslPassword from one secret (keys username and password)
 *   and Redis__Username and Redis__Password from another, each secret its own: no other host's, and not a database login's.
 * - No workload but the host itself refers to a host's Kafka, Redis or database login secret (env, envFrom or a volume; the
 *   database logins' own rule, hostLoginFindings, says which jobs may): the kafka-topics job may get api-main's Kafka principal
 *   alone (when kafkaTopics.adminSecretName gives it none of its own), and nothing of Redis.
 * - The Kafka password only where Kafka is used: Kafka__Sasl* only in the API hosts and the kafka-topics job, Redis__Username and
 *   Password only in the API hosts, each from a secret (secretKeyRef), never a literal; in k8s-prd never optional.
 * - The migration login's password (Database__Migration__Password) reaches the database-migration job alone, from its secret,
 *   which no workload refers to but that job, the scheduled backup (CronJob/ariva-db-backup) and the TimescaleDB server itself
 *   (StatefulSet/timescaledb, whose release owns the secret): base-appsettings-secret, which every pod mounts, holds no credential.
 * - In k8s-prd the kafka-topics job signs in as a principal of its own (kafkaTopics.adminSecretName), never api-main's, so no
 *   host's principal needs CREATE on the ariva. prefix (the ARV-083b review).
 * With hosts (a whole render) every API host must be there with both credentials and the job with the migration password;
 * without (a fixture), what is there is checked.
 */
const HOST_CREDENTIALS = [
	{ label: 'Kafka principal', user: 'Kafka__SaslUsername', password: 'Kafka__SaslPassword' },
	{ label: 'Redis user', user: 'Redis__Username', password: 'Redis__Password' }
];
const TOPICS_JOB = 'Job/kafka-topics';
/** The workloads that may refer to the migration login's password secret: the job, the scheduled backup, the database server. */
const MIGRATION_SECRET_READERS = new Set(['Job/database-migration', 'CronJob/ariva-db-backup', 'StatefulSet/timescaledb']);
const KAFKA_ENV = /^Kafka__Sasl(Username|Password)$/;
const REDIS_ENV = /^Redis__(Username|Password)$/;

/** The secret a credential's two variables come from, keys username and password, or null. */
function credentialSecret(container, user, password) {
	const ref = (name) => (container.env ?? []).find((variable) => variable.name === name)?.valueFrom?.secretKeyRef;
	const u = ref(user);
	const p = ref(password);
	return u && p && u.key === 'username' && p.key === 'password' && u.name && u.name === p.name ? { name: String(u.name), optional: u.optional === true || p.optional === true } : null;
}

export function hostCredentialFindings(docs, { environment, hosts = false } = {}) {
	const findings = [];
	const workloads = docs.filter((doc) => doc && typeof doc === 'object' && WORKLOADS.has(doc.kind))
		.map((doc) => ({ id: `${doc.kind}/${doc.metadata?.name ?? '?'}`, pod: podSpecOf(doc) ?? {} }));
	const hostContainer = (pod) => (pod.containers ?? []).find((container) => DOTNET_IMAGES.has(imageName(container.image ?? ''))) ?? (pod.containers ?? [])[0] ?? {};
	const hasVariable = (container, pattern) => (container.env ?? []).some((variable) => pattern.test(variable.name ?? ''));
	// Every secret a host owns, by name: its database login (ARV-083a), its Kafka principal and its Redis user.
	const owners = new Map();
	const hostIds = new Map(Object.keys(HOST_LOGIN_SECTIONS).map((host) => [`Deployment/${host}-deployment`, host]));
	for (const [id, host] of hostIds) {
		const workload = workloads.find((candidate) => candidate.id === id);
		const database = workload ? loginSecret(hostContainer(workload.pod), 'Database__') : null;
		if (database && !owners.has(database)) owners.set(database, { host, label: 'database login' });
	}
	for (const [id, host] of hostIds) {
		const workload = workloads.find((candidate) => candidate.id === id);
		if (!workload) {
			if (hosts) findings.push(`${id}: not rendered; every API host signs in to Kafka and Redis as its own (ARV-083b)`);
			continue;
		}
		const container = hostContainer(workload.pod);
		for (const credential of HOST_CREDENTIALS) {
			const secret = credentialSecret(container, credential.user, credential.password);
			if (!secret) {
				if (hosts || hasVariable(container, new RegExp(`^(${credential.user}|${credential.password})$`))) {
					findings.push(`${id}: must get its own ${credential.label} (${credential.user} and ${credential.password}, keys username and password) from one secret (ARV-083b)`);
				}
				continue;
			}
			const owner = owners.get(secret.name);
			if (owner) findings.push(`${id}: its ${credential.label} secret ${secret.name} is ${owner.host}'s ${owner.label} too; each host's credentials have secrets of their own (ARV-083b)`);
			else owners.set(secret.name, { host, label: credential.label });
			if (environment === 'k8s-prd' && secret.optional) findings.push(`${id}: k8s-prd must not mark its ${credential.label} secret optional (ARV-083b)`);
		}
	}
	// The migration login's password: the job's own secret, which no host or the topics job may refer to.
	const job = workloads.find((candidate) => candidate.id === 'Job/database-migration');
	const migration = job ? (hostContainer(job.pod).env ?? []).find((variable) => variable.name === 'Database__Migration__Password') : null;
	if (job && !migration?.valueFrom?.secretKeyRef?.name && (hosts || migration)) {
		findings.push('Job/database-migration: must get the migration login\'s password (Database__Migration__Password) from its own secret (secretKeyRef; migration.passwordSecret), never base-appsettings-secret (ARV-083b)');
	} else if (!job && hosts) {
		findings.push('Job/database-migration: not rendered; the migration job alone gets the migration login\'s password (ARV-083b)');
	}
	const migrationSecret = migration?.valueFrom?.secretKeyRef?.name ? String(migration.valueFrom.secretKeyRef.name) : null;
	for (const { id, pod } of workloads) {
		const containers = [...(pod.initContainers ?? []), ...(pod.containers ?? [])];
		const host = hostIds.get(id);
		for (const secret of new Set(secretReferences(pod))) {
			const owner = owners.get(secret);
			const topicsOnMain = id === TOPICS_JOB && owner?.host === 'api-main' && owner?.label === 'Kafka principal';
			if (owner && owner.label !== 'database login' && owner.host !== host && !topicsOnMain) {
				findings.push(`${id}: refers to ${owner.host}'s ${owner.label} secret ${secret}; a pod gets its own host's credentials only (ARV-083b)`);
			}
			if (topicsOnMain && environment === 'k8s-prd') {
				findings.push(`${id}: k8s-prd signs the topics job in as a principal of its own (kafkaTopics.adminSecretName), never api-main's ${secret} (ARV-083b)`);
			}
			if (secret === migrationSecret && !MIGRATION_SECRET_READERS.has(id)) {
				findings.push(`${id}: refers to the migration login's password secret ${secret}; only the database-migration job, the scheduled backup and the database server get it (ARV-083b)`);
			}
		}
		if (!host && id !== TOPICS_JOB && containers.some((container) => hasVariable(container, KAFKA_ENV))) {
			findings.push(`${id}: gets a Kafka credential; only the API hosts and the kafka-topics job sign in to Kafka (ARV-083b)`);
		}
		if (!host && containers.some((container) => hasVariable(container, REDIS_ENV))) {
			findings.push(`${id}: gets a Redis credential; only the API hosts sign in to Redis (ARV-083b)`);
		}
		if (id !== 'Job/database-migration' && containers.some((container) => hasVariable(container, /^Database__Migration__Password$/))) {
			findings.push(`${id}: gets the migration login's password; only the database-migration job does (ARV-083b)`);
		}
		for (const container of containers) {
			for (const variable of container.env ?? []) {
				if ((KAFKA_ENV.test(variable.name ?? '') || REDIS_ENV.test(variable.name ?? '') || variable.name === 'Database__Migration__Password') && !variable.valueFrom?.secretKeyRef) {
					findings.push(`${id} container ${container.name}: ${variable.name}: a credential comes from its secret (secretKeyRef), never a literal (ARV-083b)`);
				}
			}
		}
	}
	return findings;
}

/**
 * ARV-082a: the servers' CAs. Every workload that runs an Ariva .NET host against PostgreSQL and Redis (the API hosts and
 * the migration and topics jobs, which build the same host) mounts each CA, read-only and whole (a subPath mount never sees
 * a renewed CA), at the path the hosts read in k8s-prd (Database:RootCertificate, Redis:CaCertificatePath), from a secret
 * volume that projects ca.crt only, so a secret that also holds a private key never brings it into the pod.
 */
export const SERVER_CA_MOUNTS = [
	{ volume: 'database-ca', label: 'database', path: '/app/secrets/database-ca' },
	{ volume: 'redis-ca', label: 'Redis', path: '/app/secrets/redis-ca' }
];
export const isServerCaWorkload = (id) => /^Deployment\/api-[a-z]+-deployment$/.test(id) || id === 'Job/database-migration' || id === 'Job/kafka-topics';

function serverCaFindings(id, pod) {
	const findings = [];
	// The host's own container: a third-party container beside it (the malware scanner, ARV-092c) never needs the servers' CAs.
	for (const container of (pod.containers ?? []).filter((candidate) => DOTNET_IMAGES.has(imageName(candidate.image ?? '')))) {
		const cid = `${id} container ${container.name}`;
		for (const { label, path: mountPath } of SERVER_CA_MOUNTS) {
			const mount = (container.volumeMounts ?? []).find((candidate) => candidate.mountPath === mountPath);
			if (!mount) {
				findings.push(`${cid}: must mount the ${label} CA at ${mountPath} (ARV-082a)`);
				continue;
			}
			if (mount.readOnly !== true || mount.subPath) findings.push(`${cid}: must mount the ${label} CA read-only and whole (no subPath) at ${mountPath}`);
			const secret = (pod.volumes ?? []).find((volume) => volume.name === mount.name)?.secret;
			if (!secret?.secretName) {
				findings.push(`${cid}: the ${label} CA at ${mountPath} must come from a secret`);
				continue;
			}
			const items = secret.items ?? [];
			if (items.length !== 1 || items[0].key !== 'ca.crt' || items[0].path !== 'ca.crt') {
				findings.push(`${cid}: the ${label} CA volume must project only ca.crt (items), so a secret with a private key never brings it`);
			}
		}
	}
	return findings;
}

/** ARV-082a: the secret names behind the servers' CA mounts of each workload that has one, by workload id. */
export function serverCaSecrets(docs) {
	const found = {};
	for (const doc of docs) {
		if (!doc || !WORKLOADS.has(doc.kind)) continue;
		const pod = podSpecOf(doc) ?? {};
		const names = {};
		for (const { volume: name, path: mountPath } of SERVER_CA_MOUNTS) {
			const mounted = [...(pod.initContainers ?? []), ...(pod.containers ?? [])].some((container) => (container.volumeMounts ?? []).some((mount) => mount.name === name || mount.mountPath === mountPath));
			const secretName = (pod.volumes ?? []).find((volume) => volume.name === name)?.secret?.secretName;
			if (mounted || secretName) names[name] = secretName ?? null;
		}
		if (Object.keys(names).length) found[`${doc.kind}/${doc.metadata?.name}`] = names;
	}
	return found;
}

/** ARV-082a (V12.1.2): the TimescaleDB server's TLS 1.2 suites, ECDHE with AEAD only (TLS 1.3 suites are OpenSSL's AEAD set). */
export const DATABASE_TLS12_CIPHERS = 'ECDHE+AESGCM:ECDHE+CHACHA20:!aNULL';

/** A file mode as Kubernetes reads it: a number from a manifest parsed as YAML 1.1 (0440 is octal there), or an octal string. */
const fileMode = (value) => (typeof value === 'string' ? parseInt(value, 8) : Number(value));

/** The server settings a PostgreSQL container's command line gives: -c name=value, -cname=value and --name=value. */
function postgresSettings(container) {
	const words = [...(container.command ?? []), ...(container.args ?? [])].map(String);
	const settings = {};
	words.forEach((word, index) => {
		const pair = word === '-c' ? words[index + 1] : word.startsWith('-c') ? word.slice(2) : word.startsWith('--') ? word.slice(2) : undefined;
		const match = /^([A-Za-z_][A-Za-z0-9_.-]*)=(.*)$/.exec(pair ?? '');
		if (match) settings[match[1].toLowerCase().replace(/-/g, '_')] = match[2];
	});
	return settings;
}

/** The volume, key and mode behind a file path in a container (the deepest mount that holds it), or undefined. */
function mountedFile(pod, container, file) {
	const mounts = [...(container.volumeMounts ?? [])].sort((a, b) => String(b.mountPath).length - String(a.mountPath).length);
	for (const mount of mounts) {
		const base = String(mount.mountPath ?? '').replace(/\/+$/, '');
		let relative;
		if (mount.subPath) {
			if (file !== base) continue;
			relative = mount.subPath;
		} else if (file.startsWith(`${base}/`)) {
			relative = file.slice(base.length + 1);
		} else {
			continue;
		}
		const volume = (pod.volumes ?? []).find((candidate) => candidate.name === mount.name);
		const source = volume?.secret ?? volume?.configMap;
		if (!source) return { volume };
		const item = source.items ? source.items.find((candidate) => candidate.path === relative) : { key: relative };
		if (!item) return { volume };
		return { volume, key: item.key, mode: item.mode ?? source.defaultMode ?? 0o644 };
	}
	return undefined;
}

/**
 * ARV-082a: findings for a pg_hba.conf that takes TCP connections over TLS only: the local socket keeps peer; every TCP line
 * other than hostssl rejects (host, hostnossl, hostgssenc and hostnogssenc can all take a connection without TLS); hostssl
 * uses scram-sha-256 or cert; hostnossl all all all reject is there; nothing is included from elsewhere.
 */
function hbaFindings(text, where) {
	const findings = [];
	let rejectsPlain = false;
	let takesTls = false;
	const lines = String(text).split('\n').map((line) => line.replace(/#.*$/, '').trim()).filter(Boolean).map((line) => line.split(/\s+/));
	for (const fields of lines) {
		const [type, database, user, address] = fields;
		if (/^include/.test(type)) {
			findings.push(`${where}: pg_hba.conf must not include other files (${type})`);
			continue;
		}
		if (type === 'local') {
			if (fields[3] !== 'peer') findings.push(`${where}: the local socket must keep peer authentication, not ${fields[3]}`);
			continue;
		}
		if (!['host', 'hostssl', 'hostnossl', 'hostgssenc', 'hostnogssenc'].includes(type)) {
			findings.push(`${where}: pg_hba.conf line of unknown type ${type}`);
			continue;
		}
		const separateMask = address && !address.includes('/') && /^[0-9a-f.:]+$/i.test(address) && /^[0-9a-f.:]+$/i.test(fields[4] ?? '');
		const method = fields[separateMask ? 5 : 4];
		if (type === 'hostssl') {
			if (!['scram-sha-256', 'cert', 'reject'].includes(method)) findings.push(`${where}: hostssl must use scram-sha-256 or cert, not ${method}`);
			if (method !== 'reject') takesTls = true;
		} else if (method !== 'reject') {
			findings.push(`${where}: pg_hba.conf accepts TCP connections without TLS (${fields.join(' ')})`);
		}
		if (type === 'hostnossl' && database === 'all' && user === 'all' && address === 'all' && method === 'reject') rejectsPlain = true;
	}
	if (!rejectsPlain) findings.push(`${where}: pg_hba.conf must reject every TCP connection without TLS (hostnossl all all all reject)`);
	if (!takesTls) findings.push(`${where}: pg_hba.conf takes no TCP connection over TLS (no hostssl line)`);
	return findings;
}

/**
 * ARV-082a: the TimescaleDB server's TLS, for every StatefulSet that runs PostgreSQL (POSTGRES_PASSWORD), from docs parsed
 * as Kubernetes reads them (YAML 1.1, so a mode such as 0440 is octal). With tls on: ssl on; ssl_cert_file and ssl_key_file
 * from the secret secretName (keys tls.crt and tls.key); a key file PostgreSQL accepts as the kubelet writes it (root owns
 * it, fsGroup gives the postgres user group read, so the mode allows no group write and nothing for others: 0640 at most);
 * ssl_min_protocol_version TLSv1.2 or TLSv1.3; ssl_ciphers exactly DATABASE_TLS12_CIPHERS (V12.1.2); hba_file the release's
 * pg_hba.conf from a ConfigMap, TCP over TLS only (hbaFindings). With tls off: no ssl, ssl_* or hba_file setting at all.
 */
export function databaseTlsFindings(docs, { tls, secretName = 'timescaledb-tls' }) {
	const findings = [];
	for (const doc of docs) {
		if (doc?.kind !== 'StatefulSet') continue;
		const id = `${doc.kind}/${doc.metadata?.name ?? '?'}`;
		const pod = podSpecOf(doc) ?? {};
		for (const container of pod.containers ?? []) {
			if (!(container.env ?? []).some((variable) => variable.name === 'POSTGRES_PASSWORD')) continue;
			const cid = `${id} container ${container.name}`;
			const settings = postgresSettings(container);
			const on = /^(on|true|yes|1)$/i.test(String(settings.ssl ?? ''));
			if (!tls) {
				const set = Object.keys(settings).filter((name) => name === 'ssl' || name.startsWith('ssl_') || name === 'hba_file');
				if (set.length) findings.push(`${cid}: renders TLS settings (${set.join(', ')}) although TLS is off for this release`);
				continue;
			}
			if (!on) findings.push(`${cid}: ssl is not on; every TCP connection must be encrypted (ARV-082a)`);
			for (const [setting, key] of [['ssl_cert_file', 'tls.crt'], ['ssl_key_file', 'tls.key']]) {
				const file = settings[setting] ? mountedFile(pod, container, settings[setting]) : undefined;
				if (!file?.volume?.secret || file.volume.secret.secretName !== secretName || file.key !== key) {
					findings.push(`${cid}: ${setting} does not come from the secret ${secretName} (key ${key})`);
					continue;
				}
				if (setting === 'ssl_key_file') {
					const mode = fileMode(file.mode);
					if (!(Number(pod.securityContext?.fsGroup) > 0) || !Number.isInteger(mode) || (mode & 0o037) !== 0) {
						findings.push(`${cid}: PostgreSQL refuses the key file as mounted (mode ${Number.isInteger(mode) ? '0' + mode.toString(8) : file.mode}): root owns it, so it needs fsGroup for group read and at most u=rw,g=r (0640)`);
					}
				}
			}
			const floor = String(settings.ssl_min_protocol_version ?? '');
			if (!['TLSv1.2', 'TLSv1.3'].includes(floor)) findings.push(`${cid}: ssl_min_protocol_version must be TLSv1.2 or TLSv1.3, not ${floor || 'unset'}`);
			if (on && settings.ssl_ciphers !== DATABASE_TLS12_CIPHERS) {
				findings.push(`${cid}: ssl_ciphers must be ${DATABASE_TLS12_CIPHERS} (TLS 1.2 with ECDHE and AEAD only, V12.1.2), not ${settings.ssl_ciphers ?? 'unset'}`);
			}
			const hbaPath = String(settings.hba_file ?? '');
			const hba = hbaPath ? mountedFile(pod, container, hbaPath) : undefined;
			const map = hba?.volume?.configMap ? docs.find((other) => other?.kind === 'ConfigMap' && other.metadata?.name === hba.volume.configMap.name) : undefined;
			const text = map?.data?.[hba?.key];
			if (typeof text !== 'string') {
				findings.push(`${cid}: hba_file must be the release's pg_hba.conf from a ConfigMap (${hbaPath || 'hba_file unset'})`);
				continue;
			}
			findings.push(...hbaFindings(text, `${cid} hba_file`));
		}
	}
	return findings;
}

/**
 * ARV-082b: pod TLS. The pods the ingress and the hosts reach (the five API hosts and the web pod's nginx, by image) serve
 * TLS only, from their own certificate; the Services and every Ingress reach them over TLS, and the ingress verifies them.
 */
export const POD_TLS = {
	services: ['api-main', 'api-ingest', 'api-stream', 'api-cronz', 'api-integration', 'web'],
	/** The TLS container port by kind of image (named https) and the plain ports that must be gone. */
	ports: { dotnet: 8443, web: 3443 },
	plainPorts: [8080, 3000],
	/** Where each kind of image reads its certificate (tls.crt, tls.key): Kestrel in k8s-prd, nginx's tls.conf. */
	certificates: { dotnet: '/app/secrets/pod-tls', web: '/etc/nginx/tls' },
	/** The directory the web image's default.conf includes inside its server block (listen.d/*.conf). */
	webListen: '/etc/nginx/listen.d',
	caSecretName: 'ariva-cluster-ca',
	/**
	 * Up to two intermediate CAs between a pod's certificate and the cluster CA. OpenSSL's verify depth counts the intermediates
	 * only (neither the leaf nor the trust anchor): 1 already takes the leaf, one intermediate and the root (measured with
	 * nginx 1.30 as the proxy); 2 leaves room for a site PKI with an issuing CA below a policy CA.
	 */
	verifyDepth: 2,
	/** Kestrel rebinds its endpoint when it reloads a renewed certificate: up to this long without new connections. */
	rotationGapSeconds: 5
};
/** The TLS 1.2 suites every Ariva TLS server allows, ECDHE with AEAD only (V12.1.2): the database's list (ARV-082a). */
export const TLS12_CIPHERS = DATABASE_TLS12_CIPHERS;
const TLS_PROTOCOLS = new Set(['TLSv1.2', 'TLSv1.3']);
const INGRESS_ANNOTATION = 'nginx.ingress.kubernetes.io/';
const PROBES = ['startupProbe', 'readinessProbe', 'livenessProbe'];

/** dotnet for the five API images, web for the web image, undefined for anything else (the simulator, third parties). */
function podTlsRole(container) {
	const name = imageName(container.image ?? '');
	if (!POD_TLS.services.includes(name)) return undefined;
	return name === 'web' ? 'web' : 'dotnet';
}

/** The container port number a probe or a Service's targetPort names: a number as it is, a name through the container's ports. */
function containerPortNumber(container, port) {
	if (port === undefined || port === null) return undefined;
	if (/^\d+$/.test(String(port))) return Number(port);
	const named = (container.ports ?? []).find((candidate) => candidate.name === port);
	return named ? Number(named.containerPort) : undefined;
}

/** The containers that serve HTTP to the Services: those of the TLS images in long-running workloads (not the jobs). */
function servedContainers(docs) {
	const served = [];
	for (const doc of docs) {
		if (!doc || !['Deployment', 'StatefulSet', 'DaemonSet'].includes(doc.kind)) continue;
		const pod = podSpecOf(doc) ?? {};
		for (const container of pod.containers ?? []) {
			const role = podTlsRole(container);
			if (role) served.push({ doc, pod, container, role, service: imageName(container.image), id: `${doc.kind}/${doc.metadata?.name ?? '?'}`, labels: doc.spec?.template?.metadata?.labels ?? {} });
		}
	}
	return served;
}

const selects = (service, labels) => {
	const wanted = Object.entries(service.spec?.selector ?? {});
	return wanted.length > 0 && wanted.every(([key, value]) => labels?.[key] === value);
};

/** The statements of an nginx file as token lists, comments dropped, with a flag for any block. */
function nginxStatements(text) {
	const body = String(text).split('\n').map((line) => line.replace(/#.*$/, '')).join('\n');
	return { blocks: /[{}]/.test(body), statements: body.split(';').map((statement) => statement.trim()).filter(Boolean).map((statement) => statement.split(/\s+/)) };
}

/** Findings for the certificate a TLS container serves: one whole, read-only directory of tls.crt and tls.key only. */
function podCertificateFindings(entry, cid, secretNames) {
	const { pod, container, role, service } = entry;
	const findings = [];
	const directory = POD_TLS.certificates[role];
	const mounts = container.volumeMounts ?? [];
	const volumeOf = (mount) => (pod.volumes ?? []).find((volume) => volume.name === mount.name);
	const atDirectory = mounts.filter((mount) => mount.mountPath === directory || String(mount.mountPath).startsWith(`${directory}/`));
	const secretNamesHere = new Set(atDirectory.map((mount) => volumeOf(mount)?.secret?.secretName).filter(Boolean));
	// A subPath mount never receives a renewed certificate (the kubelet swaps ..data, a subPath keeps the old file), anywhere
	// in the pod and wherever it is mounted.
	const subPath = [...(pod.initContainers ?? []), ...(pod.containers ?? [])].flatMap((other) => other.volumeMounts ?? [])
		.some((mount) => (mount.subPath || mount.subPathExpr) && (atDirectory.includes(mount) || secretNamesHere.has(volumeOf(mount)?.secret?.secretName)));
	if (subPath) {
		findings.push(`${cid}: mounts its certificate with subPath, which never receives a renewed certificate; mount the secret's directory at ${directory}`);
		return findings;
	}
	const mount = atDirectory.find((candidate) => candidate.mountPath === directory);
	if (!mount) {
		findings.push(`${cid}: must mount its certificate (tls.crt, tls.key) at ${directory}`);
		return findings;
	}
	if (mount.readOnly !== true) findings.push(`${cid}: must mount its certificate read-only at ${directory}`);
	const secret = volumeOf(mount)?.secret;
	if (!secret?.secretName) {
		findings.push(`${cid}: the certificate at ${directory} must come from a secret`);
		return findings;
	}
	const items = secret.items ?? [];
	const only = items.length === 2 && ['tls.crt', 'tls.key'].every((key) => items.some((item) => item.key === key && item.path === key));
	if (!only) findings.push(`${cid}: the certificate volume must project only tls.crt and tls.key (items), so a ca.crt or anything else the secret holds never reaches the pod`);
	const key = items.find((item) => item.key === 'tls.key');
	const mode = fileMode(key?.mode ?? secret.defaultMode ?? 0o644);
	if (!Number.isInteger(mode) || (mode & 0o037) !== 0) {
		findings.push(`${cid}: the key file's mode ${Number.isInteger(mode) ? '0' + mode.toString(8) : mode} lets others read it or the group write it; at most 0440, read through fsGroup`);
	}
	if (!(Number(pod.securityContext?.fsGroup) > 0)) findings.push(`${cid}: the pod needs fsGroup, so the non-root app user reads the root-owned key file`);
	if (secretNames[service] && secret.secretName !== secretNames[service]) findings.push(`${cid}: serves the certificate of ${secret.secretName}, not ${secretNames[service]}`);
	return findings;
}

/** Findings for the web pod's nginx listener: the listen.d directory holds TLS listeners only, as the release renders them. */
function webListenFindings(entry, cid, docs, expected) {
	const { pod, container } = entry;
	const findings = [];
	const directory = POD_TLS.webListen;
	const mounts = (container.volumeMounts ?? []).filter((mount) => mount.mountPath === directory || String(mount.mountPath).startsWith(`${directory}/`));
	if (mounts.some((mount) => mount.subPath || mount.subPathExpr)) {
		findings.push(`${cid}: mounts its listener with subPath, which leaves the image's plain listen.d/plain.conf in place; mount the ConfigMap over ${directory}`);
		return findings;
	}
	const mount = mounts.find((candidate) => candidate.mountPath === directory);
	if (!mount) {
		findings.push(`${cid}: must mount the TLS listener over ${directory}, or the image's plain listener stays`);
		return findings;
	}
	for (const other of mounts.filter((candidate) => candidate !== mount)) findings.push(`${cid}: adds ${other.mountPath} to the listener directory`);
	const configMap = (pod.volumes ?? []).find((volume) => volume.name === mount.name)?.configMap;
	const map = configMap ? docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === configMap.name) : undefined;
	if (!map) {
		findings.push(`${cid}: the listener at ${directory} must come from a ConfigMap of the release`);
		return findings;
	}
	const files = configMap.items ? configMap.items.map((item) => ({ key: item.key, path: item.path })) : Object.keys(map.data ?? {}).map((key) => ({ key, path: key }));
	let tlsListeners = 0;
	let protocols;
	let ciphers;
	const certificatePaths = {};
	for (const { key, path: file } of files.filter((candidate) => String(candidate.path).endsWith('.conf'))) {
		const where = `${cid} ${directory}/${file}`;
		const { blocks, statements } = nginxStatements(map.data?.[key] ?? '');
		if (blocks) findings.push(`${where}: holds a block; the file is included inside default.conf's server block`);
		for (const [directive, ...args] of statements) {
			if (directive === 'include') findings.push(`${where}: must not include other files (include ${args.join(' ')})`);
			if (directive === 'listen') {
				const port = Number(/(?:^|:)(\d+)$/.exec(args[0] ?? '')?.[1]);
				if (!args.slice(1).includes('ssl')) {
					findings.push(`${where}: listens without TLS (listen ${args.join(' ')})`);
					continue;
				}
				tlsListeners += 1;
				if (port !== expected) findings.push(`${where}: listens on ${args[0]}, not the https port ${expected}`);
			}
			if (directive === 'ssl_protocols') protocols = args;
			if (directive === 'ssl_ciphers') ciphers = args.join(' ');
			if (directive === 'ssl_certificate' || directive === 'ssl_certificate_key') certificatePaths[directive] = args[0];
		}
	}
	if (tlsListeners === 0) findings.push(`${cid}: the listener directory holds no TLS listener (listen ${expected} ssl)`);
	if (!protocols?.length || protocols.some((protocol) => !TLS_PROTOCOLS.has(protocol))) findings.push(`${cid}: ssl_protocols must be TLSv1.2 and TLSv1.3 only, not ${protocols?.join(' ') ?? 'unset'}`);
	if (ciphers !== TLS12_CIPHERS) findings.push(`${cid}: ssl_ciphers must be ${TLS12_CIPHERS} (TLS 1.2 with ECDHE and AEAD only, V12.1.2), not ${ciphers ?? 'unset'}`);
	// The volumes mounted at or under the certificate directory (podCertificateFindings judges how they are mounted).
	const certificate = new Set((container.volumeMounts ?? []).filter((candidate) => candidate.mountPath === POD_TLS.certificates.web ||
		String(candidate.mountPath).startsWith(`${POD_TLS.certificates.web}/`)).map((candidate) => candidate.name));
	for (const [directive, key] of [['ssl_certificate', 'tls.crt'], ['ssl_certificate_key', 'tls.key']]) {
		const file = certificatePaths[directive] ? mountedFile(pod, container, certificatePaths[directive]) : undefined;
		if (!file?.volume?.secret || !certificate.has(file.volume.name) || file.key !== key) {
			findings.push(`${cid}: ${directive} does not come from the pod's certificate at ${POD_TLS.certificates.web} (key ${key})`);
		}
	}
	return findings;
}

/**
 * ARV-082b: findings for pod TLS, from docs parsed as Kubernetes reads them (YAML 1.1, so a mode such as 0440 is octal).
 * With tls on, for every container of the five API images and the web image in a long-running workload:
 * - no plain container port (8080, 3000); its TLS port named https (8443 for the API hosts, 3443 for nginx);
 * - every probe httpGet with scheme HTTPS on that port, and a readiness probe that tolerates one certificate rotation (the
 *   failures a gap of rotationGapSeconds can cause stay below failureThreshold);
 * - its certificate mounted read-only as a whole directory (never a subPath, which a renewal never reaches) from a secret
 *   volume projecting only tls.crt and tls.key, the key at most 0440 and read through fsGroup, from secretNames[service]
 *   when given;
 * - the web container's listen.d directory a ConfigMap of TLS listeners on its https port only, with the protocols, the
 *   TLS 1.2 suites and the certificate and key from its certificate mount.
 * Every Service that selects such a pod reaches no plain port and serves the https port on 443 only. Every Ingress proxies
 * HTTPS to port 443 of a Service the render holds, one backend per Ingress, and verifies it: proxy-ssl-verify on,
 * proxy-ssl-secret <namespace>/<caSecretName>, proxy-ssl-name <service>.<namespace>.svc, proxy-ssl-server-name on,
 * proxy-ssl-protocols TLS 1.2 and 1.3 only, proxy-ssl-verify-depth 2 at least, a proxy-next-upstream that keeps error and
 * timeout when it is set, and no annotation value (a snippet) that writes a proxy_ssl, grpc_ssl, *_pass or *_by_lua
 * directive itself. Whether the CA
 * secret holds ca.crt is the cluster's to say: validate.yaml judges it through lookup. With tls off, nothing of this is
 * rendered (the plain layout).
 */
export function podTlsFindings(docs, { tls, namespace = 'default', caSecretName = POD_TLS.caSecretName, secretNames = {} }) {
	const findings = [];
	const served = servedContainers(docs);
	const services = docs.filter((doc) => doc?.kind === 'Service');
	const ingresses = docs.filter((doc) => doc?.kind === 'Ingress');
	if (!tls) {
		for (const { container, role, id } of served) {
			const artefacts = [
				...(container.ports ?? []).filter((port) => port.name === 'https' || Object.values(POD_TLS.ports).includes(Number(port.containerPort))).map((port) => `port ${port.containerPort}`),
				...(container.volumeMounts ?? []).filter((mount) => [POD_TLS.certificates[role], POD_TLS.webListen].includes(mount.mountPath)).map((mount) => mount.mountPath),
				...PROBES.filter((probe) => container[probe]?.httpGet?.scheme === 'HTTPS').map((probe) => `${probe} HTTPS`)
			];
			if (artefacts.length) findings.push(`${id} container ${container.name}: renders pod TLS (${artefacts.join(', ')}) although pod TLS is off`);
		}
		for (const service of services) {
			if (served.some((entry) => selects(service, entry.labels)) && (service.spec?.ports ?? []).some((port) => Number(port.port) === 443 || port.targetPort === 'https')) {
				findings.push(`Service/${service.metadata?.name}: serves 443 or https although pod TLS is off`);
			}
		}
		for (const ingress of ingresses) {
			const set = Object.keys(ingress.metadata?.annotations ?? {}).filter((key) => key === `${INGRESS_ANNOTATION}backend-protocol` || key.startsWith(`${INGRESS_ANNOTATION}proxy-ssl-`));
			if (set.length) findings.push(`Ingress/${ingress.metadata?.name}: sets ${set.join(', ')} although pod TLS is off`);
		}
		return findings;
	}

	for (const entry of served) {
		const { container, role } = entry;
		const cid = `${entry.id} container ${container.name}`;
		const expected = POD_TLS.ports[role];
		for (const port of (container.ports ?? []).filter((candidate) => POD_TLS.plainPorts.includes(Number(candidate.containerPort)))) {
			findings.push(`${cid}: serves plain HTTP on container port ${port.containerPort}; with pod TLS only ${expected}, named https`);
		}
		const https = (container.ports ?? []).find((port) => port.name === 'https');
		if (!https || Number(https.containerPort) !== expected) findings.push(`${cid}: needs its TLS port ${expected}, named https`);
		for (const probe of PROBES.filter((name) => container[name])) {
			const get = container[probe].httpGet;
			if (!get || get.scheme !== 'HTTPS' || containerPortNumber(container, get.port) !== expected) findings.push(`${cid}: ${probe} must be httpGet with scheme HTTPS on ${expected}`);
		}
		const readiness = container.readinessProbe;
		if (!readiness) findings.push(`${cid}: needs a readinessProbe`);
		else {
			const period = Number(readiness.periodSeconds ?? 10);
			const threshold = Number(readiness.failureThreshold ?? 3);
			const fallible = Math.floor(POD_TLS.rotationGapSeconds / period) + 1;
			if (!(threshold > fallible)) {
				findings.push(`${cid}: readinessProbe must tolerate a certificate rotation (${POD_TLS.rotationGapSeconds} s without new connections fail up to ${fallible} probes every ${period} s; failureThreshold ${threshold} takes the pod out of its Service)`);
			}
		}
		findings.push(...podCertificateFindings(entry, cid, secretNames));
		if (role === 'web') findings.push(...webListenFindings(entry, cid, docs, expected));
	}

	for (const service of services) {
		const sid = `Service/${service.metadata?.name}`;
		const entry = served.find((candidate) => selects(service, candidate.labels));
		if (!entry) continue;
		const expected = POD_TLS.ports[entry.role];
		for (const port of service.spec?.ports ?? []) {
			const target = port.targetPort ?? port.port;
			const resolved = containerPortNumber(entry.container, target);
			if (resolved === undefined) findings.push(`${sid}: port ${port.port} targets ${target}, which no container port of ${entry.id} answers`);
			else if (POD_TLS.plainPorts.includes(resolved)) findings.push(`${sid}: port ${port.port} reaches plain HTTP on ${resolved}`);
			else if ((resolved === expected) !== (Number(port.port) === 443)) {
				findings.push(`${sid}: port ${port.port} reaches ${resolved}; the pods' https port is served on 443 only (the internal addresses are https://<service> with no port)`);
			}
		}
	}

	for (const ingress of ingresses) {
		const iid = `Ingress/${ingress.metadata?.name}`;
		const annotations = ingress.metadata?.annotations ?? {};
		const get = (name) => annotations[`${INGRESS_ANNOTATION}${name}`];
		const ingressNamespace = ingress.metadata?.namespace ?? namespace;
		const backends = [ingress.spec?.defaultBackend, ...(ingress.spec?.rules ?? []).flatMap((rule) => (rule.http?.paths ?? []).map((entry) => entry.backend))]
			.map((backend) => backend?.service).filter(Boolean);
		const names = [...new Set(backends.map((backend) => backend.name))];
		if (get('backend-protocol') !== 'HTTPS') findings.push(`${iid}: must proxy HTTPS to the pods (backend-protocol HTTPS), not ${get('backend-protocol') ?? 'HTTP'}`);
		if (get('proxy-ssl-verify') !== 'on') findings.push(`${iid}: must verify the pods' certificate (proxy-ssl-verify on)`);
		const ca = `${ingressNamespace}/${caSecretName}`;
		if (!get('proxy-ssl-secret')) findings.push(`${iid}: needs proxy-ssl-secret, the cluster CA it verifies the pods against (${ca}); without it ingress-nginx verifies nothing`);
		else if (get('proxy-ssl-secret') !== ca) findings.push(`${iid}: proxy-ssl-secret is ${get('proxy-ssl-secret')}, not ${ca} (the cluster CA in the Ingress's namespace)`);
		if (names.length !== 1) findings.push(`${iid}: routes to ${names.join(', ') || 'no service'}; one backend per Ingress, so proxy-ssl-name names it`);
		else if (get('proxy-ssl-name') !== `${names[0]}.${ingressNamespace}.svc`) findings.push(`${iid}: proxy-ssl-name is ${get('proxy-ssl-name') ?? 'unset'}, not its backend ${names[0]}.${ingressNamespace}.svc`);
		if (get('proxy-ssl-server-name') !== 'on') findings.push(`${iid}: must send the backend's name (proxy-ssl-server-name on)`);
		const protocols = String(get('proxy-ssl-protocols') ?? '').split(/\s+/).filter(Boolean);
		if (!protocols.length || protocols.some((protocol) => !TLS_PROTOCOLS.has(protocol))) findings.push(`${iid}: proxy-ssl-protocols must be TLSv1.2 and TLSv1.3 only, not ${get('proxy-ssl-protocols') ?? 'unset'}`);
		const depth = Number(get('proxy-ssl-verify-depth'));
		if (!Number.isInteger(depth) || depth < POD_TLS.verifyDepth) findings.push(`${iid}: proxy-ssl-verify-depth must be at least ${POD_TLS.verifyDepth} (up to two intermediate CAs below the cluster CA), not ${get('proxy-ssl-verify-depth') ?? 'unset (1)'}`);
		// A snippet (configuration-snippet, server-snippet or any other annotation value) that writes proxy_ssl_*, grpc_ssl_*, a
		// *_pass directive or a *_by_lua block could turn the verification off or proxy around it; the chart's own write none.
		for (const [key, value] of Object.entries(annotations)) {
			const written = /proxy_ssl\w*|grpc_ssl\w*|\w*_pass\b|\w*_by_lua\w*/i.exec(String(value));
			if (written) findings.push(`${iid}: ${key} writes ${written[0]} itself; with pod TLS only the chart's proxy-ssl annotations configure the proxy to the pods`);
		}
		const retry = get('proxy-next-upstream');
		if (retry !== undefined && !['error', 'timeout'].every((reason) => String(retry).split(/\s+/).includes(reason))) {
			findings.push(`${iid}: proxy-next-upstream ${retry} drops error or timeout; a pod rebinding a renewed certificate must cost a retry on another pod, not an error`);
		}
		for (const backend of backends) {
			const port = backend.port?.number ?? backend.port?.name;
			if (!(Number(port) === 443 || port === 'https')) {
				findings.push(`${iid}: backend ${backend.name} port ${port} is not 443`);
				continue;
			}
			const service = services.find((candidate) => candidate.metadata?.name === backend.name);
			if (!service) findings.push(`${iid}: routes to ${backend.name}, which the render does not hold`);
			else if (!(service.spec?.ports ?? []).some((candidate) => Number(candidate.port) === 443 && (port === 'https' ? candidate.name === 'https' : true))) {
				findings.push(`${iid}: ${backend.name} serves no port 443`);
			}
		}
	}
	return findings;
}

/** ARV-082b: the secret behind the pod TLS certificate mount of each workload that has one, by workload id. */
export function podTlsSecrets(docs) {
	const found = {};
	for (const doc of docs) {
		if (!doc || !WORKLOADS.has(doc.kind)) continue;
		const pod = podSpecOf(doc) ?? {};
		for (const container of [...(pod.initContainers ?? []), ...(pod.containers ?? [])]) {
			for (const mount of container.volumeMounts ?? []) {
				if (!Object.values(POD_TLS.certificates).includes(mount.mountPath)) continue;
				found[`${doc.kind}/${doc.metadata?.name}`] = (pod.volumes ?? []).find((volume) => volume.name === mount.name)?.secret?.secretName ?? null;
			}
		}
	}
	return found;
}

function labelsMatch(selector, labels) {
	const wanted = Object.entries(selector?.matchLabels ?? {});
	return wanted.every(([key, value]) => labels?.[key] === value);
}

function imageTag(image) {
	const withoutDigest = String(image).split('@')[0];
	const lastSlash = withoutDigest.lastIndexOf('/');
	const colon = withoutDigest.indexOf(':', lastSlash + 1);
	return colon === -1 ? '' : withoutDigest.slice(colon + 1);
}

/** Every node of a JSON value, depth first. */
function* walk(node) {
	if (node === null || typeof node !== 'object') return;
	yield node;
	for (const child of Array.isArray(node) ? node : Object.values(node)) yield* walk(child);
}

/** The queries of a rule or dashboard: the rules' builder, formula and PromQL queries and the dashboards' builder queries. */
function* monitoringQueries(json) {
	for (const node of walk(json)) {
		if (Array.isArray(node)) continue;
		if (typeof node.type === 'string' && node.spec && typeof node.spec === 'object' && !Array.isArray(node.spec)) yield { type: node.type, spec: node.spec };
		if (node.kind === 'signoz/BuilderQuery' && node.spec) yield { type: 'builder_query', spec: node.spec };
	}
}

/** The label keys a rule or dashboard filters, groups or prints by (the same reading as MonitoringRulesTests). */
export function monitoringLabels(json) {
	const keys = [];
	const filterKeys = /([A-Za-z_][A-Za-z0-9_.]*)\s*(?:=|!=|<=|>=|<|>|\bNOT\s+IN\b|\bIN\b|\bNOT\s+LIKE\b|\bLIKE\b|\bCONTAINS\b|\bEXISTS\b|\bREGEXP\b)/gi;
	for (const { spec } of monitoringQueries(json)) {
		const filter = spec.filter?.expression;
		if (typeof filter === 'string') for (const match of filter.matchAll(filterKeys)) keys.push(match[1]);
		for (const group of spec.groupBy ?? []) if (group?.name) keys.push(group.name);
		if (typeof spec.legend === 'string') for (const match of spec.legend.matchAll(/\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}/g)) keys.push(match[1]);
		if (typeof spec.query === 'string') {
			for (const match of spec.query.matchAll(/\b(?:by|without|on|ignoring|group_left|group_right)\s*\(([^)]*)\)/g)) {
				keys.push(...match[1].split(',').map((key) => key.trim().replace(/"/g, '')).filter(Boolean));
			}
			for (const match of spec.query.matchAll(/[{,]\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:=~|!~|!=|=)/g)) keys.push(match[1]);
		}
	}
	for (const group of json?.notificationSettings?.groupBy ?? []) keys.push(group);
	for (const text of Object.values(json?.annotations ?? {})) {
		for (const match of String(text).matchAll(/\{\{\s*\$([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}/g)) if (!['value', 'threshold'].includes(match[1])) keys.push(match[1]);
	}
	return keys;
}

/** The monitoring environment the chart allows (templates/validate.yaml): a plain name. */
const MONITORING_ENVIRONMENT = /^[A-Za-z0-9]([A-Za-z0-9._-]{0,61}[A-Za-z0-9])?$/;

/**
 * ARV-125b: every query of a rule or dashboard reads its own release only, so one SigNoz may take several environments and
 * clusters: an Ariva metric through a filter that starts with deployment.environment = '<environment>' (and holds no OR),
 * an external builder metric within k8s.namespace.name IN [...], and a PromQL query within a namespace or
 * exported_namespace matcher.
 */
function monitoringScopeFindings(json, where, environment) {
	const findings = [];
	const scope = `deployment.environment = '${environment}'`;
	for (const { type, spec } of monitoringQueries(json)) {
		if (type === 'builder_query') {
			const filter = String(spec.filter?.expression ?? '');
			for (const metric of (spec.aggregations ?? []).map((aggregation) => aggregation?.metricName).filter((name) => typeof name === 'string')) {
				if (metric.startsWith('ariva.')) {
					if (!(filter === scope || filter.startsWith(`${scope} AND `)) || /\bOR\b/i.test(filter)) findings.push(`${where}: reads ${metric} without the filter ${scope}`);
				} else if (!/\bk8s\.namespace\.name IN \['[a-z0-9-]+'(, '[a-z0-9-]+')*\]/.test(filter) || /\bOR\b/i.test(filter)) {
					findings.push(`${where}: reads ${metric} outside a namespace filter (k8s.namespace.name IN [...])`);
				}
			}
		} else if (type === 'promql' && !/\{(?:[^}]*,)?\s*(?:namespace|exported_namespace)=~?"[a-z0-9|-]+"/.test(String(spec.query ?? ''))) {
			findings.push(`${where}: a PromQL query reads no namespace (a namespace or exported_namespace matcher)`);
		}
	}
	return findings;
}

/** Keys that look like a credential or a secret wherever they appear in a monitoring object (CWE-532). */
const CREDENTIAL_KEY = /(password|passwd|secret|token|api.?key|authorization|credential|cookie)/i;

/**
 * ARV-125b: findings for one monitoring ConfigMap: every value parses as JSON; a rule is SigNoz v5 / v2alpha1, has a
 * severity (each threshold named warning or critical, and its severity label one of them), a runbook link into wiki 10 and a
 * resolved number for every threshold; a dashboard is SigNoz's v6 format with one query per panel; no token, no Helm
 * leftover, no credential; every label key on the allowlist and none naming a person, a credential or a document; every
 * query scoped to the release (monitoringScopeFindings).
 */
function monitoringFindings(doc, id) {
	const findings = [];
	const alerts = doc.metadata?.name === 'ariva-monitoring-alerts';
	const environment = doc.metadata?.annotations?.['ariva.dalil.tech/environment'];
	const scoped = MONITORING_ENVIRONMENT.test(String(environment ?? ''));
	if (!scoped) findings.push(`${id}: has no environment annotation (ariva.dalil.tech/environment, a plain name)`);
	for (const [key, text] of Object.entries(doc.data ?? {})) {
		const where = `${id} ${key}`;
		if (/\$\{|<no value>|\{\{\s*\./.test(String(text))) findings.push(`${where}: holds an unresolved token or a Helm leftover`);
		if (/SIGNOZ-API-KEY|:\/\/[^/\s"]*:[^/\s"]*@/i.test(String(text))) findings.push(`${where}: carries a credential (an API key header or a URL with a password)`);
		let json;
		try {
			json = JSON.parse(text);
		} catch (error) {
			findings.push(`${where}: is not valid JSON (${error.message})`);
			continue;
		}
		for (const node of walk(json)) {
			if (Array.isArray(node)) continue;
			for (const name of Object.keys(node)) if (CREDENTIAL_KEY.test(name)) findings.push(`${where}: field ${name} looks like a credential; monitoring objects carry none`);
		}
		if (scoped) findings.push(...monitoringScopeFindings(json, where, environment));
		for (const label of monitoringLabels(json)) {
			if (MONITORING_FORBIDDEN.test(label) && !MONITORING_EXCEPTIONS.has(label)) findings.push(`${where}: queries the label ${label}, which may hold a secret or a person identifier`);
			if (!MONITORING_LABELS.has(label)) findings.push(`${where}: queries the label ${label}, which is not on the allowlist (tests/monitoring.json)`);
		}
		if (alerts) {
			if (json.version !== 'v5' || json.schemaVersion !== 'v2alpha1') findings.push(`${where}: is not a SigNoz rule of version v5 and schemaVersion v2alpha1`);
			const thresholds = json.condition?.thresholds?.spec ?? [];
			if (thresholds.length === 0) findings.push(`${where}: has no threshold`);
			for (const threshold of thresholds) {
				if (!MONITORING.severities.includes(threshold.name)) findings.push(`${where}: threshold ${threshold.name} is not a severity (${MONITORING.severities.join(', ')})`);
				if (typeof threshold.target !== 'number' || !Number.isFinite(threshold.target)) findings.push(`${where}: a threshold has no resolved number`);
				if (!Array.isArray(threshold.channels)) findings.push(`${where}: a threshold's channels are not a list`);
			}
			if (json.labels?.severity !== undefined && !thresholds.some((threshold) => threshold.name === json.labels.severity)) {
				findings.push(`${where}: severity label ${json.labels.severity} is no threshold's name`);
			}
			if (!String(json.annotations?.runbook ?? '').startsWith(MONITORING.runbook)) findings.push(`${where}: has no runbook link into ${MONITORING.runbook}`);
			if (!/^\d+m$/.test(String(json.evaluation?.spec?.evalWindow ?? ''))) findings.push(`${where}: has no evaluation window in minutes`);
			if (json.condition?.alertOnAbsent && !(Number.isInteger(json.condition.absentFor) && json.condition.absentFor > 0)) {
				findings.push(`${where}: alerts on absent data without a resolved absentFor`);
			}
		} else {
			if (json.schemaVersion !== 'v6') findings.push(`${where}: is not a SigNoz dashboard of schemaVersion v6`);
			const panels = Object.entries(json.spec?.panels ?? {});
			if (panels.length === 0) findings.push(`${where}: has no panel`);
			for (const [panelId, panel] of panels) {
				if ((panel?.spec?.queries ?? []).length !== 1) findings.push(`${where}: panel ${panelId} needs exactly one query`);
			}
		}
	}
	return findings;
}

/** The rules off unless a border deployment turns them on (ARV-125b, ARV-259). */
export const BORDER_RULES = ['amanFeedStale', 'amanCodesUnmapped'];

/** ARV-182: the rules rendered with the scheduled backup (backup.enabled) only. */
export const BACKUP_RULES = ['backupFailed', 'backupMissing'];

/**
 * ARV-125b: the criterion rules a render lacks. Every rule of tests/monitoring.json must be a key of ariva-monitoring-alerts
 * (hostDown once per host, as hostDown-<host>), but for the border rules, which are off unless the render turns them on, and
 * the scheduled backup's rules (ARV-182), which are there exactly when the backup is on.
 */
export function missingMonitoringRules(docs, { border = false, backup = false } = {}) {
	const alerts = docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === 'ariva-monitoring-alerts');
	if (!alerts) return ['ConfigMap/ariva-monitoring-alerts: not rendered'];
	const keys = Object.keys(alerts.data ?? {}).map((key) => key.replace(/\.json$/, ''));
	const missing = MONITORING.criterionRules
		.filter((rule) => !BORDER_RULES.includes(rule) || border)
		.filter((rule) => !BACKUP_RULES.includes(rule) || backup)
		.filter((rule) => !keys.some((key) => key === rule || key.startsWith(`${rule}-`)))
		.map((rule) => `ConfigMap/ariva-monitoring-alerts: rule ${rule} is missing`);
	for (const rule of BORDER_RULES.filter((rule) => !border && keys.includes(rule)))
		missing.push(`ConfigMap/ariva-monitoring-alerts: ${rule} is rendered although it is off`);
	for (const rule of BACKUP_RULES.filter((rule) => !backup && keys.includes(rule)))
		missing.push(`ConfigMap/ariva-monitoring-alerts: ${rule} is rendered although the scheduled backup is off`);
	if (!docs.some((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === 'ariva-monitoring-dashboards')) missing.push('ConfigMap/ariva-monitoring-dashboards: not rendered');
	return missing;
}

/**
 * ARV-182: the scheduled backup's objects as the chart renders them (templates/backup.yaml): the CronJob, its ServiceAccount and
 * the ConfigMap of its scripts, the command it runs, its mounts and the age Jobs are kept, which the backupMissing rule reads.
 */
export const BACKUP = {
	cronJob: 'ariva-db-backup',
	serviceAccount: 'ariva-db-backup',
	configMap: 'ariva-db-backup-scripts',
	container: 'backup',
	command: ['bash', '/opt/ariva-backup/ariva-backup.sh'],
	user: 1000,
	scriptsPath: '/opt/ariva-backup',
	backupsPath: '/backups',
	caPath: '/app/secrets/database-ca',
	scripts: ['ariva-age.py', 'ariva-backup.sh', 'ariva-db.sh'],
	recipient: /^age1[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$/,
	/** The variables the container gets, and nothing else (the password from its Secret, the namespace from the pod). */
	env: ['ARIVA_BACKUP_NAMESPACE', 'ARIVA_BACKUP_RECIPIENT', 'ARIVA_BACKUP_KEEP', 'ARIVA_BACKUP_DIRECTORY', 'ARIVA_BACKUP_SCRIPTS',
		'ARIVA_DATABASE_HOST', 'ARIVA_DATABASE_PORT', 'POSTGRES_DB', 'ARIVA_MIGRATION_LOGIN', 'ARIVA_MIGRATION_PASSWORD', 'PGSSLMODE', 'PGSSLROOTCERT',
		'PGREQUIREAUTH', 'PGCHANNELBINDING']
};

/**
 * An age identity (a private key), wherever it appears: the cluster holds the custodian's public recipient only (ARV-182). The
 * whole shape (the prefix, 1 and 58 Bech32 characters), since the scripts name the prefix to refuse it.
 */
const AGE_IDENTITY = /AGE-SECRET-KEY-1[QPZRY9X8GF2TVDW0S3JN54KHCE6MUA7L]{58}/i;

/**
 * ARV-182 (CWE-269, CWE-532): findings for the scheduled backup of a render, against the values that made it: enabled, the
 * environment, backup (the merged backup values), maxAgeHours (monitoring.alerts.backupMissing.maxAgeHours), the database CA's
 * Secret name and scripts (the text each ConfigMap key must hold: files/backup's, and scripts/ops/ariva-db.sh for ariva-db.sh).
 * In every render no object holds an age identity. Off: no object of the backup and none of its rules. On:
 * - the CronJob reads its schedule in Etc/UTC, runs one at a time (Forbid), is not suspended, keeps a succeeded Job, retries
 *   nothing (backoffLimit 0), stops at activeDeadlineSeconds and keeps each finished Job maxAgeHours, the age backupMissing
 *   judges; its pods carry the app label the NetworkPolicies select;
 * - least privilege: its own ServiceAccount, without a token on the account or in the pod, bound by no RoleBinding or
 *   ClusterRoleBinding; no service links; the image's postgres user (runAsNonRoot, uid, gid and fsGroup 1000); one container
 *   running the backup script and nothing else (no init container, no argument, no privilege or added capability);
 * - secrets: the migration password from passwordSecret alone (secretKeyRef, not optional), no other variable from a Secret,
 *   no envFrom, only the variables the script reads; the recipient a public age recipient; no Secret volume but, with
 *   verify-full, the database CA (ca.crt only, read-only), which k8s-prd requires; libpq held to SCRAM-SHA-256
 *   (PGREQUIREAUTH) and, with verify-full, to channel binding (PGCHANNELBINDING require);
 * - its resources requests and limits of cpu, memory and ephemeral-storage only;
 * - the volumes /tmp, the scripts (read-only, the ConfigMap holding the three scripts and nothing else, each the repository's
 *   byte for byte) and the claim at /backups, nothing more.
 */
export function backupFindings(docs, { enabled, environment, backup = {}, maxAgeHours = 26, databaseCaSecretName = 'ariva-database-ca', scripts = {} }) {
	const findings = [];
	for (const doc of docs.filter(Boolean)) {
		if (AGE_IDENTITY.test(JSON.stringify(doc))) findings.push(`${doc.kind}/${doc.metadata?.name ?? '?'}: holds an age identity; the cluster holds the custodian's public recipient only`);
	}
	const own = docs.filter((doc) => doc && (doc.metadata?.labels?.app === BACKUP.cronJob || [BACKUP.cronJob, BACKUP.configMap].includes(doc.metadata?.name)));
	if (!enabled) {
		for (const doc of own) findings.push(`${doc.kind}/${doc.metadata?.name}: rendered although backup is off`);
		return findings;
	}
	const cronJobs = docs.filter((doc) => doc?.kind === 'CronJob' && doc.metadata?.name === BACKUP.cronJob);
	if (cronJobs.length !== 1) return [...findings, `CronJob/${BACKUP.cronJob}: rendered ${cronJobs.length} times, not once`];
	const id = `CronJob/${BACKUP.cronJob}`;
	const spec = cronJobs[0].spec ?? {};
	const job = spec.jobTemplate?.spec ?? {};
	const pod = job.template?.spec ?? {};
	if (spec.schedule !== backup.schedule) findings.push(`${id}: schedule is ${spec.schedule}, not backup.schedule (${backup.schedule})`);
	if (spec.timeZone !== 'Etc/UTC') findings.push(`${id}: must read its schedule in Etc/UTC (timeZone), not ${spec.timeZone ?? "the controller's zone"}`);
	if (spec.concurrencyPolicy !== 'Forbid') findings.push(`${id}: must run one backup at a time (concurrencyPolicy Forbid)`);
	if (spec.suspend === true) findings.push(`${id}: is suspended`);
	if (!(Number(spec.successfulJobsHistoryLimit) >= 1)) findings.push(`${id}: must keep a succeeded Job (successfulJobsHistoryLimit 1 or more): backupMissing reads them`);
	if (job.backoffLimit !== 0) findings.push(`${id}: must not retry a failed run (backoffLimit 0): a failure is a page`);
	if (job.activeDeadlineSeconds !== backup.activeDeadlineSeconds) findings.push(`${id}: activeDeadlineSeconds is ${job.activeDeadlineSeconds}, not backup.activeDeadlineSeconds (${backup.activeDeadlineSeconds})`);
	if (job.ttlSecondsAfterFinished !== maxAgeHours * 3600) {
		findings.push(`${id}: must keep each finished Job monitoring.alerts.backupMissing.maxAgeHours (ttlSecondsAfterFinished ${maxAgeHours * 3600}), the age backupMissing judges, not ${job.ttlSecondsAfterFinished}`);
	}
	if (job.template?.metadata?.labels?.app !== BACKUP.cronJob) findings.push(`${id}: its pods must carry app: ${BACKUP.cronJob}, which the NetworkPolicies select`);

	// Least privilege (CWE-269): its own ServiceAccount, no token anywhere, bound to nothing.
	if (pod.serviceAccountName !== BACKUP.serviceAccount) findings.push(`${id}: must run as its own ServiceAccount ${BACKUP.serviceAccount}, not ${pod.serviceAccountName ?? 'default'}`);
	if (pod.automountServiceAccountToken !== false) findings.push(`${id}: must set automountServiceAccountToken false: the backup calls no Kubernetes API`);
	if (pod.enableServiceLinks !== false) findings.push(`${id}: must set enableServiceLinks false`);
	if (pod.restartPolicy !== 'Never') findings.push(`${id}: restartPolicy must be Never`);
	const account = docs.find((doc) => doc?.kind === 'ServiceAccount' && doc.metadata?.name === BACKUP.serviceAccount);
	if (!account) findings.push(`ServiceAccount/${BACKUP.serviceAccount}: not rendered`);
	else if (account.automountServiceAccountToken !== false) findings.push(`ServiceAccount/${BACKUP.serviceAccount}: must set automountServiceAccountToken false`);
	for (const binding of docs.filter((doc) => ['RoleBinding', 'ClusterRoleBinding'].includes(doc?.kind))) {
		if ((binding.subjects ?? []).some((subject) => subject?.kind === 'ServiceAccount' && subject.name === BACKUP.serviceAccount)) {
			findings.push(`${binding.kind}/${binding.metadata?.name}: binds ${BACKUP.serviceAccount}, which needs no Kubernetes API right`);
		}
	}
	const psc = pod.securityContext ?? {};
	if (psc.runAsNonRoot !== true || psc.runAsUser !== BACKUP.user || psc.runAsGroup !== BACKUP.user || psc.fsGroup !== BACKUP.user) {
		findings.push(`${id}: must run as the image's postgres user (runAsNonRoot true, runAsUser, runAsGroup and fsGroup ${BACKUP.user})`);
	}
	if ((pod.initContainers ?? []).length) findings.push(`${id}: runs no init container`);
	const containers = pod.containers ?? [];
	if (containers.length !== 1 || containers[0]?.name !== BACKUP.container) return [...findings, `${id}: must run one container, ${BACKUP.container}`];
	const container = containers[0];
	const cid = `${id} container ${BACKUP.container}`;
	if (container.image !== backup.image) findings.push(`${cid}: runs ${container.image}, not backup.image`);
	if (JSON.stringify(container.command) !== JSON.stringify(BACKUP.command) || container.args !== undefined) findings.push(`${cid}: must run ${BACKUP.command.join(' ')} and nothing else`);
	if (container.securityContext?.privileged === true || (container.securityContext?.capabilities?.add ?? []).length) findings.push(`${cid}: adds a privilege or a capability`);

	// Secrets (CWE-532): the migration password from its Secret alone; the recipient public; no other secret in the pod.
	if ((container.envFrom ?? []).length) findings.push(`${cid}: takes no envFrom: every variable is named`);
	const env = Object.fromEntries((container.env ?? []).map((variable) => [variable.name, variable]));
	const unknown = Object.keys(env).filter((name) => !BACKUP.env.includes(name));
	if (unknown.length) findings.push(`${cid}: sets ${unknown.join(', ')}, which the backup does not read`);
	const password = env.ARIVA_MIGRATION_PASSWORD?.valueFrom?.secretKeyRef;
	const secret = backup.database?.passwordSecret ?? {};
	if (!password || password.name !== secret.name || password.key !== secret.key || password.optional === true || env.ARIVA_MIGRATION_PASSWORD.value !== undefined) {
		findings.push(`${cid}: ARIVA_MIGRATION_PASSWORD must come from backup.database.passwordSecret (${secret.name}, key ${secret.key}), not optional, never a value`);
	}
	for (const variable of container.env ?? []) {
		if (variable.valueFrom?.secretKeyRef && variable.name !== 'ARIVA_MIGRATION_PASSWORD') findings.push(`${cid}: ${variable.name} comes from a Secret; only the migration password does`);
	}
	const value = (name) => env[name]?.value;
	if (value('ARIVA_BACKUP_RECIPIENT') !== backup.recipient || !BACKUP.recipient.test(String(value('ARIVA_BACKUP_RECIPIENT') ?? ''))) {
		findings.push(`${cid}: ARIVA_BACKUP_RECIPIENT must be backup.recipient, the custodian's public age recipient (age1...)`);
	}
	if (value('ARIVA_BACKUP_KEEP') !== String(backup.keep)) findings.push(`${cid}: ARIVA_BACKUP_KEEP is ${value('ARIVA_BACKUP_KEEP')}, not backup.keep (${backup.keep})`);
	if (env.ARIVA_BACKUP_NAMESPACE?.valueFrom?.fieldRef?.fieldPath !== 'metadata.namespace') findings.push(`${cid}: ARIVA_BACKUP_NAMESPACE must be the pod's namespace (fieldRef metadata.namespace)`);
	if (value('ARIVA_BACKUP_DIRECTORY') !== BACKUP.backupsPath || value('ARIVA_BACKUP_SCRIPTS') !== BACKUP.scriptsPath) findings.push(`${cid}: must write to ${BACKUP.backupsPath} and run the scripts of ${BACKUP.scriptsPath}`);
	const database = backup.database ?? {};
	for (const [name, expected] of [['ARIVA_DATABASE_HOST', database.host], ['ARIVA_DATABASE_PORT', String(database.port)], ['POSTGRES_DB', database.name], ['ARIVA_MIGRATION_LOGIN', database.login]]) {
		if (value(name) !== expected) findings.push(`${cid}: ${name} is ${value(name)}, not ${expected} (backup.database)`);
	}
	if (value('ARIVA_MIGRATION_LOGIN') === 'postgres') findings.push(`${cid}: connects as the superuser postgres; the backup runs as the migration login`);
	const verify = database.sslMode === 'verify-full';
	if (value('PGSSLMODE') !== database.sslMode) findings.push(`${cid}: PGSSLMODE is ${value('PGSSLMODE')}, not backup.database.sslMode (${database.sslMode})`);
	if (environment === 'k8s-prd' && value('PGSSLMODE') !== 'verify-full') findings.push(`${cid}: k8s-prd verifies the database server (PGSSLMODE verify-full), not ${value('PGSSLMODE')}`);

	// The volumes: /tmp, the scripts, the claim and, with verify-full, the database CA alone.
	const volumes = Object.fromEntries((pod.volumes ?? []).map((volume) => [volume.name, volume]));
	const wanted = ['backups', 'scripts', 'tmp', ...(verify ? ['database-ca'] : [])].sort();
	if (JSON.stringify(Object.keys(volumes).sort()) !== JSON.stringify(wanted)) findings.push(`${id}: its volumes are ${Object.keys(volumes).sort().join(', ')}, not ${wanted.join(', ')}`);
	for (const volume of pod.volumes ?? []) {
		if ((volume.secret || volume.projected) && volume.name !== 'database-ca') findings.push(`${id}: mounts the Secret volume ${volume.name}; the backup reads no Secret file but the database CA (no identity, no signing key)`);
	}
	if (!volumes.tmp?.emptyDir) findings.push(`${id}: /tmp must be an emptyDir`);
	if (volumes.scripts?.configMap?.name !== BACKUP.configMap) findings.push(`${id}: the scripts must come from the ConfigMap ${BACKUP.configMap}`);
	if (volumes.backups?.persistentVolumeClaim?.claimName !== backup.claimName) findings.push(`${id}: the backups go to the claim ${volumes.backups?.persistentVolumeClaim?.claimName}, not backup.claimName (${backup.claimName})`);
	const mounts = Object.fromEntries((container.volumeMounts ?? []).map((mount) => [mount.name, mount]));
	if (mounts.scripts?.mountPath !== BACKUP.scriptsPath || mounts.scripts.readOnly !== true) findings.push(`${cid}: must mount its scripts read-only at ${BACKUP.scriptsPath}`);
	if (mounts.backups?.mountPath !== BACKUP.backupsPath) findings.push(`${cid}: must mount the backup claim at ${BACKUP.backupsPath}`);
	if (verify) {
		const ca = volumes['database-ca']?.secret;
		if (ca?.secretName !== databaseCaSecretName || (ca.items ?? []).length !== 1 || ca.items[0].key !== 'ca.crt' || ca.items[0].path !== 'ca.crt') {
			findings.push(`${id}: the database CA must come from ${databaseCaSecretName}, ca.crt only`);
		}
		const caMount = mounts['database-ca'];
		if (caMount?.mountPath !== BACKUP.caPath || caMount.readOnly !== true || caMount.subPath) findings.push(`${cid}: must mount the database CA read-only and whole at ${BACKUP.caPath}`);
		if (value('PGSSLROOTCERT') !== `${BACKUP.caPath}/ca.crt`) findings.push(`${cid}: PGSSLROOTCERT must be ${BACKUP.caPath}/ca.crt with verify-full`);
	} else if (env.PGSSLROOTCERT) {
		findings.push(`${cid}: sets PGSSLROOTCERT without verify-full`);
	}
	if (value('PGREQUIREAUTH') !== 'scram-sha-256') findings.push(`${cid}: PGREQUIREAUTH must be scram-sha-256, so libpq takes no other authentication method`);
	if (verify ? value('PGCHANNELBINDING') !== 'require' : env.PGCHANNELBINDING !== undefined) {
		findings.push(`${cid}: PGCHANNELBINDING must be require with verify-full${verify ? '' : ', and unset without it (no TLS channel to bind)'}`);
	}
	const resources = container.resources ?? {};
	if (Object.keys(resources).some((part) => !['requests', 'limits'].includes(part))
		|| Object.values(resources).some((amounts) => !amounts || typeof amounts !== 'object' || Object.keys(amounts).some((name) => !['cpu', 'memory', 'ephemeral-storage'].includes(name)))) {
		findings.push(`${cid}: its resources hold more than requests and limits of cpu, memory and ephemeral-storage`);
	}

	// The scripts: the repository's, byte for byte, and nothing else.
	const map = docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === BACKUP.configMap);
	if (!map) findings.push(`ConfigMap/${BACKUP.configMap}: not rendered`);
	else {
		if (JSON.stringify(Object.keys(map.data ?? {}).sort()) !== JSON.stringify(BACKUP.scripts) || map.binaryData) findings.push(`ConfigMap/${BACKUP.configMap}: must hold ${BACKUP.scripts.join(', ')} and nothing else`);
		for (const [key, text] of Object.entries(scripts)) {
			if (map.data?.[key] !== text) findings.push(`ConfigMap/${BACKUP.configMap}: ${key} is not the repository's (${key === 'ariva-db.sh' ? 'scripts/ops/ariva-db.sh' : `files/backup/${key}`})`);
		}
	}
	return findings;
}

/**
 * @param {any[]} docs parsed manifests
 * @param {{ environment: string, hosts?: boolean }} context hosts: true for a whole render (every API host and the simulator,
 *   when rendered, must export telemetry naming its pod, ARV-275); false for a fixture or a part of a render
 * @returns {string[]} findings, empty when every rule holds
 */
export function checkManifests(docs, { environment, hosts = false }) {
	const findings = [];
	for (const doc of docs) {
		if (!doc || typeof doc !== 'object') continue;
		const id = `${doc.kind}/${doc.metadata?.name ?? '?'}`;

		if (WORKLOADS.has(doc.kind)) {
			const pod = podSpecOf(doc) ?? {};
			const psc = pod.securityContext ?? {};
			if (psc.runAsNonRoot !== true) findings.push(`${id}: pod securityContext.runAsNonRoot must be true`);
			if (!(Number(psc.runAsUser) > 0)) findings.push(`${id}: pod securityContext.runAsUser must be a non-zero uid`);
			if (psc.seccompProfile?.type !== 'RuntimeDefault') findings.push(`${id}: pod seccompProfile must be RuntimeDefault`);
			for (const flag of ['hostNetwork', 'hostPID', 'hostIPC']) {
				if (pod[flag] === true) findings.push(`${id}: ${flag} is not allowed`);
			}
			// ARV-010a: only Ariva.Api.Main signs access tokens, so only its pod may mount the signing key; every API
			// pod validates tokens and needs the public keys.
			const secretVolumes = (pod.volumes ?? []).map((volume) => volume.secret?.secretName ?? '');
			const integrationRing = (name) => name.includes('integration-token');
			// Secret files are root-owned without fsGroup: a non-root pod cannot read a 0400 key file and fails to start (0400 parses
			// as 256 under YAML 1.1 and as 400 under YAML 1.2).
			const ownerOnly = (pod.volumes ?? []).some((volume) => volume.secret && [256, 400, '0400'].includes(volume.secret.defaultMode));
			if (ownerOnly && psc.runAsNonRoot === true && !(Number(psc.fsGroup) > 0)) {
				findings.push(`${id}: a non-root pod mounting 0400 secrets needs securityContext.fsGroup`);
			}
			if (secretVolumes.some((name) => name.includes('token-signing') && !integrationRing(name)) && doc.metadata?.name !== 'api-main-deployment') {
				findings.push(`${id}: only api-main-deployment may mount the token signing key`);
			}
			// ARV-042: Ariva.Api.Integration signs integration tokens with a ring of its own; it alone mounts it, and must.
			if (secretVolumes.some((name) => name.includes('integration-token-signing')) && doc.metadata?.name !== 'api-integration-deployment') {
				findings.push(`${id}: only api-integration-deployment may mount the integration token signing key`);
			}
			if (doc.metadata?.name === 'api-integration-deployment' &&
				!(secretVolumes.some((name) => name.includes('integration-token-signing')) && secretVolumes.some((name) => name.includes('integration-token-public')))) {
				findings.push(`${id}: api-integration-deployment must mount the integration token key ring (signing and public)`);
			}
			if (/^api-[a-z]+-deployment$/.test(doc.metadata?.name ?? '') && !secretVolumes.some((name) => name.includes('token-public'))) {
				findings.push(`${id}: API pods must mount the token public keys`);
			}
			// ARV-211 (the review's finding L5): the key ring's signing key (ariva-dataprotection-signing, and a previous signer's
			// certificate during a rotation) reaches the API pods alone, owner-only and read-only, and a previous signer's volume
			// projects its tls.crt alone (a previous signer only verifies).
			const signingVolumes = (pod.volumes ?? []).filter((volume) => (volume.secret?.secretName ?? '').includes('dataprotection-signing') ||
				(volume.name ?? '').startsWith('dataprotection-signing'));
			if (signingVolumes.length && !/^api-[a-z]+-deployment$/.test(doc.metadata?.name ?? '')) {
				findings.push(`${id}: only the API deployments may mount the key ring signing key`);
			}
			for (const volume of signingVolumes) {
				if (![256, 400, '0400'].includes(volume.secret?.defaultMode)) findings.push(`${id}: the key ring signing volume ${volume.name} must be owner-only (defaultMode 0400)`);
				const mounts = [...(pod.containers ?? []), ...(pod.initContainers ?? [])].flatMap((container) => (container.volumeMounts ?? []).filter((mount) => mount.name === volume.name));
				if (mounts.some((mount) => mount.readOnly !== true)) findings.push(`${id}: the key ring signing volume ${volume.name} must be mounted read-only`);
				if (volume.name === 'dataprotection-signing-previous' && JSON.stringify((volume.secret?.items ?? []).map((item) => [item.key, item.path])) !== JSON.stringify([['tls.crt', 'tls.crt']])) {
					findings.push(`${id}: a previous key ring signer's volume must project its tls.crt alone (a previous signer only verifies)`);
				}
			}
			if (doc.kind === 'Deployment' && /^api-[a-z]+-deployment$/.test(doc.metadata?.name ?? '')) {
				const grace = Number(pod.terminationGracePeriodSeconds);
				if (!(grace >= MIN_GRACE_SECONDS)) {
					findings.push(`${id}: an API pod needs terminationGracePeriodSeconds of at least ${MIN_GRACE_SECONDS} (the hosts' stop budget, ARV-165)`);
				} else {
					for (const container of (pod.containers ?? []).filter((c) => DOTNET_IMAGES.has(imageName(c.image ?? '')))) {
						if (String(envValueOf(container, docs, 'Hosting__GracePeriodSeconds')) !== String(grace)) {
							findings.push(`${id} container ${container.name}: must set Hosting__GracePeriodSeconds to the pod's terminationGracePeriodSeconds (${grace})`);
						}
					}
				}
				// ARV-176: the host's start holds its web server while its consumers wait for their topics and readiness, so it
				// is told its startup probe's allowance and refuses waits that would not fit. The allowance is the probe's worst
				// case: the kubelet's prober ticks every periodSeconds from before the container starts and skips the ticks
				// within initialDelaySeconds of its start, so the first counted tick can come at initialDelaySeconds and the
				// failureThreshold-th failure, which restarts the container, periodSeconds times (failureThreshold - 1) later
				// (a refused connection fails at once, so the probe's timeout adds nothing).
				for (const container of (pod.containers ?? []).filter((c) => DOTNET_IMAGES.has(imageName(c.image ?? '')))) {
					const probe = container.startupProbe;
					if (!probe) {
						findings.push(`${id} container ${container.name}: an API host needs a startupProbe (the hosts' start budget, ARV-176)`);
						continue;
					}
					const allowance = startupAllowance(probe);
					if (String(envValueOf(container, docs, 'Hosting__StartupAllowanceSeconds')) !== String(allowance)) {
						findings.push(`${id} container ${container.name}: must set Hosting__StartupAllowanceSeconds to its startup probe's allowance at its worst, initialDelaySeconds plus periodSeconds times (failureThreshold - 1) (${allowance})`);
					}
				}
			}
			const containers = [...(pod.initContainers ?? []), ...(pod.containers ?? [])];
			if (containers.length === 0) findings.push(`${id}: no containers`);
			for (const container of containers) {
				const cid = `${id} container ${container.name}`;
				const sc = container.securityContext ?? {};
				if (sc.allowPrivilegeEscalation !== false) findings.push(`${cid}: allowPrivilegeEscalation must be false`);
				if (sc.privileged === true) findings.push(`${cid}: privileged is not allowed`);
				if (!(sc.capabilities?.drop ?? []).includes('ALL')) findings.push(`${cid}: capabilities.drop must include ALL`);
				if (sc.readOnlyRootFilesystem !== true) findings.push(`${cid}: readOnlyRootFilesystem must be true`);
				if (!(container.volumeMounts ?? []).some((mount) => mount.mountPath === '/tmp')) {
					findings.push(`${cid}: needs a writable /tmp mount (emptyDir) because the root filesystem is read-only`);
				}
				// ARV-098: a .NET image ships no environment file, so a host without DOTNET_ENVIRONMENT refuses to start; every
				// one must resolve the release's environment, from its own env or a ConfigMap it loads, and no --environment
				// argument or Secret may override it.
				if (DOTNET_IMAGES.has(imageName(container.image ?? '')) && dotnetEnvironmentOf(container, docs) !== environment) {
					findings.push(`${cid}: must set DOTNET_ENVIRONMENT to ${environment} (env or envFrom ConfigMap, no --environment argument)`);
				}
				const tag = imageTag(container.image ?? '');
				if (!tag) findings.push(`${cid}: image ${container.image} has no tag`);
				if (tag === 'latest') findings.push(`${cid}: image tag latest is not allowed`);
				if (environment === 'k8s-prd' && (tag === 'trunk' || tag === '')) findings.push(`${cid}: production must pin a build number, not ${tag || 'an empty tag'}`);
				if (!ARIVA_IMAGES.has(imageName(container.image ?? '')) && !/@sha256:[0-9a-f]{64}$/.test(container.image ?? '')) {
					findings.push(`${cid}: third-party image ${container.image} must be pinned by digest`);
				}
				for (const variable of container.env ?? []) {
					if (SECRET_ENV.test(variable.name ?? '') && variable.value !== undefined && variable.value !== '') {
						findings.push(`${cid}: ${variable.name} carries a literal value; credentials come from a secret (valueFrom)`);
					}
					// ARV-085 (Platform/Cloud/CLAUDE.md): it clears KnownProxies and KnownIPNetworks and trusts X-Forwarded-* from anyone.
					if (/^ASPNETCORE_FORWARDEDHEADERS_ENABLED$/i.test(variable.name ?? '')) {
						findings.push(`${cid}: ${variable.name} is set; it trusts X-Forwarded-For from any peer (Security:ForwardedHeaders names the ingress controller's network)`);
					}
				}
			}
			findings.push(...readerLoginFindings(doc, id, pod, environment));
			// ARV-082a: the hosts that verify PostgreSQL and Redis in k8s-prd find the servers' CAs where they look.
			if (isServerCaWorkload(id)) findings.push(...serverCaFindings(id, pod));
			// ARV-062: a stateful workload (the database) only accepts traffic a NetworkPolicy allows.
			if (doc.kind === 'StatefulSet') {
				const podLabels = doc.spec?.template?.metadata?.labels ?? {};
				const guarded = docs.some((policy) => policy?.kind === 'NetworkPolicy' && (policy.spec?.policyTypes ?? ['Ingress']).includes('Ingress') &&
					Object.keys(policy.spec?.podSelector?.matchLabels ?? {}).length > 0 && labelsMatch(policy.spec.podSelector, podLabels));
				if (!guarded) findings.push(`${id}: a StatefulSet needs a NetworkPolicy that selects its pods`);
				if (pod.automountServiceAccountToken !== false) findings.push(`${id}: a StatefulSet does not call the Kubernetes API; set automountServiceAccountToken false`);
				// A PostgreSQL server (the official entrypoint's variables): initdb's default is trust on the socket and on
				// loopback, which a port-forward reaches; peer on the socket and scram-sha-256 on every TCP connection instead.
				for (const container of pod.containers ?? []) {
					const env = Object.fromEntries((container.env ?? []).map((variable) => [variable.name, variable]));
					if (!env.POSTGRES_PASSWORD) continue;
					const initdb = String(env.POSTGRES_INITDB_ARGS?.value ?? '');
					if (!initdb.includes('--auth-local=peer') || !initdb.includes('--auth-host=scram-sha-256')) {
						findings.push(`${id} container ${container.name}: PostgreSQL needs POSTGRES_INITDB_ARGS with --auth-local=peer and --auth-host=scram-sha-256 (no password-free login)`);
					}
					if (env.POSTGRES_HOST_AUTH_METHOD) findings.push(`${id} container ${container.name}: POSTGRES_HOST_AUTH_METHOD must not be set; every TCP login uses scram-sha-256`);
				}
			}
		}

		// ARV-125b (CWE-269, CWE-532): the monitoring objects are ConfigMaps of rules and dashboards, and nothing more.
		if (doc.metadata?.labels?.['app.kubernetes.io/component'] === 'monitoring' || MONITORING_CONFIG_MAPS.has(doc.metadata?.name)) {
			if (doc.kind !== 'ConfigMap') findings.push(`${id}: a monitoring object must be a ConfigMap (no Job, Secret, ServiceAccount or RBAC)`);
			else findings.push(...monitoringFindings(doc, id));
		}

		// ARV-062: a script that creates a database login never makes it a superuser.
		if (doc.kind === 'ConfigMap') {
			for (const [key, value] of Object.entries(doc.data ?? {})) {
				if (/CREATE ROLE/i.test(String(value)) && !/NOSUPERUSER/.test(String(value))) {
					findings.push(`${id}: ${key} creates a role without NOSUPERUSER`);
				}
			}
		}

		// ARV-062: with the topics Job, the hosts never create topics, so their Kafka principal needs no create rights.
		if (doc.kind === 'Job' && doc.metadata?.name === 'kafka-topics') {
			const mainConfig = docs.find((other) => other?.kind === 'ConfigMap' && other.metadata?.name === 'api-main-configmap');
			if (mainConfig && mainConfig.data?.Kafka__ProvisionTopics !== 'false') {
				findings.push(`${id}: with the topics job, api-main-configmap must set Kafka__ProvisionTopics "false"`);
			}
		}

		if (doc.kind === 'Ingress') {
			const tls = doc.spec?.tls ?? [];
			if (tls.length === 0) findings.push(`${id}: ingress needs a tls section`);
			const tlsHosts = new Set(tls.flatMap((entry) => entry.hosts ?? []));
			for (const rule of doc.spec?.rules ?? []) {
				if (rule.host && !tlsHosts.has(rule.host)) findings.push(`${id}: host ${rule.host} is not covered by tls`);
			}
			for (const entry of tls) {
				if (!entry.secretName) findings.push(`${id}: tls entry without secretName`);
			}
			const annotations = doc.metadata?.annotations ?? {};
			// ARV-091 (ASVS 5.0 V16.2.5, CWE-532): the ModSecurity audit log records the request line and headers (bearer tokens,
			// the refresh cookie) and, with part C, the body (sign-in passwords), so every Ingress turns it off: its
			// modsecurity-snippet's last SecAuditEngine is Off (the last one wins in a location) and no rule turns it on per request
			// (ctl:auditEngine).
			const modsecurity = String(annotations['nginx.ingress.kubernetes.io/modsecurity-snippet'] ?? '');
			const engines = [...modsecurity.matchAll(/^\s*SecAuditEngine\s+(\S+)/gim)].map((match) => match[1]);
			const ctl = [...modsecurity.matchAll(/ctl:\s*auditEngine\s*=\s*([A-Za-z]+)/gi)].map((match) => match[1]).filter((value) => !/^off$/i.test(value));
			if (!engines.length || !/^off$/i.test(engines[engines.length - 1]) || ctl.length) {
				findings.push(`${id}: the ingress must turn the ModSecurity audit log off (its modsecurity-snippet ends with SecAuditEngine Off, ` +
					`not ${engines.length ? engines.join(' then ') : 'unset'}${ctl.length ? `, and no rule sets ctl:auditEngine=${ctl.join(', ')}` : ''}): it records the request line and headers ` +
					'(bearer tokens, the refresh cookie) and, with part C, the body (passwords)');
			}
			// ModSecurity's debug log writes the same request values at its higher levels, to the file it names (stdout included).
			if (/^\s*SecDebugLog/im.test(modsecurity)) findings.push(`${id}: the modsecurity-snippet sets a ModSecurity debug log (SecDebugLog...), which writes request headers and variables`);
			// The SignalR hubs take the access token in the URL (ARV-035, CWE-598): an ingress that serves /hubs keeps no
			// access log and only critical errors (and its audit log off, as every Ingress above), and serves nothing else.
			const paths = (doc.spec?.rules ?? []).flatMap((rule) => (rule.http?.paths ?? []).map((entry) => entry.path));
			if (paths.some((value) => value === '/hubs' || value?.startsWith('/hubs/'))) {
				if (annotations['nginx.ingress.kubernetes.io/enable-access-log'] !== 'false') findings.push(`${id}: /hubs carries tokens in its URL; the ingress must disable the access log`);
				// ARV-091: the ingress's OpenTelemetry module exports the request target with its query string (http.target).
				if (annotations['nginx.ingress.kubernetes.io/enable-opentelemetry'] !== 'false') findings.push(`${id}: /hubs carries tokens in its URL; the ingress must keep OpenTelemetry off (enable-opentelemetry "false"), whose spans carry the request target with its query string`);
				if (!/error_log\s+\S+\s+crit;/.test(annotations['nginx.ingress.kubernetes.io/configuration-snippet'] ?? '')) findings.push(`${id}: /hubs carries tokens in its URL; the ingress must log only critical errors`);
				if (paths.some((value) => !(value === '/hubs' || value?.startsWith('/hubs/')))) findings.push(`${id}: /hubs carries tokens in its URL; serve it from its own ingress`);
			}
		}
	}
	// ARV-275: every container that exports telemetry names its pod as service.instance.id (with hosts, a whole render: every host does).
	findings.push(...instanceIdFindings(docs, { hosts }));
	// ARV-083a: each host's database login from its own secret, and the migration job alone with them all.
	findings.push(...hostLoginFindings(docs, { hosts }));
	// ARV-083b: each host's Kafka principal and Redis user from its own secrets, and the migration password the job's alone.
	findings.push(...hostCredentialFindings(docs, { environment, hosts }));
	return findings;
}

/** ARV-275: the resource attribute that tells the pods of one workload apart, and the downward API variable it is made from. */
const TELEMETRY_SETTING = /^(OTEL_|Otlp(__|:))/i;

export const INSTANCE_ID = { attribute: 'service.instance.id', variable: 'K8S_POD_NAME', fieldPath: 'metadata.name' };

/**
 * ARV-275 (ASVS 5.0 V16.2.1): the replicas of a host export indistinguishable series unless each names itself, so a gauge every
 * replica reports for its own share (ariva.stream.zones_unknown, ariva.stream.consumer_lag) reads as interleaved samples rather than
 * their sum. Every container of a workload that exports (OTEL_EXPORTER_OTLP_ENDPOINT, or Otlp__Enabled or Otlp__Endpoint with a
 * double underscore or a colon, names compared ignoring case as .NET reads them) or has OTEL_RESOURCE_ATTRIBUTES:
 * - gets OTEL_RESOURCE_ATTRIBUTES once, a literal value whose pairs carry service.instance.id=$(K8S_POD_NAME) once and no other
 *   service.instance.id, and k8s.pod.name, when given, as $(K8S_POD_NAME);
 * - gets K8S_POD_NAME once, from the pod's name (fieldRef metadata.name), declared before OTEL_RESOURCE_ATTRIBUTES, and every
 *   other $(NAME) the attributes refer to is a downward API variable (fieldRef) declared before them (Kubernetes expands $(NAME)
 *   only from a variable declared earlier in the list; a later one leaves the text as written, exported as it stands; $$(NAME)
 *   is the escape for the literal text and refers to nothing).
 * No ConfigMap holds a key of OpenTelemetry's or Ariva's telemetry settings (OTEL_*, Otlp__* or Otlp:*), and no envFrom adds a
 * prefix that would make its keys one: envFrom would make them variables these rules do not see, and a case variant of one
 * beside the chart's could replace it in .NET's configuration.
 * With hosts, the .NET containers (not the init containers) of each of the five API host Deployments and of the simulator, when
 * rendered, must have them.
 */
export function instanceIdFindings(docs, { hosts = false } = {}) {
	const findings = [];
	const reference = `$(${INSTANCE_ID.variable})`;
	for (const doc of docs) {
		if (!doc || typeof doc !== 'object') continue;
		const id = `${doc.kind}/${doc.metadata?.name ?? '?'}`;
		if (doc.kind === 'ConfigMap') {
			for (const key of Object.keys(doc.data ?? {}).filter((candidate) => TELEMETRY_SETTING.test(candidate))) {
				findings.push(`${id}: ${key} is a telemetry setting; it reaches a workload in its env only, where the chart checks that it names its pod (envFrom would add it unchecked)`);
			}
			continue;
		}
		if (!WORKLOADS.has(doc.kind)) continue;
		const name = doc.metadata?.name ?? '';
		const pod = podSpecOf(doc) ?? {};
		const host = hosts && doc.kind === 'Deployment' && [...API_HOSTS, 'simulation'].some((candidate) => name === `${candidate}-deployment`);
		const main = new Set(pod.containers ?? []);
		for (const container of [...(pod.initContainers ?? []), ...(pod.containers ?? [])]) {
			const cid = `${id} container ${container.name}`;
			const env = container.env ?? [];
			const attributes = env.filter((variable) => /^OTEL_RESOURCE_ATTRIBUTES$/i.test(variable.name ?? ''));
			const exports = env.some((variable) => /^(OTEL_EXPORTER_OTLP_ENDPOINT|Otlp(__|:)(Enabled|Endpoint))$/i.test(variable.name ?? ''));
			for (const source of (container.envFrom ?? []).filter((candidate) => TELEMETRY_SETTING.test(candidate.prefix ?? ''))) {
				findings.push(`${cid}: envFrom with the prefix ${source.prefix} makes its keys telemetry settings these rules do not see; give them in env`);
			}
			const dotnet = DOTNET_IMAGES.has(imageName(container.image ?? ''));
			if (!attributes.length && !exports) {
				if (host && dotnet && main.has(container)) findings.push(`${cid}: exports no telemetry naming its pod (no OTEL_RESOURCE_ATTRIBUTES with ${INSTANCE_ID.attribute}=${reference})`);
				continue;
			}
			if (attributes.length !== 1 || attributes[0].valueFrom || typeof attributes[0].value !== 'string') {
				findings.push(`${cid}: must get OTEL_RESOURCE_ATTRIBUTES once, a literal value, not ${attributes.length} times${attributes.some((variable) => variable.valueFrom) ? ' (one from valueFrom)' : ''}`);
				continue;
			}
			const pairs = attributes[0].value.split(',').map((pair) => pair.trim()).filter(Boolean);
			const ids = pairs.filter((pair) => pair.split('=')[0].trim() === INSTANCE_ID.attribute);
			if (ids.length !== 1 || ids[0] !== `${INSTANCE_ID.attribute}=${reference}`) {
				findings.push(`${cid}: OTEL_RESOURCE_ATTRIBUTES must carry ${INSTANCE_ID.attribute}=${reference} once, the pod's name, not ${ids.length ? ids.join(', ') : 'none'}`);
			}
			const podNames = pairs.filter((pair) => pair.split('=')[0].trim() === 'k8s.pod.name');
			if (podNames.some((pair) => pair !== `k8s.pod.name=${reference}`)) {
				findings.push(`${cid}: OTEL_RESOURCE_ATTRIBUTES must give k8s.pod.name as ${reference}, the pod's name, not ${podNames.join(', ')}`);
			}
			const pods = env.filter((variable) => variable.name === INSTANCE_ID.variable);
			if (pods.length !== 1 || pods[0].value !== undefined || pods[0].valueFrom?.fieldRef?.fieldPath !== INSTANCE_ID.fieldPath) {
				findings.push(`${cid}: must get ${INSTANCE_ID.variable} once, from the pod's name (fieldRef ${INSTANCE_ID.fieldPath}), not ${pods.length} times or another source`);
			} else if (env.indexOf(pods[0]) > env.indexOf(attributes[0])) {
				findings.push(`${cid}: ${INSTANCE_ID.variable} must be declared before OTEL_RESOURCE_ATTRIBUTES, or Kubernetes leaves ${reference} unexpanded`);
			}
			// Every other reference: a downward API variable declared before the attributes, or its text is exported as written.
			const before = env.slice(0, env.indexOf(attributes[0]));
			for (const [, referenced] of attributes[0].value.matchAll(/(?<!\$)\$\(([A-Za-z_][A-Za-z0-9_]*)\)/g)) {
				if (referenced === INSTANCE_ID.variable) continue;
				if (!before.some((variable) => variable.name === referenced && variable.valueFrom?.fieldRef?.fieldPath)) {
					findings.push(`${cid}: OTEL_RESOURCE_ATTRIBUTES refers to $(${referenced}), which is not a downward API variable declared before it`);
				}
			}
		}
	}
	return findings;
}

/**
 * ARV-084: the response headers, plain HTTP and cookies of every Ingress, and the controller values the site applies.
 * HSTS is one year with includeSubDomains, no preload (ASVS 5.0 V3.4.1); the TLS 1.2 suites are Mozilla's intermediate list
 * (Server Side TLS guidelines 6.0), the ECDHE and AEAD policy of every Ariva TLS server (TLS12_CIPHERS) spelled out.
 */
export const INGRESS_HARDENING = {
	hstsMaxAge: 31536000,
	mozillaIntermediateTls12: [
		'ECDHE-ECDSA-AES128-GCM-SHA256', 'ECDHE-RSA-AES128-GCM-SHA256', 'ECDHE-ECDSA-AES256-GCM-SHA384',
		'ECDHE-RSA-AES256-GCM-SHA384', 'ECDHE-ECDSA-CHACHA20-POLY1305', 'ECDHE-RSA-CHACHA20-POLY1305'
	],
	plainHttpAnnotation: 'ariva.dalil.tech/plain-http',
	/** The machine services, whose Ingresses refuse plain HTTP in k8s-prd on every host, the web host's /api and /hubs included (ARV-199). */
	machineServices: ['api-main-service', 'api-ingest-service', 'api-integration-service'],
	/** The affinity cookie's name prefix (ARV-199, ASVS 5.0 V3.3.1): kept by a browser only from its own host, with no Domain. */
	cookiePrefix: '__Host-'
};

/**
 * An nginx snippet as a tree of statements ({ name, args, block }), as nginx's own reader splits it: words and quoted strings
 * (quotes removed, escapes \" \' \\ resolved, nothing else) separated by blanks; ";" ends a statement, "{" opens its block and
 * "}" closes it; "#" starts a comment outside quotes; a ")" right after a closing quote is a word of its own. Throws on an
 * unbalanced block or an unterminated string.
 */
export function nginxTree(text) {
	const tokens = [];
	const source = String(text ?? '');
	let i = 0;
	while (i < source.length) {
		const ch = source[i];
		if (/\s/.test(ch)) { i += 1; continue; }
		if (ch === '#') { while (i < source.length && source[i] !== '\n') i += 1; continue; }
		if (ch === ';' || ch === '{' || ch === '}') { tokens.push({ punct: ch }); i += 1; continue; }
		if (ch === '"' || ch === "'") {
			let value = '';
			i += 1;
			while (i < source.length && source[i] !== ch) {
				if (source[i] === '\\' && ['"', "'", '\\'].includes(source[i + 1])) { value += source[i + 1]; i += 2; continue; }
				value += source[i];
				i += 1;
			}
			if (i >= source.length) throw new Error('unterminated string');
			i += 1;
			tokens.push({ word: value, quoted: true });
			if (source[i] === ')') { tokens.push({ word: ')' }); i += 1; }
			continue;
		}
		let word = '';
		while (i < source.length && !/[\s;{}]/.test(source[i]) && !(word === '' && (source[i] === '"' || source[i] === "'"))) { word += source[i]; i += 1; }
		tokens.push({ word });
	}
	let position = 0;
	const readBlock = (nested) => {
		const statements = [];
		let words = [];
		while (position < tokens.length) {
			const token = tokens[position++];
			if (token.punct === ';') { if (words.length) statements.push({ name: words[0].word, args: words.slice(1) }); words = []; continue; }
			if (token.punct === '{') { const block = readBlock(true); statements.push({ name: words[0]?.word ?? '', args: words.slice(1), block }); words = []; continue; }
			if (token.punct === '}') { if (!nested || words.length) throw new Error('unbalanced }'); return statements; }
			words.push(token);
		}
		if (nested || words.length) throw new Error('unterminated statement or block');
		return statements;
	};
	return readBlock(false);
}

/**
 * The condition of an nginx if statement as words, without its parentheses: if ($scheme = https) gives [$scheme, =, https].
 * A quoted word keeps every character (if ($uri ~* "^/a(/|$)") gives [$uri, ~*, ^/a(/|$)]): only bare parentheses go.
 */
function ifCondition(statement) {
	const args = statement.args;
	const closedApart = args.some((arg) => !arg.quoted && arg.word === ')');
	const words = args.filter((arg) => arg.quoted || (arg.word !== ')' && arg.word !== '(')).map((arg) => ({ ...arg }));
	if (words.length) {
		if (!words[0].quoted) words[0].word = words[0].word.replace(/^\(/, '');
		const last = words[words.length - 1];
		if (!closedApart && !last.quoted) last.word = last.word.replace(/\)$/, '');
	}
	return words.map((arg) => arg.word);
}

/** Whether an nginx regex condition (~, ~*, !~, !~*) holds for a value; undefined for another operator or a regex JS cannot read. */
function nginxRegexHolds(operator, pattern, value) {
	if (!['~', '~*', '!~', '!~*'].includes(operator)) return undefined;
	let regex;
	try {
		regex = new RegExp(pattern, operator.endsWith('*') ? 'i' : '');
	} catch {
		return undefined;
	}
	return operator.startsWith('!') ? !regex.test(value) : regex.test(value);
}

/** A header directive of a snippet tree (top level only): { kind: more_set_headers or add_header, value, always }. */
function headerDirectives(tree, header) {
	const found = [];
	const name = header.toLowerCase();
	for (const statement of tree) {
		if (statement.block) continue;
		if (statement.name === 'more_set_headers') {
			for (const arg of statement.args.filter((candidate) => !candidate.word.startsWith('-'))) {
				const match = /^([^:]+):\s*(.*)$/.exec(arg.word);
				if (match && match[1].trim().toLowerCase() === name) found.push({ kind: 'more_set_headers', value: match[2].trim() });
			}
		}
		if (statement.name === 'add_header' && statement.args[0]?.word.toLowerCase() === name) {
			found.push({ kind: 'add_header', value: statement.args[1]?.word ?? '', always: statement.args.slice(2).some((arg) => arg.word === 'always') });
		}
	}
	return found;
}

/**
 * The value a server-level header directive takes on one path through the server's rewrite statements: the set and if
 * statements of the snippet run in order for the given scheme, and the host either is the server's or is not (the 421 path);
 * a return ends the path. A $uri regex condition is judged on the given normalised path (default /). Unknown conditions are
 * not taken. Returns { value, returned }.
 */
function serverValueOnPath(tree, value, { scheme, otherHost, uri = '/' }) {
	const variables = {};
	let returned;
	const run = (statements) => {
		for (const statement of statements) {
			if (returned !== undefined) return;
			if (statement.name === 'set' && !statement.block) variables[statement.args[0]?.word] = statement.args[1]?.word ?? '';
			else if (statement.name === 'return' && !statement.block) returned = statement.args[0]?.word ?? '';
			else if (statement.name === 'if' && statement.block) {
				const [left, operator, right] = ifCondition(statement);
				let taken = false;
				if (left === '$scheme' && operator === '=') taken = right === scheme;
				else if (left === '$scheme' && operator === '!=') taken = right !== scheme;
				else if (left === '$https' && ifCondition(statement).length === 1) taken = scheme === 'https';
				else if (left === '$host' && operator === '!=') taken = otherHost;
				else if (left === '$host' && operator === '=') taken = !otherHost;
				else if (left === '$uri' && operator === '=') taken = right === uri;
				else if (left === '$uri' && operator === '!=') taken = right !== uri;
				else if (left === '$uri') taken = nginxRegexHolds(operator, right, uri) ?? false;
				if (taken) run(statement.block);
			}
		}
	};
	run(tree);
	const resolved = String(value).replace(/\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?/g, (_, name) => variables[`$${name}`] ?? '');
	return { value: resolved, returned };
}

/** Findings for an HSTS value: one year at least, includeSubDomains, no preload. */
function hstsValueFindings(value, where) {
	const findings = [];
	const parts = String(value).split(';').map((part) => part.trim()).filter(Boolean);
	const maxAge = parts.map((part) => /^max-age\s*=\s*"?(\d+)"?$/i.exec(part)).find(Boolean);
	if (!maxAge) findings.push(`${where}: Strict-Transport-Security has no max-age (${value})`);
	else if (Number(maxAge[1]) < INGRESS_HARDENING.hstsMaxAge) findings.push(`${where}: Strict-Transport-Security max-age ${maxAge[1]} is under a year (${INGRESS_HARDENING.hstsMaxAge})`);
	if (!parts.some((part) => /^includesubdomains$/i.test(part))) findings.push(`${where}: Strict-Transport-Security without includeSubDomains`);
	if (parts.some((part) => /^preload$/i.test(part))) findings.push(`${where}: Strict-Transport-Security with preload (the story keeps hosts off the preload list)`);
	return findings;
}

const NGINX = 'nginx.ingress.kubernetes.io/';
/** ARV-084, ARV-204: the directives the ingress's own policy names, each 'none' (ASVS 5.0 V3.4.3: object-src and base-uri on every policy). */
const INGRESS_POLICY = new Map([
	['frame-ancestors', /(^|;)\s*frame-ancestors\s+'none'\s*(;|$)/],
	['object-src', /(^|;)\s*object-src\s+'none'\s*(;|$)/],
	['base-uri', /(^|;)\s*base-uri\s+'none'\s*(;|$)/]
]);
const namesNone = (value, name) => INGRESS_POLICY.get(name).test(value);
const fullPolicy = (value) => [...INGRESS_POLICY.keys()].every((name) => namesNone(value, name));

/**
 * ARV-084: findings for the Ingresses of a render (docs parsed as Kubernetes reads them). For every Ingress:
 * - its server-snippet (one per host: every Ingress of a host carries the same) sets HSTS with more_set_headers, so it applies
 *   to every status and replaces the controller's own: over HTTPS max-age of a year at least with includeSubDomains and no
 *   preload, on the 421 for other hosts as well (set before it), and nothing over plain HTTP; nosniff with more_set_headers;
 *   Content-Security-Policy with frame-ancestors 'none', object-src 'none' and base-uri 'none' through add_header always (it adds
 *   to the pods' own policy; ARV-204);
 * - its configuration-snippet repeats that add_header (nginx drops the server's in a location with an add_header of its own);
 * - plain HTTP per ariva.dalil.tech/plain-http: refuse has ssl-redirect and force-ssl-redirect "false" and a 4xx for
 *   if ($scheme = http) in the configuration-snippet; redirect has both "true" and no refusal; ingress-nginx applies the mode
 *   per location, so the Ingresses of a host may differ, but two that route one path of it agree (ARV-199); in k8s-prd an
 *   Ingress to a machine service refuses, whatever its host;
 * - an affinity cookie is named __Host-... in letters, digits, - and _ (a conservative subset of what ingress-nginx's
 *   annotation validation takes), with no Domain, Secure, SameSite=Strict, Path=/ (ARV-199);
 *   ssl-prefer-server-ciphers "false"; no from-to-www-redirect, no
 *   custom-headers, no more_clear_headers, and no other annotation names the three headers.
 */
export function ingressHardeningFindings(docs, { environment }) {
	const findings = [];
	const ingresses = docs.filter((doc) => doc?.kind === 'Ingress');
	const hostsOf = (ingress) => [...new Set((ingress.spec?.rules ?? []).map((rule) => rule.host).filter(Boolean))];
	const servicesOf = (ingress) => [...new Set((ingress.spec?.rules ?? []).flatMap((rule) => (rule.http?.paths ?? []).map((entry) => entry.backend?.service?.name)).filter(Boolean))];
	/** Each host and location the Ingress routes, as "host path": /api and /api/ are one Prefix location, so the trailing slash goes. */
	const location = (path) => (path.length > 1 ? path.replace(/\/+$/, '') || '/' : path);
	const routesOf = (ingress) => [...new Set((ingress.spec?.rules ?? []).filter((rule) => rule.host).flatMap((rule) => (rule.http?.paths ?? []).map((entry) => `${rule.host} ${location(entry.path ?? '/')}`)))];
	const modes = new Map();
	const serverSnippets = new Map();
	for (const ingress of ingresses) {
		const id = `Ingress/${ingress.metadata?.name}`;
		const annotations = ingress.metadata?.annotations ?? {};
		const get = (name) => annotations[`${NGINX}${name}`];
		const parse = (name) => {
			try {
				return nginxTree(get(name) ?? '');
			} catch (error) {
				findings.push(`${id}: ${name} is not valid nginx (${error.message})`);
				return [];
			}
		};
		const server = parse('server-snippet');
		const location = parse('configuration-snippet');

		// HSTS: one more_set_headers at the server level, a year with includeSubDomains over HTTPS on every path, none over HTTP.
		const hsts = headerDirectives(server, 'Strict-Transport-Security');
		const hstsSet = hsts.filter((directive) => directive.kind === 'more_set_headers');
		if (hsts.some((directive) => directive.kind === 'add_header')) findings.push(`${id}: server-snippet sets Strict-Transport-Security with add_header, which comes after the controller's own HSTS (two headers, the controller's first); use more_set_headers`);
		else if (hstsSet.length === 0) findings.push(`${id}: server-snippet sends no Strict-Transport-Security (more_set_headers at the server level)`);
		else if (hstsSet.length > 1) findings.push(`${id}: server-snippet sets Strict-Transport-Security more than once`);
		else {
			const https = serverValueOnPath(server, hstsSet[0].value, { scheme: 'https', otherHost: false });
			const misdirected = serverValueOnPath(server, hstsSet[0].value, { scheme: 'https', otherHost: true });
			const plain = serverValueOnPath(server, hstsSet[0].value, { scheme: 'http', otherHost: false });
			if (!https.value) findings.push(`${id}: server-snippet sends no Strict-Transport-Security over HTTPS`);
			else findings.push(...hstsValueFindings(https.value, `${id}: server-snippet`));
			if (https.value && !misdirected.value) findings.push(`${id}: server-snippet answers ${misdirected.returned ?? 'the 421'} for other hosts before it sets Strict-Transport-Security`);
			if (plain.value) findings.push(`${id}: server-snippet sends Strict-Transport-Security over plain HTTP (RFC 6797 section 7.2: only over HTTPS)`);
		}

		// nosniff: one more_set_headers at the server level (it replaces the pods' own instead of repeating it).
		const nosniff = headerDirectives(server, 'X-Content-Type-Options');
		if (!nosniff.some((directive) => directive.kind === 'more_set_headers' && directive.value.toLowerCase() === 'nosniff')) {
			findings.push(`${id}: server-snippet sends no X-Content-Type-Options: nosniff with more_set_headers (every status, one header)`);
		}

		// frame-ancestors, object-src and base-uri 'none': add_header always at the server level and again in the location.
		for (const [tree, where, why] of [[server, 'server-snippet', 'the server level (the 421 and every location without an add_header)'],
			[location, 'configuration-snippet', 'the location (an add_header there drops the server\'s)']]) {
			const policies = headerDirectives(tree, 'Content-Security-Policy');
			if (policies.some((directive) => directive.kind === 'more_set_headers')) findings.push(`${id}: ${where} replaces the pods' Content-Security-Policy with more_set_headers; add frame-ancestors, object-src and base-uri 'none' with add_header always`);
			const added = policies.filter((directive) => directive.kind === 'add_header');
			const missing = [...INGRESS_POLICY.keys()].filter((name) => !added.some((directive) => namesNone(directive.value, name)));
			if (added.length === 0) findings.push(`${id}: ${where} adds no Content-Security-Policy frame-ancestors 'none' at ${why}`);
			else if (missing.length) findings.push(`${id}: ${where} adds a Content-Security-Policy without ${missing.map((name) => `${name} 'none'`).join(', ')}`);
			else if (!added.some((directive) => fullPolicy(directive.value))) findings.push(`${id}: ${where} adds frame-ancestors, object-src and base-uri in separate policies; name the three in one add_header`);
			else if (!added.some((directive) => fullPolicy(directive.value) && directive.always)) findings.push(`${id}: ${where} adds Content-Security-Policy without always, so 4xx and 5xx responses lack it`);
		}

		// Plain HTTP.
		const mode = annotations[INGRESS_HARDENING.plainHttpAnnotation];
		const refusal = location.filter((statement) => statement.name === 'if' && statement.block && ifCondition(statement).join(' ') === '$scheme = http')
			.flatMap((statement) => statement.block.filter((inner) => inner.name === 'return').map((inner) => Number(inner.args[0]?.word)));
		if (!['refuse', 'redirect'].includes(mode)) findings.push(`${id}: ${INGRESS_HARDENING.plainHttpAnnotation} is ${mode ?? 'unset'}, neither refuse nor redirect`);
		else if (mode === 'refuse') {
			if (get('ssl-redirect') !== 'false') findings.push(`${id}: refuses plain HTTP but ssl-redirect is ${get('ssl-redirect') ?? 'unset (the controller redirects)'}, not "false"`);
			if (get('force-ssl-redirect') !== 'false') findings.push(`${id}: refuses plain HTTP but force-ssl-redirect is ${get('force-ssl-redirect') ?? 'unset'}, not "false"`);
			if (refusal.length === 0) findings.push(`${id}: refuses plain HTTP without answering it (configuration-snippet: if ($scheme = http) { return 403; })`);
			else if (refusal.some((code) => !(code >= 400 && code < 500))) findings.push(`${id}: answers plain HTTP with ${refusal.join(', ')}, not an error (4xx): a machine host never redirects`);
		} else {
			if (get('ssl-redirect') !== 'true' || get('force-ssl-redirect') !== 'true') findings.push(`${id}: redirects plain HTTP but ssl-redirect and force-ssl-redirect are not both "true"`);
			if (refusal.length) findings.push(`${id}: redirects plain HTTP but its configuration-snippet refuses it`);
		}
		const machine = servicesOf(ingress).some((service) => INGRESS_HARDENING.machineServices.includes(service));
		if (environment === 'k8s-prd' && machine && mode === 'redirect') findings.push(`${id}: k8s-prd redirects plain HTTP to a machine service; it must refuse it, on the web host too (ASVS 5.0 V4.1.2)`);
		for (const route of routesOf(ingress)) {
			const [host, path] = [route.slice(0, route.indexOf(' ')), route.slice(route.indexOf(' ') + 1)];
			if (modes.has(route) && modes.get(route) !== mode) findings.push(`${id}: host ${host} has Ingresses that disagree on plain HTTP for ${path} (${modes.get(route)} and ${mode})`);
			if (!modes.has(route)) modes.set(route, mode);
		}
		for (const host of hostsOf(ingress)) {
			if (serverSnippets.has(host) && serverSnippets.get(host) !== (get('server-snippet') ?? '')) findings.push(`${id}: host ${host} has Ingresses with different server-snippets; ingress-nginx applies one per host`);
			if (!serverSnippets.has(host)) serverSnippets.set(host, get('server-snippet') ?? '');
		}

		// The affinity cookie, the cipher order and the annotations that would get around the headers.
		if (get('affinity') === 'cookie') {
			if (!/^[A-Za-z0-9_-]+$/.test(get('session-cookie-name') ?? 'INGRESSCOOKIE')) findings.push(`${id}: the affinity cookie's name ${get('session-cookie-name')} holds characters outside letters, digits, - and _ (a conservative subset of what ingress-nginx's annotation validation takes; a refused name would reject the Ingress)`);
			if (!String(get('session-cookie-name') ?? '').startsWith(INGRESS_HARDENING.cookiePrefix)) findings.push(`${id}: the affinity cookie ${get('session-cookie-name') ?? '(unset: INGRESSCOOKIE)'} has no ${INGRESS_HARDENING.cookiePrefix} prefix (session-cookie-name), so another host of the domain could set or replace it`);
			if (get('session-cookie-domain') !== undefined) findings.push(`${id}: the affinity cookie names a Domain (session-cookie-domain), which a ${INGRESS_HARDENING.cookiePrefix} cookie must not, or browsers drop it`);
			if (get('session-cookie-secure') !== 'true') findings.push(`${id}: the affinity cookie is not Secure (session-cookie-secure "true")`);
			if (get('session-cookie-samesite') !== 'Strict') findings.push(`${id}: the affinity cookie is not SameSite=Strict (session-cookie-samesite), but ${get('session-cookie-samesite') ?? 'unset'}`);
			if (get('session-cookie-path') !== '/') findings.push(`${id}: the affinity cookie's path is ${get('session-cookie-path') ?? 'the location\'s'}, not / (session-cookie-path)`);
		}
		if (get('ssl-prefer-server-ciphers') !== 'false') findings.push(`${id}: ssl-prefer-server-ciphers is ${get('ssl-prefer-server-ciphers') ?? 'unset (the controller\'s on)'}, not "false" (Mozilla intermediate: the client's order)`);
		if (get('from-to-www-redirect') === 'true') findings.push(`${id}: from-to-www-redirect generates a www server that carries none of the chart's headers`);
		if (get('custom-headers') !== undefined) findings.push(`${id}: custom-headers can replace the chart's headers from a ConfigMap the chart cannot read`);
		for (const [key, value] of Object.entries(annotations)) {
			if (/more_clear_headers/i.test(String(value))) findings.push(`${id}: ${key} clears response headers (more_clear_headers)`);
			else if (![`${NGINX}server-snippet`, `${NGINX}configuration-snippet`].includes(key) && /strict-transport-security|x-content-type-options|content-security-policy/i.test(String(value))) {
				findings.push(`${id}: ${key} names one of the chart's response headers`);
			}
		}
		if (headerDirectives(location, 'Strict-Transport-Security').length || headerDirectives(location, 'X-Content-Type-Options').length) {
			findings.push(`${id}: configuration-snippet sets Strict-Transport-Security or X-Content-Type-Options in the location, over the server's`);
		}
	}
	return findings;
}

/**
 * ARV-091, ARV-206, ARV-284, ARV-288: the controller's own snippets (its ConfigMap keys), which every snippet rule of
 * controllerValuesFindings reads: the main, http, server and location snippets, the global auth location's and, since
 * ARV-288, the stream block's (stream-snippet: an include or a preread_by_lua_block there was not read before).
 */
export const CONTROLLER_SNIPPETS = ['main-snippet', 'http-snippet', 'server-snippet', 'location-snippet', 'global-auth-snippet', 'stream-snippet'];

/**
 * ARV-091 (ASVS 5.0 V16.2.5, CWE-532): the controller's access log format. controllerDefault is ingress-nginx 1.15's
 * log-format-upstream (internal/ingress/controller/config/config.go at controller-v1.15.0), whose "$request" is the request
 * line with its query string; Ariva's values write requestLine in its place and keep every other field. No format may write
 * a variable of forbidden (the query string, the body, the Authorization header, the cookies) or one starting with a prefix
 * of forbiddenPrefixes (one query argument or cookie). nginx variable names are case-insensitive.
 */
export const INGRESS_LOG_FORMAT = {
	controllerDefault: '$remote_addr - $remote_user [$time_local] "$request" $status $body_bytes_sent "$http_referer" "$http_user_agent" $request_length $request_time [$proxy_upstream_name] [$proxy_alternative_upstream_name] $upstream_addr $upstream_response_length $upstream_response_time $upstream_status $req_id',
	requestLine: '$request_method $uri $server_protocol',
	forbidden: ['request', 'request_uri', 'args', 'query_string', 'request_body', 'http_authorization', 'http_cookie'],
	forbiddenPrefixes: ['arg_', 'cookie_'],
	/** error-log-level values that write no request headers (info and debug do; ingress-nginx's nginx is built with debug). */
	errorLevels: ['notice', 'warn', 'error', 'crit', 'alert', 'emerg'],
	/** The controller's own snippets, in which no log or ModSecurity directive may appear (they would get around the guards). */
	snippets: CONTROLLER_SNIPPETS,
	/** A log or ModSecurity directive, read as nginx's tokenizer reads a statement (nginxDirective). */
	directive: nginxDirective('access_log|log_format|error_log|rewrite_log|modsecurity(_[a-z_]+)?')
};

/**
 * ARV-284: an nginx directive whose name is one of the alternation given, read as nginx's tokenizer reads a statement: at the
 * start of the text or a line, or after ";", "{", "}" or "'", then spaces, then the name bare (followed by a space, ";", "{" or
 * the end of the text) or quoted with " or ' (followed by a space, ";", "{", ")" or the end: nginx takes a quoted name followed
 * directly by ")" as the name and the start of the next argument). Case is ignored. validate.yaml's ariva-platform.directive
 * (Helm/Charts/platform/templates/_helpers.tpl) is the same pattern for the Ingresses' annotation values.
 */
export function nginxDirective(names) {
	return new RegExp(`(^|[;{}'])\\s*(?:"(${names})"(?:[\\s;{)]|$)|'(${names})'(?:[\\s;{)]|$)|(${names})(?:[\\s;{]|$))`, 'im');
}

/** The directive name an nginxDirective match found: the first group after the statement's start that matched. */
export const directiveName = (match) => match.slice(2).find(Boolean);

/**
 * ARV-206: the name of an nginx directive that keeps a copy of an answer (a cache zone, a cache, or the answer stored as a file),
 * for the proxy, FastCGI, uwsgi and SCGI modules, anywhere in the text as a whole name: so however nginx's tokenizer reads the
 * snippet (a quoted name followed by ")", which nginx takes as the start of the next argument, included), and in a comment or a
 * quoted argument too, and "proxy_cache off" as well. Directives that keep answers out of a cache (proxy_no_cache,
 * proxy_cache_bypass) and the cache's settings, which do nothing without a cache (proxy_cache_valid), are other names and do not
 * match. validate.yaml holds the same pattern for the Ingresses' annotation values.
 */
export const INGRESS_CACHE_DIRECTIVE = /\b((proxy|fastcgi|uwsgi|scgi)_(cache|cache_path|store))\b/i;

/**
 * ARV-284, ARV-288: what no snippet writes, in the controller's snippets (controllerValuesFindings) and in the rendered Ingresses'
 * annotations (ingressPrivacyFindings) alike, as [name, pattern, why, story]: an include (read as nginx's tokenizer reads a
 * statement, nginxDirective), and client_body_in_file_only and a *_by_lua directive anywhere in the text. validate.yaml refuses
 * the same in every site annotation, in every environment. A name left undefined is reported as the directive nginxDirective
 * found (directiveName).
 */
export const SNIPPET_REFUSED_DIRECTIVES = [
	['include', nginxDirective('include'), 'it reads a file on the controller\'s disk into the configuration, which gets around every directive guard', 'ARV-284'],
	['client_body_in_file_only', /client_body_in_file_only/i, 'it keeps every request body (sign-in passwords, TOTP codes, uploads) as a file on the controller', 'ARV-284'],
	['a *_by_lua directive', /_by_lua/i, 'a Lua block can copy request bodies and headers into the controller\'s shared dictionaries or files', 'ARV-284']
];

/**
 * ARV-284, ARV-288: what none of the controller's snippets (CONTROLLER_SNIPPETS) writes: SNIPPET_REFUSED_DIRECTIVES, a rewrite,
 * and, each read as nginx's tokenizer reads a statement and in any snippet (nginx takes load_module and ssl_engine in the main
 * context only, lua_package_path and lua_package_cpath in http only; more is refused, never less): load_module and ssl_engine
 * (a shared object of the controller's disk loaded into nginx, a module or an OpenSSL engine), lua_package_path and
 * lua_package_cpath (the controller's Lua loads modules from other paths), and every js_ and perl directive (njs and nginx's
 * perl module load scripts from the controller's disk and run them on the requests, if its nginx carries them: js_import,
 * js_content, js_set, perl, perl_require, perl_modules and the others).
 */
export const CONTROLLER_REFUSED_DIRECTIVES = [
	...SNIPPET_REFUSED_DIRECTIVES,
	['rewrite', nginxDirective('rewrite'), 'it changes the path after the chart\'s health probe check, so another path could reach /health on a pod', 'ARV-284'],
	[undefined, nginxDirective('load_module|ssl_engine'), 'it loads a shared object of the controller\'s disk into nginx (a module or an OpenSSL engine), code no guard here reads', 'ARV-288'],
	[undefined, nginxDirective('lua_package_path|lua_package_cpath'), 'it makes the controller\'s Lua load its modules (Lua or C) from other paths of its disk, code no guard here reads', 'ARV-288'],
	[undefined, nginxDirective('js_[a-z0-9_]+'), 'njs loads JavaScript from the controller\'s disk and runs it on the requests (bodies and headers included), code no guard here reads', 'ARV-288'],
	[undefined, nginxDirective('perl(_[a-z_]+)?'), 'nginx\'s perl module loads Perl from the controller\'s disk and runs it on the requests (bodies and headers included), code no guard here reads', 'ARV-288']
];

/**
 * ARV-288: what no rendered Ingress's annotation writes beside a rewrite (ingressPrivacyFindings): SNIPPET_REFUSED_DIRECTIVES and
 * rewrite_log, read as nginx's tokenizer reads a statement. A second line behind validate.yaml, which refuses them in every site
 * annotation, and the proof that the chart's own snippets write none of them.
 */
export const INGRESS_REFUSED_DIRECTIVES = [
	...SNIPPET_REFUSED_DIRECTIVES,
	['rewrite_log', nginxDirective('rewrite_log'), 'it logs the rewrite checks of its server or location (the chart\'s server-level ones run on every request) at notice, with the request line and its query string (the live hub\'s access token)', 'ARV-288']
];

/** Every refused directive of a table (SNIPPET_REFUSED_DIRECTIVES and the others) that a text writes, as [name, why, story] (ARV-288). */
export function refusedDirectives(table, text) {
	const found = [];
	for (const [name, pattern, why, story] of table) {
		const match = pattern.exec(String(text ?? ''));
		if (match) found.push([name ?? directiveName(match), why, story]);
	}
	return found;
}

/** A ConfigMap boolean as ingress-nginx reads it (mapstructure, weakly typed: strconv.ParseBool). */
const configTrue = (value) => /^(1|t|true)$/i.test(String(value ?? ''));

/**
 * ARV-084: findings for the ingress-nginx controller values the site applies (Helm/ingress-nginx-values.yaml, parsed): snippet
 * annotations allowed from the chart value only, at the Critical risk level; TLS 1.2 and 1.3; Mozilla's intermediate TLS 1.2
 * suites exactly; no session tickets; HSTS of a year with includeSubDomains and no preload; X-Forwarded-* not trusted; every
 * ConfigMap value a string. ARV-091: ModSecurity off for the whole controller (on, its http level loads modsecurity.conf, whose
 * audit settings libmodsecurity lets override every Ingress's SecAuditEngine Off), and an access log format that writes no
 * query string, body, Authorization header or cookie (INGRESS_LOG_FORMAT), given explicitly (the default writes $request);
 * error-log-level unset or notice and above, nginx's log escaping kept (no log-format-escape-none), the controller's
 * OpenTelemetry off (its spans carry the query string), and no access_log, log_format, error_log or modsecurity directive in
 * the controller's main, http, server or location snippet. ARV-206: nor the name of a directive that keeps a copy of an answer
 * (INGRESS_CACHE_DIRECTIVE: proxy_cache_path in the http-snippet, proxy_cache, proxy_store, and the FastCGI, uwsgi and SCGI ones)
 * in those snippets or the global auth snippet, and no global-auth-cache-key (ingress-nginx would cache the global external
 * authentication answers in its auth_cache zone). ARV-284: the log and ModSecurity names are read as nginx's tokenizer reads a
 * statement (nginxDirective), and no snippet writes an include, client_body_in_file_only, a *_by_lua directive or a rewrite
 * (CONTROLLER_REFUSED_DIRECTIVES). ARV-288: every snippet rule reads the stream-snippet too (CONTROLLER_SNIPPETS); no snippet
 * writes load_module, ssl_engine, lua_package_path, lua_package_cpath or a js_ or perl directive (CONTROLLER_REFUSED_DIRECTIVES);
 * and no plugins key (Lua plugins from the controller's disk) but empty.
 */
export function controllerValuesFindings(values) {
	const findings = [];
	const controller = values?.controller ?? {};
	const config = controller.config ?? {};
	if (controller.allowSnippetAnnotations !== true) findings.push('controller.allowSnippetAnnotations must be true: the chart\'s Ingresses carry snippet annotations');
	if (Object.hasOwn(config, 'allow-snippet-annotations')) findings.push('controller.config must not set allow-snippet-annotations: the ingress-nginx chart writes it from controller.allowSnippetAnnotations, so the key would appear twice');
	if (config['annotations-risk-level'] !== 'Critical') findings.push(`controller.config annotations-risk-level is ${config['annotations-risk-level'] ?? 'unset (High)'}, not Critical: the controller would refuse the chart's snippets`);
	const protocols = String(config['ssl-protocols'] ?? '').split(/\s+/).filter(Boolean);
	if (protocols.length !== 2 || !protocols.includes('TLSv1.2') || !protocols.includes('TLSv1.3')) findings.push(`controller.config ssl-protocols is ${config['ssl-protocols'] ?? 'unset'}, not TLSv1.2 TLSv1.3`);
	if (config['ssl-ciphers'] !== INGRESS_HARDENING.mozillaIntermediateTls12.join(':')) findings.push(`controller.config ssl-ciphers is not the Mozilla intermediate TLS 1.2 list (${INGRESS_HARDENING.mozillaIntermediateTls12.join(':')})`);
	if (config['ssl-session-tickets'] !== 'false') findings.push(`controller.config ssl-session-tickets is ${config['ssl-session-tickets'] ?? 'unset'}, not "false"`);
	if (config.hsts !== 'true') findings.push(`controller.config hsts is ${config.hsts ?? 'unset'}, not "true"`);
	if (!(Number(config['hsts-max-age']) >= INGRESS_HARDENING.hstsMaxAge)) findings.push(`controller.config hsts-max-age is ${config['hsts-max-age'] ?? 'unset'}, under a year (${INGRESS_HARDENING.hstsMaxAge})`);
	if (config['hsts-include-subdomains'] !== 'true') findings.push(`controller.config hsts-include-subdomains is ${config['hsts-include-subdomains'] ?? 'unset'}, not "true"`);
	if (config['hsts-preload'] !== 'false') findings.push(`controller.config hsts-preload is ${config['hsts-preload'] ?? 'unset'}, not "false"`);
	if (config['use-forwarded-headers'] !== 'false') findings.push(`controller.config use-forwarded-headers is ${config['use-forwarded-headers'] ?? 'unset'}, not "false": TLS ends at the controller`);
	if (String(config['enable-modsecurity']) !== 'false') {
		findings.push(`controller.config enable-modsecurity is ${config['enable-modsecurity'] ?? 'unset'}, not "false": on, the controller loads modsecurity.conf at the http level ` +
			'(SecAuditEngine RelevantOnly, parts ABIJDEFHZ), and libmodsecurity lets that level\'s audit settings override every Ariva Ingress\'s SecAuditEngine Off');
	}
	const format = config['log-format-upstream'];
	if (format === undefined) findings.push('controller.config log-format-upstream is unset: the controller\'s default writes $request, the request line with its query string');
	else {
		const written = [...new Set([...String(format).matchAll(/\$\{?([A-Za-z0-9_]+)\}?/g)].map((match) => match[1].toLowerCase()))];
		const refused = written.filter((name) => INGRESS_LOG_FORMAT.forbidden.includes(name) || INGRESS_LOG_FORMAT.forbiddenPrefixes.some((prefix) => name.startsWith(prefix)));
		if (refused.length) {
			findings.push(`controller.config log-format-upstream writes ${refused.map((name) => `$${name}`).join(', ')}: a query string (the live hub's access_token), ` +
				`the body, the Authorization header or a cookie; write the request line as "${INGRESS_LOG_FORMAT.requestLine}"`);
		}
	}
	if (config['error-log-level'] !== undefined && !INGRESS_LOG_FORMAT.errorLevels.includes(String(config['error-log-level']))) {
		findings.push(`controller.config error-log-level is ${config['error-log-level']}, not one of ${INGRESS_LOG_FORMAT.errorLevels.join(', ')} (or unset): ` +
			'info writes every ModSecurity match with the matched input, and debug every request header and the request line');
	}
	if (configTrue(config['log-format-escape-none'])) findings.push(`controller.config log-format-escape-none is ${config['log-format-escape-none']}: it turns off nginx's log escaping, so a request could write control characters and forged lines into the access log`);
	if (configTrue(config['enable-opentelemetry'])) {
		findings.push(`controller.config enable-opentelemetry is ${config['enable-opentelemetry']}: the controller's OpenTelemetry module would export a span for every request with its target and query string (http.target), ` +
			'personal data from every host; the Ariva hosts trace themselves with query values redacted');
	}
	for (const key of INGRESS_LOG_FORMAT.snippets) {
		const match = INGRESS_LOG_FORMAT.directive.exec(String(config[key] ?? ''));
		if (match) findings.push(`controller.config ${key} writes ${directiveName(match)}: the controller's snippets write no access_log, log_format, error_log, rewrite_log or modsecurity directive, which would get around the access log format and the Ingresses' audit log and logging guards`);
	}
	for (const key of CONTROLLER_SNIPPETS) {
		const cache = INGRESS_CACHE_DIRECTIVE.exec(String(config[key] ?? ''));
		if (cache) {
			findings.push(`controller.config ${key} writes ${cache[1]}: the controller keeps no copy of an answer (no cache zone, cache or stored answer), as Ariva's answers carry credentials, ` +
				'border data and personal data that no cache outside Ariva may hold (ARV-206)');
		}
	}
	for (const key of CONTROLLER_SNIPPETS) {
		for (const [name, why, story] of refusedDirectives(CONTROLLER_REFUSED_DIRECTIVES, config[key])) findings.push(`controller.config ${key} writes ${name}: ${why} (${story})`);
	}
	// ARV-288: ingress-nginx loads each Lua plugin this key names from the controller's disk (/etc/nginx/lua/plugins) and runs it in
	// every request's phases, on every Ingress; any value but empty is refused, spaces included, as for global-auth-cache-key.
	if (String(config.plugins ?? '') !== '') {
		findings.push('controller.config plugins is set: ingress-nginx loads the Lua plugins it names from the controller\'s disk (/etc/nginx/lua/plugins) and runs them on every request of every Ingress, ' +
			'code no guard here reads (ARV-288)');
	}
	// Any value but empty, spaces included: ingress-nginx tests the string as it is, so " " would cache with one key per location.
	if (String(config['global-auth-cache-key'] ?? '') !== '') {
		findings.push('controller.config global-auth-cache-key is set: ingress-nginx would cache the global external authentication answers (proxy_cache auth_cache) on the controller\'s disk; ' +
			'Ariva uses no external authentication and the controller keeps no copy of an answer (ARV-206)');
	}
	for (const [key, value] of Object.entries(config)) if (typeof value !== 'string') findings.push(`controller.config ${key} must be a string (a ConfigMap value), not ${typeof value} ${value}`);
	return findings;
}

// ---------------------------------------------------------------------------------------------
// ARV-085: network policies, trusted proxies and private probes

/**
 * A CIDR as the chart takes it (validate.yaml, ariva-platform.cidrRefusal): an IPv4 address in dotted decimal without leading
 * zeros, or an IPv6 address in hexadecimal with at most one :: (no zone, no embedded IPv4), then its prefix. Returns
 * { version, prefix, bits (the address as a BigInt), canonical } (canonical: no bit set after the prefix), or undefined when it
 * is not a CIDR.
 */
export function parseCidr(text) {
	const match = /^([^/]+)\/(\d{1,3})$/.exec(String(text ?? ''));
	if (!match) return undefined;
	const [, address, prefixText] = match;
	const prefix = Number(prefixText);
	if (/^\d{1,3}(\.\d{1,3}){3}$/.test(address)) {
		const parts = address.split('.');
		if (prefix > 32 || parts.some((part) => Number(part) > 255 || String(Number(part)) !== part)) return undefined;
		const bits = parts.reduce((value, part) => (value << 8n) | BigInt(part), 0n);
		return { version: 4, prefix, bits, canonical: (bits & ((1n << BigInt(32 - prefix)) - 1n)) === 0n };
	}
	if (!/^[0-9a-f:]+$/i.test(address) || prefix > 128) return undefined;
	const halves = address.split('::');
	if (halves.length > 2) return undefined;
	const groups = (part) => (part === '' ? [] : part.split(':'));
	const left = groups(halves[0]);
	const right = halves.length === 2 ? groups(halves[1]) : [];
	if ([...left, ...right].some((group) => !/^[0-9a-f]{1,4}$/i.test(group))) return undefined;
	if (halves.length === 1 ? left.length !== 8 : left.length + right.length > 7) return undefined;
	const all = [...left, ...Array(8 - left.length - right.length).fill('0'), ...right];
	const bits = all.reduce((value, group) => (value << 16n) | BigInt(parseInt(group, 16)), 0n);
	return { version: 6, prefix, bits, canonical: (bits & ((1n << BigInt(128 - prefix)) - 1n)) === 0n };
	}

	/**
	 * ARV-085: whether an IPv6 trusted proxy network trusts IPv4 clients wider than an IPv4 /14, as the hosts judge it
	 * (ForwardedHeadersExtensions.TrustsMappedClients, k8s-prd): a dual-stack pod sees an IPv4 client as ::ffff:a.b.c.d, and .NET
	 * 10's IPNetwork.Contains matches it with the network's last 32 bits under a mask of (prefix mod 32) bits, 32 when that is 0;
	 * a mask under 14 (prefixes /65 to /77 and /97 to /109 at /48 or longer) is refused, and so is a network of /96 or wider that
	 * holds the IPv4-mapped block ::ffff:0:0/96. False for IPv4 and for what is not a CIDR.
	 */
	export function trustsMappedClients(text) {
		const cidr = parseCidr(text);
		if (cidr?.version !== 6) return false;
		const mask = cidr.prefix % 32 === 0 ? 32 : cidr.prefix % 32;
		if (mask < NETWORK_PREFIXES.proxies[4]) return true;
		const all = (1n << 128n) - 1n;
		const kept = cidr.prefix === 0 ? 0n : all ^ ((1n << BigInt(128 - cidr.prefix)) - 1n);
		return cidr.prefix <= 96 && ((0xffffn << 32n) & kept) === cidr.bits;
	}

/**
 * The shortest prefixes k8s-prd allows (validate.yaml): the trusted proxy networks (Security:ForwardedHeaders:KnownNetworks),
 * and every other network the chart opens (NetworkPolicy ipBlocks, the Ingresses' source ranges). Elsewhere a trusted proxy
 * network is never /0, and the others are free.
 */
export const NETWORK_PREFIXES = { proxies: { 4: 14, 6: 48 }, open: { 4: 8, 6: 16 }, anyProxy: { 4: 1, 6: 1 } };

/** Findings for one CIDR: not a CIDR, not a network address, or wider than the shortest prefixes given (by IP version). */
function cidrFindings(text, where, widest) {
	const cidr = parseCidr(text);
	if (!cidr) return [`${where}: ${text} is not a CIDR`];
	if (!cidr.canonical) return [`${where}: ${text} is not a network address (bits set after its prefix)`];
	if (widest && cidr.prefix < widest[cidr.version]) return [`${where}: ${text} is wider than /${widest[cidr.version]}`];
	return [];
}

/** The Ariva pods (by their app label) the NetworkPolicies isolate, and the flows the firewall matrix (wiki/05) gives them. */
export const NETWORK = {
	apiHosts: ['api-main', 'api-ingest', 'api-stream', 'api-cronz', 'api-integration'],
	jobs: ['database-migration', 'kafka-topics'],
	/** The hosts the simulator calls on their plain port (dev and demo only). */
	simulatorTargets: ['api-main', 'api-ingest', 'api-integration'],
	/** ARV-182: the scheduled backup's pod (backup.enabled), the chart's own although it runs the TimescaleDB image. */
	backup: 'ariva-db-backup'
};
NETWORK.workloads = [...NETWORK.apiHosts, 'web', 'simulation', ...NETWORK.jobs, NETWORK.backup];

/** The port a pod serves the ingress controller on (ARV-082b): an API host 8443 with pod TLS or 8080, the web pod 3443 or 3000, the simulator 8080. */
export function servingPort(app, podTls) {
	if (app === 'web') return podTls ? 3443 : 3000;
	if (app === 'simulation') return 8080;
	return podTls ? 8443 : 8080;
}

/** Whether a label selector (matchLabels, matchExpressions) selects the labels; an empty selector selects everything. */
function selectorMatches(selector, labels) {
	if (!selector || typeof selector !== 'object') return false;
	const own = labels ?? {};
	for (const [key, value] of Object.entries(selector.matchLabels ?? {})) if (own[key] !== String(value)) return false;
	for (const expression of selector.matchExpressions ?? []) {
		const has = Object.hasOwn(own, expression?.key ?? '');
		const values = (expression?.values ?? []).map(String);
		if (expression?.operator === 'In') { if (!(has && values.includes(own[expression.key]))) return false; }
		else if (expression?.operator === 'NotIn') { if (has && values.includes(own[expression.key])) return false; }
		else if (expression?.operator === 'Exists') { if (!has) return false; }
		else if (expression?.operator === 'DoesNotExist') { if (has) return false; }
		else return false;
	}
	return true;
}

const selectsAll = (selector) => !Object.keys(selector?.matchLabels ?? {}).length && !(selector?.matchExpressions ?? []).length;

/** The pods of a render: { app, id, labels, ariva } for every workload (ariva: it runs an image Ariva builds). */
function podsOf(docs) {
	return docs.filter((doc) => doc && WORKLOADS.has(doc.kind)).map((doc) => {
		const template = doc.kind === 'CronJob' ? doc.spec?.jobTemplate?.spec?.template : doc.spec?.template;
		const pod = template?.spec ?? {};
		const labels = template?.metadata?.labels ?? {};
		const ariva = [...(pod.initContainers ?? []), ...(pod.containers ?? [])].some((container) => ARIVA_IMAGES.has(imageName(container.image ?? '')))
			|| (doc.kind === 'CronJob' && doc.metadata?.name === NETWORK.backup && labels.app === NETWORK.backup);
		return { app: labels.app ?? doc.metadata?.name, id: `${doc.kind}/${doc.metadata?.name ?? '?'}`, labels, ariva };
	});
}

const canonical = (value) => (Array.isArray(value) ? value.map(canonical)
	: value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonical(value[key])])) : value);

/**
 * What a peer names in a flow: pod:<app> for each Ariva pod that a podSelector of the release's namespace (no
 * namespaceSelector) selects, so the simulator's flows read the same however their selectors are written; any other peer
 * as itself (JSON with sorted keys), as the values give it.
 */
function peerNames(peer, pods) {
	if (peer && typeof peer === 'object' && peer.podSelector && !peer.namespaceSelector && !peer.ipBlock) {
		const apps = pods.filter((pod) => pod.ariva && selectorMatches(peer.podSelector, pod.labels)).map((pod) => `pod:${pod.app}`);
		if (apps.length) return [...new Set(apps)];
	}
	return [JSON.stringify(canonical(peer))];
}

const portName = (port) => `${port?.protocol ?? 'TCP'}/${port?.port}${port?.endPort ? `-${port.endPort}` : ''}`;

/** The Ariva workloads the render's Ingresses route to, by app (their backend Services' names without -service). */
function routedApps(docs) {
	const names = docs.filter((doc) => doc?.kind === 'Ingress').flatMap((ingress) => [ingress.spec?.defaultBackend,
		...(ingress.spec?.rules ?? []).flatMap((rule) => (rule.http?.paths ?? []).map((entry) => entry.backend))].map((backend) => backend?.service?.name).filter(Boolean));
	return [...new Set(names.map((name) => String(name).replace(/-service$/, '')))].filter((app) => NETWORK.workloads.includes(app));
}

/**
 * ARV-085: the flows the firewall matrix gives each Ariva pod, from the release's values ({ podTls, simulation, mqtt (its port,
 * or null when off), networkPolicies, ingressController }) and the render's Ingresses (which pods are routed): a Map of app to
 * a Set of "in|out <peer> <protocol>/<port>".
 * - in: the ingress controller to every routed pod on its serving port; the sensor networks to api-ingest's MQTT port; with
 *   the simulator, the simulator to api-main, api-ingest and api-integration on 8080, and api-integration to the simulator.
 * - out: DNS (53 UDP and TCP) for every pod but the web pod; the database for the API hosts and the migration job; Kafka for
 *   the API hosts, the topics job and the simulator; Redis for the API hosts; the OTLP collector for the API hosts and the
 *   simulator; Loki for the API hosts; the SMTP relay for api-integration and api-cronz (the scheduled reports); each outbound
 *   endpoint for api-integration; the simulator to the three hosts and api-integration to the simulator; with the malware scan
 *   on and its signatures from the public mirrors or the site's (ARV-092c), the signature mirror for api-main, whose freshclam
 *   sidecar updates them (clamd answers api-main on the pod's loopback, which no NetworkPolicy filters); with the scheduled
 *   backup (ARV-182), DNS and the database for its pod, which takes nothing in.
 */
export function expectedNetworkFlows(values, docs) {
	const pods = podsOf(docs);
	const np = values.networkPolicies ?? {};
	const controller = values.ingressController ?? {};
	const flows = new Map(NETWORK.workloads.map((app) => [app, new Set()]));
	const add = (apps, direction, peers, ports) => {
		for (const app of apps) for (const peer of peers ?? []) for (const name of peerNames(peer, pods)) for (const port of ports) flows.get(app).add(`${direction} ${name} ${port}`);
	};
	const tcp = (ports) => (ports ?? []).map((port) => `TCP/${port}`);
	const withSimulation = (apps) => (values.simulation ? [...apps, 'simulation'] : apps);
	for (const app of routedApps(docs)) {
		add([app], 'in', [{ namespaceSelector: controller.namespaceSelector, podSelector: controller.podSelector }], [`TCP/${servingPort(app, values.podTls)}`]);
	}
	if (values.mqtt) add(['api-ingest'], 'in', np.mqttSensors?.from, [`TCP/${values.mqtt.port}`]);
	if (values.simulation) {
		for (const app of NETWORK.simulatorTargets) {
			flows.get(app).add('in pod:simulation TCP/8080');
			flows.get('simulation').add(`out pod:${app} TCP/8080`);
		}
		flows.get('simulation').add('in pod:api-integration TCP/8080');
		flows.get('api-integration').add('out pod:simulation TCP/8080');
	}
	add(withSimulation([...NETWORK.apiHosts, ...NETWORK.jobs]), 'out', np.dns?.to, ['UDP/53', 'TCP/53']);
	add([...NETWORK.apiHosts, 'database-migration'], 'out', np.database?.to, tcp(np.database?.ports));
	add(withSimulation([...NETWORK.apiHosts, 'kafka-topics']), 'out', np.kafka?.to, tcp(np.kafka?.ports));
	add(NETWORK.apiHosts, 'out', np.redis?.to, tcp(np.redis?.ports));
	add(withSimulation(NETWORK.apiHosts), 'out', np.otlp?.to, tcp(np.otlp?.ports));
	add(NETWORK.apiHosts, 'out', np.loki?.to, tcp(np.loki?.ports));
	add(['api-cronz', 'api-integration'], 'out', np.smtp?.to, tcp(np.smtp?.ports));
	for (const endpoint of np.outbound ?? []) add(['api-integration'], 'out', endpoint?.to, tcp(endpoint?.ports));
	if (values.uploadScan?.enabled && ['public', 'mirror'].includes(values.uploadScan.source)) add(['api-main'], 'out', np.clamavMirror?.to, tcp(np.clamavMirror?.ports ?? [443]));
	if (values.backup) {
		add([NETWORK.backup], 'out', np.dns?.to, ['UDP/53', 'TCP/53']);
		add([NETWORK.backup], 'out', np.database?.to, tcp(np.database?.ports));
	}
	return flows;
}

/**
 * ARV-085: findings for the NetworkPolicies of a render (docs parsed as Kubernetes reads them), given whether the release has
 * them on, its environment and its values (expectedNetworkFlows).
 * Off: no NetworkPolicy selects an Ariva pod. On:
 * - every Ariva pod is selected by a default deny (policyTypes Ingress and Egress, no rule);
 * - no rule is a wildcard: every ingress rule has peers (from) and ports, every egress rule peers (to) and ports;
 * - no policy selects Ariva pods together with others (the database's own policy stays its own);
 * - in k8s-prd no ipBlock is wider than /8 (IPv4) or /16 (IPv6) or not a network address, and no peer selects every pod of
 *   every namespace;
 * - the flows each Ariva pod gets (the union of every policy that selects it) are exactly expectedNetworkFlows'. Findings
 *   group the pods that share the same extra or missing flows.
 */
export function networkPolicyFindings(docs, { enabled, environment, values }) {
	const findings = [];
	const pods = podsOf(docs);
	const arivaPods = pods.filter((pod) => pod.ariva);
	const policies = docs.filter((doc) => doc?.kind === 'NetworkPolicy');
	const typesOf = (spec) => spec.policyTypes ?? (spec.egress ? ['Ingress', 'Egress'] : ['Ingress']);
	if (!enabled) {
		for (const policy of policies) {
			const selected = arivaPods.filter((pod) => selectorMatches(policy.spec?.podSelector ?? {}, pod.labels));
			if (selected.length) findings.push(`NetworkPolicy/${policy.metadata?.name}: selects ${selected.map((pod) => pod.app).join(', ')} although networkPolicies is off`);
		}
		return findings;
	}
	const prd = environment === 'k8s-prd';
	for (const policy of policies) {
		const pid = `NetworkPolicy/${policy.metadata?.name}`;
		const spec = policy.spec ?? {};
		const selected = pods.filter((pod) => selectorMatches(spec.podSelector ?? {}, pod.labels));
		if (selected.some((pod) => pod.ariva) && selected.some((pod) => !pod.ariva)) {
			findings.push(`${pid}: selects Ariva pods together with ${selected.filter((pod) => !pod.ariva).map((pod) => pod.id).join(', ')}`);
		}
		if (!selected.some((pod) => pod.ariva)) continue;
		for (const [direction, rules, field] of [['ingress', spec.ingress ?? [], 'from'], ['egress', spec.egress ?? [], 'to']]) {
			rules.forEach((rule, index) => {
				const where = `${pid}: ${direction}[${index}]`;
				if (!(rule?.[field] ?? []).length) findings.push(`${where} has no ${field}, so it ${direction === 'ingress' ? 'takes connections from everyone' : 'opens every destination'}`);
				if (!(rule?.ports ?? []).length) findings.push(`${where} has no ports, so it ${direction === 'ingress' ? 'takes' : 'opens'} every port`);
				for (const peer of rule?.[field] ?? []) {
					if (peer?.ipBlock) {
						for (const [label, cidr] of [['cidr', peer.ipBlock.cidr], ...(peer.ipBlock.except ?? []).map((value) => ['except', value])]) {
							findings.push(...cidrFindings(cidr, `${where} ipBlock ${label}`, prd && label === 'cidr' ? NETWORK_PREFIXES.open : undefined));
						}
					} else if (prd && peer?.namespaceSelector && selectsAll(peer.namespaceSelector) && selectsAll(peer.podSelector)) {
						findings.push(`${where} selects every pod of every namespace`);
					}
				}
			});
		}
	}
	const expected = expectedNetworkFlows(values ?? {}, docs);
	const groups = new Map();
	const group = (key, text, pod) => {
		if (!groups.has(key)) groups.set(key, { text, pods: [] });
		groups.get(key).pods.push(pod.id);
	};
	for (const pod of arivaPods) {
		const isolated = { Ingress: false, Egress: false };
		let defaultDeny = false;
		const flows = new Set();
		for (const policy of policies) {
			const spec = policy.spec ?? {};
			if (!selectorMatches(spec.podSelector ?? {}, pod.labels)) continue;
			const types = typesOf(spec);
			for (const type of types) isolated[type] = true;
			if (types.includes('Ingress') && types.includes('Egress') && !(spec.ingress ?? []).length && !(spec.egress ?? []).length) defaultDeny = true;
			for (const [direction, rules, field, type] of [['in', spec.ingress ?? [], 'from', 'Ingress'], ['out', spec.egress ?? [], 'to', 'Egress']]) {
				if (!types.includes(type)) continue;
				for (const rule of rules) {
					const peers = (rule?.[field] ?? []).length ? rule[field] : [{ everyone: true }];
					const ports = (rule?.ports ?? []).length ? rule.ports.map(portName) : ['every port'];
					for (const peer of peers) for (const name of peerNames(peer, pods)) for (const port of ports) flows.add(`${direction} ${name} ${port}`);
				}
			}
		}
		if (!defaultDeny || !isolated.Ingress || !isolated.Egress) group('deny', 'no default deny selects them (a NetworkPolicy with policyTypes Ingress and Egress and no rule)', pod);
		const wanted = expected.get(pod.app) ?? new Set();
		const extra = [...flows].filter((flow) => !wanted.has(flow)).sort();
		const missing = [...wanted].filter((flow) => !flows.has(flow)).sort();
		if (extra.length) group(`extra ${extra.join(' | ')}`, `the policies allow what the firewall matrix does not: ${extra.join(' | ')}`, pod);
		if (missing.length) group(`missing ${missing.join(' | ')}`, `the policies lack the firewall matrix's ${missing.join(' | ')}`, pod);
	}
	for (const { text, pods: ids } of groups.values()) findings.push(`${ids.join(', ')}: ${text}`);
	return findings;
}

/** ARV-085: the network values expectedNetworkFlows reads, from a release's merged values (values.yaml, then its files). */
export function networkValues(values) {
	return {
		podTls: (values?.podTls ?? {}).enabled === true,
		simulation: values?.simulationEnabled === true,
		mqtt: values?.mqtt?.enabled ? { port: values.mqtt.port ?? 8883 } : null,
		networkPolicies: values?.networkPolicies ?? {},
		ingressController: values?.ingressController ?? {},
		uploadScan: { enabled: values?.uploadScan?.enabled === true, source: String(values?.uploadScan?.signatures?.source ?? '') },
		backup: values?.backup?.enabled === true
	};
}

/**
 * ARV-085 (ASVS 5.0 V13.4.5): the health probes are private. The paths the chart's server-snippet must answer 404 at the
 * server level, every spelling ASP.NET Core and the web pod's nginx would serve (routing ignores case), and paths it must not.
 */
export const HEALTH_PROBES = {
	paths: ['/health', '/health/', '/health/startup', '/health/readiness', '/health/liveness', '/HEALTH/READINESS', '/Health/Startup', '/healthz', '/healthz/'],
	others: ['/', '/api', '/api/health', '/api/v1/sensing/devices/health', '/healthy', '/healthcheck', '/hub', '/login'],
	route: /^\/healthz?(\/|$)/i
};

/**
 * ARV-085: findings for the Ingresses' private probes and source ranges (docs parsed as Kubernetes reads them):
 * - no Ingress path routes /health, /healthz or anything under them;
 * - every server-snippet answers each of HEALTH_PROBES.paths 404 (or 403) at the server level, over HTTPS after it sets HSTS (so the
 *   404 carries it, as the 421 does), and answers none of HEALTH_PROBES.others;
 * - no Ingress sets rewrite-target or use-regex and no annotation value writes a rewrite directive: the 404 is judged on the
 *   request's own path, so a rewrite could take another path to /health on the pod;
 * - ARV-288: no annotation value writes an include, client_body_in_file_only, a *_by_lua directive or rewrite_log
 *   (INGRESS_REFUSED_DIRECTIVES), a second line behind validate.yaml that also proves the chart's own snippets write none;
 * - allowlist-source-range, when set, is a list of CIDRs (network addresses), in k8s-prd none wider than /8 (IPv4) or /16
 *   (IPv6); no Ingress sets whitelist-source-range (the alias ingress-nginx reads only without allowlist-source-range);
 * - in k8s-prd every Ingress to api-cronz-service (the TickerQ dashboard) has allowlist-source-range.
 */
export function ingressPrivacyFindings(docs, { environment }) {
	const findings = [];
	const prd = environment === 'k8s-prd';
	for (const ingress of docs.filter((doc) => doc?.kind === 'Ingress')) {
		const id = `Ingress/${ingress.metadata?.name}`;
		const annotations = ingress.metadata?.annotations ?? {};
		const entries = (ingress.spec?.rules ?? []).flatMap((rule) => rule.http?.paths ?? []);
		const routed = entries.map((entry) => entry.path).filter((value) => HEALTH_PROBES.route.test(String(value ?? '')));
		if (routed.length) findings.push(`${id}: routes ${routed.join(', ')}; the health probes stay on the pods' own ports`);
		let server = [];
		try {
			server = nginxTree(annotations[`${NGINX}server-snippet`] ?? '');
		} catch (error) {
			findings.push(`${id}: server-snippet is not valid nginx (${error.message})`);
		}
		const hsts = headerDirectives(server, 'Strict-Transport-Security').find((directive) => directive.kind === 'more_set_headers');
		const onPath = (uri) => serverValueOnPath(server, hsts?.value ?? '', { scheme: 'https', otherHost: false, uri });
		const unrefused = HEALTH_PROBES.paths.filter((uri) => !['403', '404'].includes(String(onPath(uri).returned)));
		if (unrefused.length) findings.push(`${id}: server-snippet does not answer ${unrefused.join(', ')} with 404 at the server level (if ($uri ~* "^/healthz?(/|$)") { return 404; }), so a location of the host can route them to a pod`);
		else if (hsts && HEALTH_PROBES.paths.some((uri) => !onPath(uri).value)) findings.push(`${id}: server-snippet refuses the probes before it sets Strict-Transport-Security, so its 404 carries none`);
		const overreach = HEALTH_PROBES.others.filter((uri) => onPath(uri).returned !== undefined);
		if (overreach.length) findings.push(`${id}: server-snippet refuses ${overreach.join(', ')}, which are not health probes`);
		// The 404 is judged on the request's own path, before any rewrite: no Ariva Ingress rewrites one.
		for (const key of ['rewrite-target', 'use-regex']) {
			if (annotations[`${NGINX}${key}`] !== undefined) findings.push(`${id}: sets ${key}; the probes' 404 is judged on the request's own path, so no Ariva Ingress rewrites one`);
		}
		for (const [key, value] of Object.entries(annotations)) {
			if (nginxDirective('rewrite').test(String(value))) findings.push(`${id}: ${key} writes a rewrite directive, which changes the path after the probes' check`);
			for (const [name, why, story] of refusedDirectives(INGRESS_REFUSED_DIRECTIVES, value)) findings.push(`${id}: ${key} writes ${name}: ${why} (${story})`);
		}
		if (annotations[`${NGINX}whitelist-source-range`] !== undefined) findings.push(`${id}: sets whitelist-source-range, the alias; the chart writes allowlist-source-range, the name ingress-nginx reads first`);
		const allow = annotations[`${NGINX}allowlist-source-range`];
		if (allow !== undefined) {
			for (const range of String(allow).split(',').map((value) => value.trim())) findings.push(...cidrFindings(range, `${id}: allowlist-source-range`, prd ? NETWORK_PREFIXES.open : undefined));
		}
		const cronz = entries.some((entry) => entry.backend?.service?.name === 'api-cronz-service');
		if (prd && cronz && !String(allow ?? '').trim()) findings.push(`${id}: the Cronz dashboard has no allowlist-source-range; k8s-prd opens it to the management network only`);
	}
	return findings;
}

/**
 * ARV-091 (ASVS 5.0 V16.2.5, CWE-532): the live hub's URL carries the access token (access_token), and Ariva.Api.Main reads it
 * on /hubs, in any case, whatever the host (JwtAuthenticationExtensions). route: the paths of the hub's own Ingress (the one
 * that logs neither the request nor errors below critical, checkManifests). all: the spellings a host without that Ingress
 * answers 404; served: what the hub's Ingress takes (nginx matches its location by case); variants: the other spellings,
 * which its host answers 404; others: paths no host refuses for the hub.
 */
export const HUB_PATHS = {
	route: /^\/hubs(\/|$)/,
	all: ['/hubs', '/hubs/', '/hubs/live', '/hubs/live/negotiate', '/HUBS/live', '/Hubs', '/hUbS/live/negotiate'],
	served: ['/hubs', '/hubs/', '/hubs/live', '/hubs/live/negotiate'],
	variants: ['/HUBS/live', '/Hubs', '/hUbS/live/negotiate'],
	others: ['/', '/api', '/api/hubs', '/hub', '/hubsx', '/hubs-old', '/HUBSX', '/login']
};

/**
 * ARV-091: findings for the live hub's refusal on every Ingress of a render (docs parsed as Kubernetes reads them). A host is the
 * hub's when one of its Ingresses routes HUB_PATHS.route. Every server-snippet answers 404 (or 403) at the server level, before
 * any location, over HTTPS after it sets HSTS (so the 404 carries it):
 * - on a host without the hub, every path of HUB_PATHS.all, so the token never reaches a location whose logs would write it
 *   (api-main's own host routes / to the same Service as the hub);
 * - on the hub's host, every path of HUB_PATHS.variants and none of HUB_PATHS.served;
 * and none of HUB_PATHS.others anywhere.
 */
export function hubRefusalFindings(docs) {
	const findings = [];
	const ingresses = docs.filter((doc) => doc?.kind === 'Ingress');
	const hostsOf = (ingress) => [...new Set((ingress.spec?.rules ?? []).map((rule) => rule.host).filter(Boolean))];
	const servesHub = (ingress) => (ingress.spec?.rules ?? []).some((rule) => (rule.http?.paths ?? []).some((entry) => HUB_PATHS.route.test(String(entry.path ?? ''))));
	const hubHosts = new Set(ingresses.filter(servesHub).flatMap(hostsOf));
	for (const ingress of ingresses) {
		const id = `Ingress/${ingress.metadata?.name}`;
		let server = [];
		try {
			server = nginxTree(ingress.metadata?.annotations?.[`${NGINX}server-snippet`] ?? '');
		} catch (error) {
			findings.push(`${id}: server-snippet is not valid nginx (${error.message})`);
		}
		const hsts = headerDirectives(server, 'Strict-Transport-Security').find((directive) => directive.kind === 'more_set_headers');
		const onPath = (uri) => serverValueOnPath(server, hsts?.value ?? '', { scheme: 'https', otherHost: false, uri });
		const refused = (uri) => ['403', '404'].includes(String(onPath(uri).returned));
		const hub = hostsOf(ingress).some((host) => hubHosts.has(host));
		const required = hub ? HUB_PATHS.variants : HUB_PATHS.all;
		const open = required.filter((uri) => !refused(uri));
		if (open.length && hub) {
			findings.push(`${id}: server-snippet does not answer ${open.join(', ')} with 404 at the server level: the host's live hub Ingress takes /hubs in lowercase only ` +
				'(nginx matches locations by case), so another spelling reaches another location with the access token in its URL (if ($uri ~ "^/(?!hubs(/|$))[Hh][Uu][Bb][Ss](/|$)") { return 404; })');
		} else if (open.length) {
			findings.push(`${id}: server-snippet does not answer ${open.join(', ')} with 404 at the server level (if ($uri ~* "^/hubs(/|$)") { return 404; }): the host has no live hub ` +
				'Ingress, and Ariva.Api.Main reads the access token from the query on /hubs whatever the host, so the token would reach a location that logs it');
		} else if (hsts && required.some((uri) => !onPath(uri).value)) findings.push(`${id}: server-snippet refuses /hubs before it sets Strict-Transport-Security, so its 404 carries none`);
		if (hub) {
			const blocked = HUB_PATHS.served.filter(refused);
			if (blocked.length) findings.push(`${id}: server-snippet refuses ${blocked.join(', ')}, which the host's live hub serves`);
		}
		const overreach = HUB_PATHS.others.filter((uri) => onPath(uri).returned !== undefined);
		if (overreach.length) findings.push(`${id}: server-snippet refuses ${overreach.join(', ')}, which are not the live hub`);
	}
	return findings;
}

const KNOWN_NETWORK = /^Security__ForwardedHeaders__KnownNetworks__(\d+)$/;

/**
 * ARV-085 (ASVS 5.0 V4.1.3): the trusted proxies. Every API host container gets exactly ingressController.podNetworks as
 * Security__ForwardedHeaders__KnownNetworks__0, __1 ... in order, as literal values, and no other Security__ForwardedHeaders__
 * variable (no KnownProxies, no ForwardLimit); no other workload gets any. The networks are CIDRs (network addresses), never
 * /0, and in k8s-prd present, no wider than /14 (IPv4) or /48 (IPv6) and none that trusts IPv4 clients wider than a /14
 * (trustsMappedClients): the hosts refuse to start on any of these.
 */
export function forwardedHeadersFindings(docs, { environment, podNetworks = [] }) {
	const findings = [];
	const prd = environment === 'k8s-prd';
	if (prd && !podNetworks.length) findings.push("ingressController.podNetworks is empty: k8s-prd needs the ingress controller's pod network");
	podNetworks.forEach((network, index) => {
		const where = `ingressController.podNetworks[${index}]`;
		const found = cidrFindings(String(network), where, prd ? NETWORK_PREFIXES.proxies : NETWORK_PREFIXES.anyProxy);
		if (!found.length && prd && trustsMappedClients(String(network))) {
			found.push(`${where}: ${network} trusts IPv4 clients wider than an IPv4 /14 (as IPv4-mapped addresses: the mapped block, a network holding it, or a prefix from /65 to /77 or /97 to /109); the hosts refuse it`);
		}
		findings.push(...found);
	});
	const expected = podNetworks.map(String);
	for (const doc of docs) {
		if (!doc || !WORKLOADS.has(doc.kind)) continue;
		const id = `${doc.kind}/${doc.metadata?.name ?? '?'}`;
		const pod = podSpecOf(doc) ?? {};
		const apiHost = doc.kind === 'Deployment' && /^api-[a-z]+-deployment$/.test(doc.metadata?.name ?? '');
		for (const container of [...(pod.initContainers ?? []), ...(pod.containers ?? [])]) {
			const cid = `${id} container ${container.name}`;
			const trusted = (container.env ?? []).filter((variable) => /^Security__ForwardedHeaders__/i.test(variable.name ?? ''));
			if (!(apiHost && DOTNET_IMAGES.has(imageName(container.image ?? '')))) {
				if (trusted.length) findings.push(`${cid}: sets ${trusted.map((variable) => variable.name).join(', ')}; only the API hosts get the trusted proxy networks`);
				continue;
			}
			const other = trusted.filter((variable) => !KNOWN_NETWORK.test(variable.name));
			if (other.length) findings.push(`${cid}: sets ${other.map((variable) => variable.name).join(', ')}; the chart passes the ingress controller's networks only (KnownNetworks)`);
			const given = trusted.filter((variable) => KNOWN_NETWORK.test(variable.name)).map((variable) => ({ index: Number(KNOWN_NETWORK.exec(variable.name)[1]), value: variable.valueFrom ? null : variable.value }))
				.sort((a, b) => a.index - b.index);
			if (given.length !== expected.length || given.some((entry, position) => entry.index !== position || entry.value !== expected[position])) {
				findings.push(`${cid}: Security__ForwardedHeaders__KnownNetworks is ${given.map((entry) => `${entry.index}=${entry.value ?? '(not a literal)'}`).join(', ') || 'unset'}, not ingressController.podNetworks (${expected.join(', ') || 'none'})`);
			}
		}
	}
	return findings;
}

/** ARV-091a: the five API hosts, which alone get the log pseudonym key, the Loki switch and Loki's credentials. */
export const API_HOSTS = ['api-main', 'api-ingest', 'api-stream', 'api-cronz', 'api-integration'];
const LOKI_ENV = /^Loki__Enabled$/i;

/**
 * ARV-091a: the variables the five API hosts take from a Secret the values name: the log pseudonym key (pseudonymKeySecret) and
 * Loki's sender credentials (lokiCredentialsSecret), each with the value that names it, what it is and why it is never optional.
 */
function secretVariables({ pseudonymKeySecret, lokiCredentialsSecret, otelHeadersSecret }) {
	const pseudonym = pseudonymKeySecret?.name ? { name: String(pseudonymKeySecret.name), key: String(pseudonymKeySecret.key || 'pseudonym-key') } : undefined;
	const loki = lokiCredentialsSecret?.name ? String(lokiCredentialsSecret.name) : '';
	return [
		{ variable: 'Logging__PseudonymKey', value: 'logging.pseudonymKeySecret', what: 'the log pseudonym key', secretLabel: "the log pseudonym key's Secret", secret: pseudonym,
			optional: 'the host waits for its Secret rather than start with a key of its own' },
		...['Username', 'Password'].map((part) => ({ variable: `Loki__${part}`, value: 'logging.lokiCredentialsSecret', what: "Loki's credentials", secretLabel: "the Secret of Loki's credentials",
			secret: loki ? { name: loki, key: String(lokiCredentialsSecret[`${part.toLowerCase()}Key`] || part.toLowerCase()) } : undefined,
			optional: 'the host waits for its Secret rather than send to Loki without them' })),
		// The simulator exports to the same collector in dev and demo, so it gets the headers too.
		{ variable: 'OTEL_EXPORTER_OTLP_HEADERS', value: 'otel.headersSecret', what: "the OTLP collector's sender headers", secretLabel: "the Secret of the OTLP collector's headers",
			secret: otelHeadersSecret?.name ? { name: String(otelHeadersSecret.name), key: String(otelHeadersSecret.key || 'headers') } : undefined, simulator: true,
			optional: 'the host waits for its Secret rather than export to the collector without them' }
	].map((entry) => ({ ...entry, pattern: new RegExp(`^${entry.variable}$`, 'i') }));
}

/**
 * ARV-091a (ASVS 5.0 V16.4.2, V16.4.3; CWE-532, CWE-798): log shipping and the log pseudonym key of a render, against its values:
 * collector (otlp, loki or empty), otelEndpoint, pseudonymKeySecret ({ name, key }, or nothing), lokiCredentialsSecret ({ name,
 * usernameKey, passwordKey }, or nothing) and required.
 * - k8s-prd, or required: a collector and the key's Secret; otlp exports to an https:// otel.endpoint.
 * - Each of the five API host Deployments: with a Secret named, its .NET container gets each of the Secret's variables
 *   (Logging__PseudonymKey; Loki__Username and Loki__Password) once, from that Secret and key (secretKeyRef, not optional, never
 *   a literal); without, none. With loki, Loki__Enabled "true" (so a k8s-prd host refuses to start without an https Loki:Uri);
 *   otherwise no Loki__Enabled. With otlp, OTEL_EXPORTER_OTLP_ENDPOINT is otel.endpoint. Variable names compare ignoring case,
 *   as .NET's configuration reads them.
 * - No other workload gets these variables or refers to their Secrets (env, envFrom, volumes); no ConfigMap names them (a data
 *   key, or a value naming PseudonymKey: envFrom would make it a variable); the chart renders neither Secret.
 */
export function logShippingFindings(docs, { environment, collector = '', otelEndpoint = '', pseudonymKeySecret, lokiCredentialsSecret, otelHeadersSecret, required = false }) {
	const findings = [];
	const where = environment === 'k8s-prd' ? 'k8s-prd' : 'logging.required';
	const needed = environment === 'k8s-prd' || required === true;
	const variables = secretVariables({ pseudonymKeySecret, lokiCredentialsSecret, otelHeadersSecret });
	const secrets = [...new Map(variables.filter((entry) => entry.secret).map((entry) => [entry.secret.name, entry.secretLabel])).entries()];
	if (needed && !['otlp', 'loki'].includes(collector)) findings.push(`${where} renders without a log collector (logging.collector otlp or loki)`);
	if (needed && !variables[0].secret) findings.push(`${where} renders without the log pseudonym key's Secret (logging.pseudonymKeySecret.name)`);
	if (collector === 'otlp' && !/^https:\/\/\S+$/i.test(otelEndpoint)) findings.push(`logging.collector otlp exports to ${otelEndpoint || 'no endpoint'}, not an https:// otel.endpoint`);
	if (otelHeadersSecret?.name && !/^https:\/\/\S+$/i.test(otelEndpoint)) findings.push(`otel.headersSecret sends the collector's headers to ${otelEndpoint || 'no endpoint'}, not an https:// otel.endpoint`);
	const hosts = new Set();
	for (const doc of docs) {
		if (!doc || typeof doc !== 'object') continue;
		const id = `${doc.kind}/${doc.metadata?.name ?? '?'}`;
		if (doc.kind === 'ConfigMap') {
			for (const [key, value] of Object.entries(doc.data ?? {})) {
				if (/pseudonym/i.test(key) || /PseudonymKey/i.test(String(value))) findings.push(`${id}: ${key} names the log pseudonym key; it reaches the hosts from its Secret only (secretKeyRef), never from a ConfigMap`);
				else if (/^Loki__(Username|Password)$/i.test(key)) findings.push(`${id}: ${key} is one of Loki's credentials; they reach the hosts from their Secret only (secretKeyRef), never from a ConfigMap`);
				else if (/^OTEL_EXPORTER_OTLP_HEADERS$/i.test(key)) findings.push(`${id}: ${key} carries the OTLP collector's sender headers; they reach the pods from their Secret only (secretKeyRef), never from a ConfigMap`);
			}
			continue;
		}
		if (doc.kind === 'Secret') {
			const keys = Object.keys({ ...(doc.data ?? {}), ...(doc.stringData ?? {}) });
			const named = secrets.find(([name]) => doc.metadata?.name === name);
			if (named || keys.some((key) => /pseudonym/i.test(key))) findings.push(`${id}: the chart renders ${named ? named[1] : "the log pseudonym key's Secret"}; the site creates it and the chart only names it`);
			continue;
		}
		if (!WORKLOADS.has(doc.kind)) continue;
		const pod = podSpecOf(doc) ?? {};
		const name = doc.metadata?.name ?? '';
		const apiHost = doc.kind === 'Deployment' && API_HOSTS.some((host) => name === `${host}-deployment`);
		if (apiHost) hosts.add(name);
		const containers = [...(pod.initContainers ?? []), ...(pod.containers ?? [])];
		const references = new Map(secrets.map(([secretName]) => [secretName, []]));
		for (const container of containers) {
			const cid = `${id} container ${container.name}`;
			const env = container.env ?? [];
			const flags = env.filter((variable) => LOKI_ENV.test(variable.name ?? ''));
			const main = apiHost && DOTNET_IMAGES.has(imageName(container.image ?? ''));
			const simulator = doc.kind === 'Deployment' && name === 'simulation-deployment' && imageName(container.image ?? '') === 'simulation';
			const belongs = (entry) => main || (entry.simulator === true && simulator);
			// A Secret's variable in its own place (its container, under the expected name) is not a stray reference.
			const expected = (variable) => variables.some((entry) => belongs(entry) && entry.secret?.name === variable.valueFrom?.secretKeyRef?.name && entry.pattern.test(variable.name ?? ''));
			for (const variable of env) {
				const secretName = variable.valueFrom?.secretKeyRef?.name;
				if (references.has(secretName) && !expected(variable)) references.get(secretName).push(`${container.name} env ${variable.name}`);
			}
			for (const source of container.envFrom ?? []) if (references.has(source.secretRef?.name)) references.get(source.secretRef.name).push(`${container.name} envFrom`);
			for (const entry of variables.filter((candidate) => !belongs(candidate))) {
				const given = env.filter((variable) => entry.pattern.test(variable.name ?? ''));
				if (given.length) findings.push(`${cid}: gets ${given.map((variable) => variable.name).join(', ')}; only the five API hosts${entry.simulator ? ' and the simulator' : ''} get ${entry.what}`);
			}
			if (!main && flags.length) findings.push(`${cid}: sets ${flags.map((variable) => variable.name).join(', ')}; only the five API hosts send their logs to Loki`);
			for (const entry of variables.filter(belongs)) {
				const given = env.filter((variable) => entry.pattern.test(variable.name ?? ''));
				if (!entry.secret) {
					if (given.length) findings.push(`${cid}: gets ${entry.variable} although ${entry.value} names no Secret`);
					continue;
				}
				if (given.length !== 1) {
					findings.push(`${cid}: must get ${entry.variable} once, from the Secret ${entry.secret.name} (key ${entry.secret.key}), not ${given.length} times`);
					continue;
				}
				const ref = given[0].valueFrom?.secretKeyRef;
				if (given[0].value !== undefined || !ref) findings.push(`${cid}: ${entry.variable} must come from the Secret ${entry.secret.name} (secretKeyRef), never a literal value`);
				else {
					if (ref.name !== entry.secret.name || ref.key !== entry.secret.key) findings.push(`${cid}: ${entry.variable} comes from ${ref.name} (key ${ref.key}), not ${entry.secret.name} (key ${entry.secret.key})`);
					if (ref.optional === true) findings.push(`${cid}: ${entry.variable} must not be optional: ${entry.optional}`);
				}
			}
			if (!main) continue;
			const last = flags.at(-1);
			const loki = last ? (last.valueFrom ? null : last.value) : envValueOf(container, docs, 'Loki__Enabled');
			if (collector === 'loki' && loki !== 'true') findings.push(`${cid}: must set Loki__Enabled "true" with logging.collector loki, so the host refuses to start without an https Loki:Uri`);
			if (collector !== 'loki' && loki !== undefined) findings.push(`${cid}: sets Loki__Enabled ${loki ?? '(not a literal)'} without logging.collector loki`);
			if (collector === 'otlp') {
				const endpoint = envValueOf(container, docs, 'OTEL_EXPORTER_OTLP_ENDPOINT');
				if (endpoint !== otelEndpoint) findings.push(`${cid}: exports to ${endpoint === null ? '(not a literal)' : endpoint || 'no endpoint'}, not otel.endpoint ${otelEndpoint || '(empty)'}`);
			}
		}
		for (const volume of pod.volumes ?? []) {
			for (const secretName of [volume.secret?.secretName, ...(volume.projected?.sources ?? []).map((source) => source.secret?.name)]) {
				if (references.has(secretName)) references.get(secretName).push(`volume ${volume.name}`);
			}
		}
		for (const [secretName, found] of references) {
			if (!found.length) continue;
			const label = secrets.find(([candidate]) => candidate === secretName)[1];
			const readers = variables.filter((entry) => entry.secret?.name === secretName);
			const names = readers.map((entry) => entry.variable).join(' and ');
			findings.push(`${id}: refers to ${label} ${secretName} (${found.join(', ')}); only the five API hosts${readers.every((entry) => entry.simulator) ? ' and the simulator' : ''} read it, as ${names}`);
		}
	}
	const missing = API_HOSTS.filter((host) => !hosts.has(`${host}-deployment`));
	if (missing.length) findings.push(`the render holds no ${missing.map((host) => `Deployment/${host}-deployment`).join(', ')}`);
	return findings;
}

/**
 * ARV-200 (ASVS 5.0 V12.3.1, V12.3.2, V16.4.3): the collectors' CAs, for a log or telemetry collector whose certificate chains to
 * the site PKI. otel.caSecretName names the OTLP collector's (the five API hosts and the simulator export to it) and
 * logging.lokiCaSecretName Loki's (the five API hosts alone send it logs). Each is a volume of the Secret projecting ca.crt
 * only, mounted read-only and whole where the variable points, which the host then trusts alone for that collector.
 */
export const COLLECTOR_CAS = [
	{ volume: 'otlp-ca', label: "the OTLP collector's CA", value: 'otel.caSecretName', path: '/app/secrets/otlp-ca', variable: 'Otlp__RootCertificate', simulator: true },
	{ volume: 'loki-ca', label: "Loki's CA", value: 'logging.lokiCaSecretName', path: '/app/secrets/loki-ca', variable: 'Loki__RootCertificate', simulator: false }
].map((entry) => ({ ...entry, file: `${entry.path}/ca.crt`, pattern: new RegExp(`^${entry.variable}$`, 'i') }));

/**
 * ARV-200: the collectors' CAs of a render, against the values that name their Secrets (otlpCaSecretName, lokiCaSecretName;
 * empty for none). Read the manifests as Kubernetes does (YAML 1.1), for the file modes.
 * - With a name, each of the five API host Deployments, and for the OTLP collector the simulator when it is rendered, has one
 *   volume of that Secret (otlp-ca or loki-ca) projecting ca.crt only, not optional (the pods wait for the Secret: a host given
 *   the variable refuses to start without the file), with a mode that lets no one write or execute; its .NET container mounts it
 *   once, read-only and whole, at /app/secrets/otlp-ca or /app/secrets/loki-ca, and gets the variable (Otlp__RootCertificate,
 *   Loki__RootCertificate) once, the literal path of the mounted ca.crt. Names compare ignoring case, as .NET reads them.
 * - Without a name, none of them; and never in another workload (the web pod, the jobs, the simulator for Loki), nor the
 *   variable in a ConfigMap (envFrom would make it one); the chart never renders the Secret.
 */
export function collectorCaFindings(docs, { otlpCaSecretName = '', lokiCaSecretName = '' } = {}) {
	const findings = [];
	const names = { 'otlp-ca': String(otlpCaSecretName || ''), 'loki-ca': String(lokiCaSecretName || '') };
	const hosts = new Set();
	const who = (ca) => `only the five API hosts${ca.simulator ? ' and the simulator' : ''}`;
	const mounted = (mounts) => mounts.map((mount) => `${mount.name} at ${mount.mountPath}`).join(', ');
	for (const doc of docs) {
		if (!doc || typeof doc !== 'object') continue;
		const id = `${doc.kind}/${doc.metadata?.name ?? '?'}`;
		if (doc.kind === 'ConfigMap') {
			for (const key of Object.keys(doc.data ?? {})) {
				const ca = COLLECTOR_CAS.find((entry) => entry.pattern.test(key));
				if (ca) findings.push(`${id}: ${key} points a host at ${ca.label}; the chart sets it only in the env of the workloads that mount it`);
			}
			continue;
		}
		if (doc.kind === 'Secret') {
			for (const ca of COLLECTOR_CAS) {
				if (names[ca.volume] && doc.metadata?.name === names[ca.volume]) findings.push(`${id}: the chart renders ${ca.label}'s Secret; the site creates it from its PKI and the chart only names it`);
			}
			continue;
		}
		if (!WORKLOADS.has(doc.kind)) continue;
		const pod = podSpecOf(doc) ?? {};
		const name = doc.metadata?.name ?? '';
		const apiHost = doc.kind === 'Deployment' && API_HOSTS.some((host) => name === `${host}-deployment`);
		const simulator = doc.kind === 'Deployment' && name === 'simulation-deployment';
		if (apiHost) hosts.add(name);
		const containers = [...(pod.initContainers ?? []), ...(pod.containers ?? [])];
		for (const ca of COLLECTOR_CAS) {
			const secretName = names[ca.volume];
			const belongs = apiHost || (ca.simulator && simulator);
			const volumes = (pod.volumes ?? []).filter((volume) => volume.name === ca.volume);
			const why = belongs ? ` although ${ca.value} names no Secret` : `; ${who(ca)} read ${ca.label}`;
			const main = (container) => belongs && DOTNET_IMAGES.has(imageName(container.image ?? '')) && (apiHost || imageName(container.image ?? '') === 'simulation');
			if (!belongs || !secretName) {
				if (volumes.length) findings.push(`${id}: has the volume ${ca.volume}${why}`);
				for (const container of containers) {
					const cid = `${id} container ${container.name}`;
					const mounts = (container.volumeMounts ?? []).filter((mount) => mount.name === ca.volume || mount.mountPath === ca.path);
					if (mounts.length) findings.push(`${cid}: mounts ${mounted(mounts)}${why}`);
					const given = (container.env ?? []).filter((variable) => ca.pattern.test(variable.name ?? ''));
					if (given.length) findings.push(`${cid}: sets ${given.map((variable) => variable.name).join(', ')}${why}`);
				}
				continue;
			}
			if (volumes.length !== 1) findings.push(`${id}: needs one volume ${ca.volume}, ${ca.label} from the Secret ${secretName}, not ${volumes.length}`);
			else {
				const secret = volumes[0].secret;
				const items = secret?.items ?? [];
				const mode = secret?.defaultMode;
				if (!secret?.secretName) findings.push(`${id}: the volume ${ca.volume} must come from the Secret ${secretName} (${ca.value})`);
				else {
					if (secret.secretName !== secretName) findings.push(`${id}: the volume ${ca.volume} comes from ${secret.secretName}, not ${ca.value} ${secretName}`);
					if (items.length !== 1 || items[0].key !== 'ca.crt' || items[0].path !== 'ca.crt') findings.push(`${id}: the volume ${ca.volume} must project only ca.crt (items), so a Secret that also holds a private key never brings it`);
					if (secret.optional === true) findings.push(`${id}: the volume ${ca.volume} must not be optional: the pods wait for the Secret, as a host given ${ca.variable} refuses to start without the file`);
					if (!Number.isInteger(mode) || (mode & 0o333) !== 0) findings.push(`${id}: the volume ${ca.volume} must let no one write or execute ${ca.label} (defaultMode 0444), not ${mode === undefined ? 'the default 0644' : `0${Number(mode).toString(8)}`}`);
				}
			}
			for (const container of containers) {
				const cid = `${id} container ${container.name}`;
				const mounts = (container.volumeMounts ?? []).filter((mount) => mount.name === ca.volume || mount.mountPath === ca.path);
				const given = (container.env ?? []).filter((variable) => ca.pattern.test(variable.name ?? ''));
				if (!main(container)) {
					if (mounts.length) findings.push(`${cid}: mounts ${mounted(mounts)}; only the host's own container reads ${ca.label}`);
					if (given.length) findings.push(`${cid}: sets ${given.map((variable) => variable.name).join(', ')}; only the host's own container reads ${ca.label}`);
					continue;
				}
				if (mounts.length !== 1) findings.push(`${cid}: must mount ${ca.label} once at ${ca.path} (volume ${ca.volume}), as ${ca.value} names ${secretName}, not ${mounts.length} times`);
				else if (mounts[0].name !== ca.volume || mounts[0].mountPath !== ca.path || mounts[0].readOnly !== true || mounts[0].subPath || mounts[0].subPathExpr) {
					findings.push(`${cid}: must mount the volume ${ca.volume} read-only and whole (no subPath) at ${ca.path}`);
				}
				if (given.length !== 1) findings.push(`${cid}: must set ${ca.variable} once, to ${ca.file}, not ${given.length} times`);
				else if (given[0].valueFrom || given[0].value !== ca.file) findings.push(`${cid}: ${ca.variable} must be ${ca.file}, the file the volume ${ca.volume} mounts, not ${given[0].valueFrom ? '(not a literal)' : given[0].value}`);
			}
		}
	}
	const missing = API_HOSTS.filter((host) => !hosts.has(`${host}-deployment`));
	if (missing.length) findings.push(`the render holds no ${missing.map((host) => `Deployment/${host}-deployment`).join(', ')}`);
	return findings;
}

/**
 * ARV-092c (ASVS 5.0 V5.4.3, V12.3.1; CWE-269): the malware scanner beside Ariva.Api.Main. clamd speaks plain TCP, so it runs in
 * the api-main pod on its loopback (127.0.0.1:3310) and api-main's container is told that address; freshclam keeps the
 * signatures current where they come from the public mirrors or the site's; with a mounted set the site does.
 * - host, port: what api-main's container gets (MalwareScan__Host, MalwareScan__Port; ARV-092b refuses another host in k8s-prd).
 * - user, group: the image's clamav user and group, which own the signature files it ships.
 * - clamdResidentMiB: clamd's memory once loaded, measured with the pinned image (1.4.6: 990 MiB in its cgroup, 952 MiB resident,
 *   the same through a reload with ConcurrentDatabaseReload no); clamd's memory request holds it. freshclamMiB: a whole download
 *   measured within 128 MiB (TestDatabases no), so freshclam's limit is at least that.
 * - livenessDelaySeconds: the least delay before clamd's liveness probe starts, past the signature load (measured with 1.4.6: 18 s
 *   on one core, 43 s on half a core); livenessSeconds: the time without an answer (periodSeconds times failureThreshold) its liveness
 *   probe must exceed (a reload answers nothing for about 20 s). No startup probe: Kubernetes counts a container whose startup
 *   probe has not passed as not ready, so a restarting clamd would take api-main out of its Service.
 * - limits: the clamd.conf settings that must equal the repository's (Platform/Cloud/clamav/clamd.conf, ARV-092b), among them
 *   OfficialDatabaseOnly yes (no unsigned signature or allowlist file in the folder is loaded).
 * - signaturesMiB: the signature files of ClamAV 1.4.6, compressed (.cvd, and .cld with CompressLocalDatabase yes) and unpacked.
 *   The emptyDir holds the compressed set, and at the worst moment of an update the current set, the unpacked copy of the
 *   database being rebuilt and its new file at once (main: 85 + 226 + 85, with daily and bytecode); its size limit, which evicts
 *   the whole pod when it is passed, is at least twice that, for the databases' growth.
 * - clamd-tmp holds at most MaxQueue streams of StreamMaxLength (each queued item may hold a whole upload) and MaxThreads scans'
 *   unpacked content of MaxScanSize; its size limit, which evicts the whole pod when it is passed, is at least that sum.
 * - mounts: what each scanner container mounts, and nothing else (no Secret but the mirror's CA in freshclam).
 */
export const UPLOAD_SCAN = {
	host: '127.0.0.1',
	port: '3310',
	user: 100,
	group: 101,
	configMap: 'api-main-clamav-configmap',
	hostContainer: 'api-main-container',
	deployment: 'api-main-deployment',
	hpa: 'api-main-hpa',
	ping: ['clamdscan', '--config-file=/etc/clamav/clamd.conf', '--ping', '1'],
	commands: {
		clamd: ['clamd', '--foreground', '--config-file=/etc/clamav/clamd.conf'],
		freshclam: ['freshclam', '--daemon', '--foreground', '--stdout', '--config-file=/etc/clamav/freshclam.conf'],
		'clamav-signatures': ['cp', '-p', '/var/lib/clamav/main.cvd', '/var/lib/clamav/daily.cvd', '/var/lib/clamav/bytecode.cvd', '/signatures/']
	},
	clamdResidentMiB: 990,
	freshclamMiB: 128,
	signaturesMiB: { compressed: { main: 85, daily: 23, bytecode: 1 }, unpacked: { main: 226, daily: 83, bytecode: 2 } },
	maxThreads: 2,
	livenessDelaySeconds: 60,
	livenessSeconds: 60,
	limits: ['StreamMaxLength', 'MaxFileSize', 'MaxScanSize', 'AlertExceedsMax', 'OfficialDatabaseOnly'],
	mirrorCaPath: '/etc/ariva/mirror-ca',
	volumes: ['clamav-config', 'clamav-signatures', 'clamd-tmp', 'freshclam-tmp', 'clamav-init-tmp', 'clamav-mirror-ca']
};

/** A Kubernetes memory or CPU quantity in its base unit (bytes, cores), or NaN when it is not one. */
export function quantity(value) {
	const match = /^([0-9]+(?:\.[0-9]+)?)(m|k|M|G|T|Ki|Mi|Gi|Ti)?$/.exec(String(value ?? ''));
	if (!match) return Number.NaN;
	const scale = { m: 1e-3, k: 1e3, M: 1e6, G: 1e9, T: 1e12, Ki: 2 ** 10, Mi: 2 ** 20, Gi: 2 ** 30, Ti: 2 ** 40 }[match[2]] ?? 1;
	return Number(match[1]) * scale;
}

/** The directives of a clamd.conf or freshclam.conf text: name to the list of its values, comments and blank lines left out. */
export function clamavDirectives(text) {
	const directives = new Map();
	for (const raw of String(text ?? '').split(/\r?\n/)) {
		const line = raw.trim();
		if (!line || line.startsWith('#')) continue;
		const [name, ...rest] = line.split(/\s+/);
		directives.set(name, [...(directives.get(name) ?? []), rest.join(' ')]);
	}
	return directives;
}

/** A clamd.conf size (a number of bytes, or with K or M: kibibytes, mebibytes, as clamd reads it), in bytes; NaN otherwise. */
export function clamavSize(value) {
	const match = /^([0-9]+)([KkMm]?)$/.exec(String(value ?? ''));
	if (!match) return Number.NaN;
	return Number(match[1]) * ({ k: 2 ** 10, m: 2 ** 20 }[match[2].toLowerCase()] ?? 1);
}

/**
 * ARV-092c: the most the signature emptyDir holds, in MiB: the compressed set, plus, for the worst database, the unpacked copy an
 * update rebuilds and its new compressed file beside the current one (UPLOAD_SCAN.signaturesMiB).
 */
export function signatureVolumeWorstMiB({ compressed, unpacked } = UPLOAD_SCAN.signaturesMiB) {
	const set = Object.values(compressed).reduce((a, b) => a + b, 0);
	return set + Math.max(...Object.keys(compressed).map((name) => unpacked[name] + compressed[name]));
}

/** The mounts each scanner container must have, exactly (name, path, read-only), for the source and the mirror's CA. */
function scannerMounts(source, mirrorCa) {
	return {
		clamd: [['clamav-config', '/etc/clamav', true], ['clamav-signatures', '/var/lib/clamav', true], ['clamd-tmp', '/tmp', false]],
		freshclam: [['clamav-config', '/etc/clamav', true], ['clamav-signatures', '/var/lib/clamav', false], ['freshclam-tmp', '/tmp', false],
			...(source === 'mirror' && mirrorCa ? [['clamav-mirror-ca', UPLOAD_SCAN.mirrorCaPath, true]] : [])],
		'clamav-signatures': [['clamav-signatures', '/signatures', false], ['clamav-init-tmp', '/tmp', false]]
	};
}

/**
 * ARV-092c: the malware scanner of a render, against its values: { enabled, source (public, mirror or mounted), mirrorUrl,
 * mirrorCaSecretName, claimName, checksPerDay, image ({ repository, tag, digest }), limits (the repository clamd.conf's
 * directives, clamavDirectives) }. Read the manifests as Kubernetes does (YAML 1.1), for the file modes.
 * - On: api-main's pod runs api-main's own container first, then clamd and, with public or mirror, freshclam, after an init
 *   container that copies the image's signatures; with mounted neither. Each runs the values' image by digest, as the image's
 *   clamav user (100:101), non-root, with a read-only root, no privilege escalation, every capability dropped, RuntimeDefault
 *   seccomp, its fixed command, requests and limits of cpu and memory (clamd's memory request holds the measured resident set,
 *   freshclam's limit a whole download), exactly its own mounts (signatures read-only in clamd), no port, no readiness probe,
 *   no envFrom and no variable from a Secret (freshclam's CURL_CA_BUNDLE only, with the mirror's CA; never TZ: clamd stays in the
 *   image's UTC, as api-main reads its signatures' build time as UTC). clamd has a liveness probe only, which PINGs it through
 *   clamdscan, starts after the signature load and outlasts a reload; no readiness or startup probe, so a clamd that runs but
 *   does not answer only refuses that pod's uploads (one that cannot start at all leaves the pod NotReady). The pod gets no service account token;
 *   the volumes are the ConfigMap (read-only), the signatures (an emptyDir, or the claim read-only), emptyDirs with a size
 *   limit for each /tmp, and the mirror's CA (ca.crt only, 0444, not optional). api-main's container gets MalwareScan__Host
 *   127.0.0.1 and MalwareScan__Port 3310, and mounts none of the scanner's volumes. The ConfigMap's clamd.conf listens on
 *   127.0.0.1:3310 only (no LocalSocket), in the foreground, without concurrent reloads, checks its signatures (SelfCheck) and
 *   has the repository's limits; freshclam.conf (public or mirror only) names its mirror, tests no database by loading it,
 *   notifies no clamd and checks checksPerDay times a day. The HPA reads api-main's own container only.
 * - Off: no scanner anywhere, no ConfigMap, no MalwareScan__Host from the chart.
 * - Always: no other workload (another host, the web pod, the simulator, the jobs) runs a ClamAV image or gets MalwareScan__*.
 */
export function uploadScanFindings(docs, { enabled, source = '', mirrorUrl = '', mirrorCaSecretName = '', claimName = '', checksPerDay = 12, image = {}, limits, containerMetrics = true }) {
	const findings = [];
	const pinned = `${image.repository}:${image.tag}@${image.digest}`;
	const freshclam = enabled && ['public', 'mirror'].includes(source);
	const isScanner = (container) => imageName(container.image ?? '') === 'clamav';
	const workloads = docs.filter((doc) => doc && WORKLOADS.has(doc.kind));
	const configMaps = docs.filter((doc) => doc?.kind === 'ConfigMap');
	const deployment = workloads.find((doc) => doc.kind === 'Deployment' && doc.metadata?.name === UPLOAD_SCAN.deployment);
	const scanVariables = (container) => {
		const names = (container.env ?? []).map((variable) => String(variable.name ?? '')).filter((name) => /^MalwareScan__/i.test(name));
		for (const from of container.envFrom ?? []) {
			const map = configMaps.find((doc) => doc.metadata?.name === from.configMapRef?.name);
			names.push(...Object.keys(map?.data ?? {}).filter((key) => /^MalwareScan__/i.test(key)));
		}
		return names;
	};
	// Every workload but api-main's own container: no scanner image, no scanner address.
	for (const doc of workloads) {
		const id = `${doc.kind}/${doc.metadata?.name ?? '?'}`;
		const pod = podSpecOf(doc) ?? {};
		const main = doc === deployment;
		for (const container of [...(pod.initContainers ?? []), ...(pod.containers ?? [])]) {
			const cid = `${id} container ${container.name}`;
			if (!main && isScanner(container)) findings.push(`${cid}: runs ${container.image}; only api-main's pod runs the scanner, on its loopback (ARV-092c)`);
			const variables = scanVariables(container);
			if (variables.length && !(main && container.name === UPLOAD_SCAN.hostContainer)) {
				findings.push(`${cid}: gets ${[...new Set(variables)].join(', ')}; only api-main's own container is told the scanner's address`);
			}
		}
	}
	const scanMap = configMaps.find((doc) => doc.metadata?.name === UPLOAD_SCAN.configMap);
	if (!deployment) return [...findings, `the render holds no Deployment/${UPLOAD_SCAN.deployment}`];
	const id = `Deployment/${UPLOAD_SCAN.deployment}`;
	const pod = podSpecOf(deployment) ?? {};
	const containers = pod.containers ?? [];
	const inits = pod.initContainers ?? [];
	const host = containers.find((container) => container.name === UPLOAD_SCAN.hostContainer);
	const scanners = [...inits, ...containers].filter(isScanner);
	const volumes = new Map((pod.volumes ?? []).map((volume) => [volume.name, volume]));
	if (!enabled) {
		if (scanners.length) findings.push(`${id}: runs ${scanners.map((container) => container.name).join(', ')} although uploadScan is off`);
		if (scanMap) findings.push(`ConfigMap/${UPLOAD_SCAN.configMap}: rendered although uploadScan is off`);
		if (host && envValueOf(host, docs, 'MalwareScan__Host') !== undefined) findings.push(`${id} container ${UPLOAD_SCAN.hostContainer}: the chart sets MalwareScan__Host although uploadScan is off`);
		for (const name of UPLOAD_SCAN.volumes) if (volumes.has(name)) findings.push(`${id}: has the volume ${name} although uploadScan is off`);
		return findings;
	}
	// The pod: api-main's container first, then the scanner's; no service account token in a pod that runs third-party code.
	if (containers[0]?.name !== UPLOAD_SCAN.hostContainer) findings.push(`${id}: ${UPLOAD_SCAN.hostContainer} must be the pod's first container (the scanner's run beside it)`);
	if (pod.automountServiceAccountToken !== false) findings.push(`${id}: must set automountServiceAccountToken false: the scanner's containers never get the service account token`);
	const expected = { clamd: 'containers', ...(freshclam ? { freshclam: 'containers', 'clamav-signatures': 'initContainers' } : {}) };
	for (const [name, list] of Object.entries(expected)) {
		const found = (list === 'containers' ? containers : inits).filter((container) => container.name === name);
		if (found.length !== 1) findings.push(`${id}: needs one ${name} in ${list}, not ${found.length}`);
	}
	for (const container of scanners) {
		const where = inits.includes(container) ? 'initContainers' : 'containers';
		if (expected[container.name] !== where) findings.push(`${id} container ${container.name}: is not a scanner container of source ${source} (${Object.keys(expected).join(', ')})`);
	}
	if (host) {
		for (const [name, value] of [['MalwareScan__Host', UPLOAD_SCAN.host], ['MalwareScan__Port', UPLOAD_SCAN.port]]) {
			const given = envValueOf(host, docs, name);
			if (given !== value) findings.push(`${id} container ${UPLOAD_SCAN.hostContainer}: must set ${name} to ${value}, clamd on the pod's loopback, not ${given === null ? '(not a literal)' : given ?? 'nothing'}`);
		}
		const mounted = (host.volumeMounts ?? []).filter((mount) => UPLOAD_SCAN.volumes.includes(mount.name));
		if (mounted.length) findings.push(`${id} container ${UPLOAD_SCAN.hostContainer}: mounts the scanner's ${mounted.map((mount) => mount.name).join(', ')}`);
	} else findings.push(`${id}: has no container ${UPLOAD_SCAN.hostContainer}`);
	const mounts = scannerMounts(source, mirrorCaSecretName);
	for (const container of scanners.filter((candidate) => expected[candidate.name])) {
		const cid = `${id} container ${container.name}`;
		const sc = container.securityContext ?? {};
		if (container.image !== pinned) findings.push(`${cid}: must run ${pinned} (uploadScan.image, pinned by digest), not ${container.image}`);
		if (sc.runAsNonRoot !== true || sc.runAsUser !== UPLOAD_SCAN.user || sc.runAsGroup !== UPLOAD_SCAN.group) {
			findings.push(`${cid}: must run as the image's clamav user, runAsNonRoot true, runAsUser ${UPLOAD_SCAN.user} and runAsGroup ${UPLOAD_SCAN.group}`);
		}
		if (sc.readOnlyRootFilesystem !== true) findings.push(`${cid}: readOnlyRootFilesystem must be true`);
		if (sc.allowPrivilegeEscalation !== false || sc.privileged === true) findings.push(`${cid}: allowPrivilegeEscalation must be false and the container never privileged`);
		if (!(sc.capabilities?.drop ?? []).includes('ALL') || (sc.capabilities?.add ?? []).length) findings.push(`${cid}: must drop every capability and add none`);
		if ((sc.seccompProfile?.type ?? pod.securityContext?.seccompProfile?.type) !== 'RuntimeDefault') findings.push(`${cid}: seccompProfile must be RuntimeDefault`);
		if (JSON.stringify(container.command ?? []) !== JSON.stringify(UPLOAD_SCAN.commands[container.name]) || (container.args ?? []).length) {
			findings.push(`${cid}: must run ${UPLOAD_SCAN.commands[container.name].join(' ')}, and nothing else`);
		}
		const resources = container.resources ?? {};
		const amount = (part, kind) => quantity(resources[part]?.[kind]);
		if (['requests', 'limits'].some((part) => ['cpu', 'memory'].some((kind) => Number.isNaN(amount(part, kind))))) {
			findings.push(`${cid}: needs requests and limits of cpu and memory`);
		} else if (amount('limits', 'memory') < amount('requests', 'memory') || amount('limits', 'cpu') < amount('requests', 'cpu')) {
			findings.push(`${cid}: its limits must not be below its requests`);
		}
		if (container.name === 'clamd' && !(amount('requests', 'memory') >= UPLOAD_SCAN.clamdResidentMiB * 2 ** 20)) {
			findings.push(`${cid}: its memory request must hold the signatures clamd keeps loaded (${UPLOAD_SCAN.clamdResidentMiB} MiB measured)`);
		}
		if (container.name === 'freshclam' && !(amount('limits', 'memory') >= UPLOAD_SCAN.freshclamMiB * 2 ** 20)) {
			findings.push(`${cid}: its memory limit must hold a whole download (${UPLOAD_SCAN.freshclamMiB} MiB measured)`);
		}
		const wanted = mounts[container.name].map(([name, mountPath, readOnly]) => `${name}:${mountPath}:${readOnly}`).sort();
		const given = (container.volumeMounts ?? []).map((mount) => `${mount.name}:${mount.mountPath}:${mount.readOnly === true}${mount.subPath ? ':subPath' : ''}`).sort();
		if (JSON.stringify(given) !== JSON.stringify(wanted)) findings.push(`${cid}: must mount exactly ${wanted.join(', ')} (name:path:read-only), not ${given.join(', ') || 'nothing'}`);
		if ((container.ports ?? []).length) findings.push(`${cid}: declares a port; clamd listens on the pod's loopback only, which no Service selects`);
		if (container.readinessProbe) findings.push(`${cid}: has a readiness probe; a scanner that does not answer must not take api-main out of its Service (uploads fail closed by themselves)`);
		if ((container.envFrom ?? []).length) findings.push(`${cid}: has envFrom; the scanner's containers get no host's settings`);
		const allowedEnv = container.name === 'freshclam' && source === 'mirror' && mirrorCaSecretName ? { CURL_CA_BUNDLE: `${UPLOAD_SCAN.mirrorCaPath}/ca.crt` } : {};
		for (const variable of container.env ?? []) {
			if (/^TZ$/i.test(variable.name ?? '')) findings.push(`${cid}: sets ${variable.name}; clamd reports its signatures' build time in its local time, which Ariva.Api.Main reads as UTC, so the scanner keeps the image's UTC`);
			else if (variable.valueFrom || allowedEnv[variable.name] !== variable.value) findings.push(`${cid}: sets ${variable.name}; the scanner's containers get no variable${variable.valueFrom ? ' from a Secret or a field' : ' but the mirror CA\'s CURL_CA_BUNDLE'}`);
		}
		for (const [name, value] of Object.entries(allowedEnv)) {
			if ((container.env ?? []).filter((variable) => variable.name === name && variable.value === value).length !== 1) findings.push(`${cid}: must set ${name} to ${value}, the mirror's CA, once`);
		}
		if (container.name === 'clamd') {
			if (container.startupProbe) findings.push(`${cid}: has a startup probe; Kubernetes counts a container whose startup probe has not passed as not ready, so a restarting clamd would take api-main out of its Service`);
			const liveness = container.livenessProbe;
			if (!liveness || JSON.stringify(liveness.exec?.command ?? []) !== JSON.stringify(UPLOAD_SCAN.ping)) findings.push(`${cid}: needs a livenessProbe that runs ${UPLOAD_SCAN.ping.join(' ')}`);
			else {
				if (!(Number(liveness.initialDelaySeconds ?? 0) >= UPLOAD_SCAN.livenessDelaySeconds)) {
					findings.push(`${cid}: its livenessProbe must wait at least ${UPLOAD_SCAN.livenessDelaySeconds} s (initialDelaySeconds), past the signature load on a slow node`);
				}
				if (!(Number(liveness.periodSeconds ?? 10) * Number(liveness.failureThreshold ?? 3) > UPLOAD_SCAN.livenessSeconds)) {
					findings.push(`${cid}: its livenessProbe must tolerate more than ${UPLOAD_SCAN.livenessSeconds} s without an answer (periodSeconds times failureThreshold), longer than a reload`);
				}
			}
		} else {
			for (const probe of ['startupProbe', 'livenessProbe']) if (container[probe]) findings.push(`${cid}: has a ${probe}; only clamd is probed`);
		}
	}
	// The volumes the scanner reads.
	const volumeFindings = (name, check, what) => {
		const volume = volumes.get(name);
		if (!volume) findings.push(`${id}: needs the volume ${name} (${what})`);
		else if (!check(volume)) findings.push(`${id}: the volume ${name} must be ${what}`);
	};
	volumeFindings('clamav-config', (v) => v.configMap?.name === UPLOAD_SCAN.configMap && Number.isInteger(v.configMap.defaultMode) && (v.configMap.defaultMode & 0o333) === 0,
		`the ConfigMap ${UPLOAD_SCAN.configMap}, mode 0444`);
	if (source === 'mounted') {
		volumeFindings('clamav-signatures', (v) => v.persistentVolumeClaim?.claimName === claimName && v.persistentVolumeClaim.readOnly === true, `the claim ${claimName} (uploadScan.signatures.claimName), read-only`);
	} else {
		const worst = signatureVolumeWorstMiB();
		volumeFindings('clamav-signatures', (v) => v.emptyDir && quantity(v.emptyDir.sizeLimit) >= 2 * worst * 2 ** 20,
			`an emptyDir whose sizeLimit is at least twice the most an update holds there (${worst} MiB; passing it evicts the whole pod)`);
	}
	// clamd's scratch space: every queued item may hold a whole stream, every running scan what an archive unpacks to.
	const clamdConf = clamavDirectives(scanMap?.data?.['clamd.conf']);
	const setting = (name) => (clamdConf.get(name) ?? [])[0];
	const threads = Number(setting('MaxThreads'));
	const queue = Number(setting('MaxQueue'));
	const scratch = queue * clamavSize(setting('StreamMaxLength')) + threads * clamavSize(setting('MaxScanSize'));
	if (!(Number.isInteger(threads) && threads >= 1 && threads <= UPLOAD_SCAN.maxThreads) || !(Number.isInteger(queue) && queue >= threads)) {
		findings.push(`ConfigMap/${UPLOAD_SCAN.configMap}: clamd.conf must set MaxThreads 1 to ${UPLOAD_SCAN.maxThreads} (Ariva.Api.Main scans one upload at a time per pod) and MaxQueue at least MaxThreads, not ${setting('MaxThreads') ?? 'the default 10'} and ${setting('MaxQueue') ?? 'the default 100'}`);
	} else {
		volumeFindings('clamd-tmp', (v) => v.emptyDir && quantity(v.emptyDir.sizeLimit) >= scratch,
			`an emptyDir whose sizeLimit holds MaxQueue ${queue} times StreamMaxLength plus MaxThreads ${threads} times MaxScanSize (${Math.round(scratch / 2 ** 20)} MiB; passing it evicts the whole pod)`);
	}
	for (const name of ['freshclam-tmp', 'clamav-init-tmp']) {
		if (freshclam) volumeFindings(name, (v) => v.emptyDir && quantity(v.emptyDir.sizeLimit) > 0, 'an emptyDir with a sizeLimit');
		else if (volumes.has(name)) findings.push(`${id}: has the volume ${name} without freshclam`);
	}
	if (source === 'mirror' && mirrorCaSecretName) {
		volumeFindings('clamav-mirror-ca', (v) => {
			const items = v.secret?.items ?? [];
			return v.secret?.secretName === mirrorCaSecretName && v.secret.optional !== true && Number.isInteger(v.secret.defaultMode) && (v.secret.defaultMode & 0o333) === 0 &&
				items.length === 1 && items[0].key === 'ca.crt' && items[0].path === 'ca.crt';
		}, `the Secret ${mirrorCaSecretName} (uploadScan.signatures.mirrorCaSecretName), ca.crt only, 0444, not optional`);
	} else if (volumes.has('clamav-mirror-ca')) findings.push(`${id}: has the volume clamav-mirror-ca without a mirror CA`);
	// The configuration.
	if (!scanMap) findings.push(`the render holds no ConfigMap/${UPLOAD_SCAN.configMap}`);
	else {
		const mid = `ConfigMap/${UPLOAD_SCAN.configMap}`;
		const clamd = clamavDirectives(scanMap.data?.['clamd.conf']);
		const one = (directives, name, value, file, why) => {
			const values = directives.get(name) ?? [];
			if (values.length !== 1 || values[0] !== value) findings.push(`${mid}: ${file} must set ${name} ${value} once (${why}), not ${values.join(', ') || 'nothing'}`);
		};
		one(clamd, 'TCPAddr', UPLOAD_SCAN.host, 'clamd.conf', "the pod's loopback only");
		one(clamd, 'TCPSocket', UPLOAD_SCAN.port, 'clamd.conf', 'the port api-main is given');
		one(clamd, 'Foreground', 'yes', 'clamd.conf', 'the container is clamd');
		one(clamd, 'ConcurrentDatabaseReload', 'no', 'clamd.conf', 'a reload never holds two signature sets');
		one(clamd, 'DatabaseDirectory', '/var/lib/clamav', 'clamd.conf', 'the signature volume');
		one(clamd, 'TemporaryDirectory', '/tmp', 'clamd.conf', 'its emptyDir');
		one(clamd, 'OfficialDatabaseOnly', 'yes', 'clamd.conf', 'no unsigned signature or allowlist file in the folder is loaded');
		const selfCheck = Number((clamd.get('SelfCheck') ?? [])[0]);
		if ((clamd.get('SelfCheck') ?? []).length !== 1 || !(selfCheck >= 60 && selfCheck <= 3600)) findings.push(`${mid}: clamd.conf must set SelfCheck once, 60 to 3600 seconds, so clamd reloads changed signatures`);
		for (const name of ['LocalSocket', 'User', 'LogFile', 'PidFile']) if (clamd.has(name)) findings.push(`${mid}: clamd.conf sets ${name}; clamd here runs non-root on TCP only and writes nothing but its scans' files in /tmp`);
		for (const name of UPLOAD_SCAN.limits) {
			const want = limits?.get(name) ?? [];
			if (JSON.stringify(clamd.get(name) ?? []) !== JSON.stringify(want)) findings.push(`${mid}: clamd.conf must set ${name} ${want.join(', ') || '(missing from the repository file)'} as Platform/Cloud/clamav/clamd.conf does, not ${(clamd.get(name) ?? []).join(', ') || 'nothing'}`);
		}
		const fresh = scanMap.data?.['freshclam.conf'];
		if (!freshclam) {
			if (fresh !== undefined) findings.push(`${mid}: holds freshclam.conf although the signatures are mounted`);
		} else if (fresh === undefined) findings.push(`${mid}: needs freshclam.conf with source ${source}`);
		else {
			const directives = clamavDirectives(fresh);
			one(directives, 'DatabaseDirectory', '/var/lib/clamav', 'freshclam.conf', 'the signature volume');
			one(directives, 'TestDatabases', 'no', 'freshclam.conf', 'testing loads a whole second set, about 1 GiB');
			one(directives, 'CompressLocalDatabase', 'yes', 'freshclam.conf', 'a database rebuilt from updates stays compressed, so an update fits the signature emptyDir');
			one(directives, 'Checks', String(checksPerDay), 'freshclam.conf', 'uploadScan.signatures.checksPerDay');
			if (directives.has('NotifyClamd')) findings.push(`${mid}: freshclam.conf sets NotifyClamd; clamd's SelfCheck reloads the signatures`);
			if (source === 'public') {
				one(directives, 'DatabaseMirror', 'database.clamav.net', 'freshclam.conf', 'the ClamAV mirrors');
				if (directives.has('PrivateMirror')) findings.push(`${mid}: freshclam.conf sets PrivateMirror with source public`);
			} else {
				one(directives, 'PrivateMirror', mirrorUrl, 'freshclam.conf', 'uploadScan.signatures.mirrorUrl');
				if (directives.has('DatabaseMirror')) findings.push(`${mid}: freshclam.conf sets DatabaseMirror with source mirror`);
			}
		}
	}
	// The HPA scales on api-main's own container: clamd's resident signatures and reloads are not api-main's load.
	// Below Kubernetes 1.27 (containerMetrics false) the API server drops a ContainerResource metric and refuses the HPA, so the
	// chart keeps the pod's Resource metrics there.
	const hpa = docs.find((doc) => doc?.kind === 'HorizontalPodAutoscaler' && doc.metadata?.name === UPLOAD_SCAN.hpa);
	const metrics = hpa?.spec?.metrics ?? [];
	if (containerMetrics && (!metrics.length || metrics.some((metric) => metric.type !== 'ContainerResource' || metric.containerResource?.container !== UPLOAD_SCAN.hostContainer))) {
		findings.push(`HorizontalPodAutoscaler/${UPLOAD_SCAN.hpa}: must scale on ${UPLOAD_SCAN.hostContainer}'s own cpu and memory (ContainerResource), not the whole pod's, which counts clamd's resident signatures`);
	}
	if (!containerMetrics && (!metrics.length || metrics.some((metric) => metric.type !== 'Resource'))) {
		findings.push(`HorizontalPodAutoscaler/${UPLOAD_SCAN.hpa}: below Kubernetes 1.27 must scale on the pod's Resource metrics, as the API server refuses a ContainerResource metric there`);
	}
	return findings;
}

/**
 * ARV-209 (crypto inventory K40; ASVS 5.0 V13.3.1): the flags through which a release gives Helm or Helmfile a value. Helm keeps
 * every value in its release record (a Secret of the release's namespace), so a credential given this way outlives the run and
 * is readable by anyone who may read the namespace's Secrets.
 */
export const RELEASE_VALUE_FLAGS = ['--set', '--set-string', '--set-file', '--set-json', '--set-literal', '--state-values-set', '--state-values-set-string'];

/**
 * A value key (its last part) that names a credential: one ending in password, passphrase, pwd, pw, secret, token, cred(s),
 * credential(s) or key (adminPassword, clientSecret, apiToken, privateKey, registry.pw). A key that only mentions one before its
 * end names something else: a Secret's name (caSecretName), a switch (tokenInQuery), a threshold (keyThreshold).
 */
const RELEASE_CREDENTIAL_KEY = /(?:password|passwd|passphrase|pwd|pw|secret|token|creds?|credentials?|key)$/i;

/**
 * The words of a variable's name that make it a credential's, wherever they stand in it (ACR_PASSWORD_B64, TOKEN_VALUE, ACR-PASS,
 * REGISTRY_PW, DOCKER_CRED). A part written in one case only (ACRPASSWORD, REGTOKEN, accesstoken) shows no word boundary, so a
 * credential's word anywhere inside it counts too; a part in mixed case is split at its capitals (AcrPassword, ACRPassword).
 */
const CREDENTIAL_WORDS = ['password', 'passwd', 'passphrase', 'pass', 'pwd', 'pw', 'secret', 'secrets', 'token', 'credential', 'credentials', 'cred', 'creds', 'key', 'apikey', 'pat', 'auth'];

const nameParts = (name) => String(name).replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').split(/[^A-Za-z0-9]+/).filter(Boolean);
const namesCredential = (name) => nameParts(name).some((part) => {
	const word = part.toLowerCase();
	const oneCase = part === part.toUpperCase() || part === word;
	return CREDENTIAL_WORDS.includes(word) || (oneCase && CREDENTIAL_WORDS.some((credential) => word.includes(credential)));
});

/**
 * Azure DevOps predefined variables (Build.BuildNumber, System.DefaultWorkingDirectory, Agent.TempDirectory), exempt only when
 * their name holds no credential's word: System.AccessToken is the pipeline's own token.
 */
const AZURE_PREDEFINED = /^(Build|System|Agent|Pipeline|Release|Environment|Resources|Strategy)\./i;

/**
 * The flags whose next argument is a values file: helm's -f and --values; Helmfile's --state-values-file and --values. Helmfile's
 * -f and --file name its state file, which sets any value, so it is read with the same path test.
 */
const HELM_VALUES_FLAGS = ['-f', '--values', '--state-values-file'];
const HELMFILE_VALUES_FLAGS = ['--state-values-file', '--values'];
const HELMFILE_STATE_FLAGS = ['-f', '--file'];

/** The words that end one shell command of a line and start the next. */
const COMMAND_SEPARATORS = new Set(['|', '||', '&&', ';', '&']);

/**
 * The `${{ }}` and `$[ ]` expressions of a text, each with its start, its end and its body: a `${{` ends at the first `}}`, a
 * `$[` at its matching `]`, and a single-quoted string inside one (`''` its quote) is skipped, so `${{ format('{0}', x) }}` and
 * `$[ coalesce(variables.A, '') ]` are each one expression. One left open runs to the end of the text.
 */
function expressions(text) {
	const found = [];
	let i = 0;
	while (i < text.length) {
		const kind = text.startsWith('${{', i) ? '${{' : text.startsWith('$[', i) ? '$[' : null;
		if (!kind) {
			i++;
			continue;
		}
		let j = i + kind.length;
		let depth = 0;
		let quoted = false;
		for (; j < text.length; j++) {
			const char = text[j];
			if (quoted) {
				if (char === "'") {
					if (text[j + 1] === "'") j++;
					else quoted = false;
				}
				continue;
			}
			if (char === "'") quoted = true;
			else if (kind === '${{' && text.startsWith('}}', j)) break;
			else if (kind === '$[' && char === '[') depth++;
			else if (kind === '$[' && char === ']') {
				if (depth === 0) break;
				depth--;
			}
		}
		const end = Math.min(text.length, j + (kind === '${{' ? 2 : 1));
		found.push({ kind, start: i, end, body: text.slice(i + kind.length, Math.min(j, text.length)) });
		i = end;
	}
	return found;
}

/** An expression's body with its single-quoted strings emptied, so only its identifiers are left to read. */
const withoutStrings = (body) => body.replace(/'(?:[^']|'')*'/g, "''");

/**
 * A text cut at each separator character that is not escaped with a backslash and not inside an expression or a `$( )` macro, so
 * `a=${{ format('{0}', x) }},b=1` gives two pairs.
 */
function splitOutside(text, separator) {
	const spans = expressions(text);
	const parts = [''];
	for (let i = 0; i < text.length; i++) {
		const span = spans.find((candidate) => candidate.start === i);
		if (span) {
			parts[parts.length - 1] += text.slice(span.start, span.end);
			i = span.end - 1;
			continue;
		}
		if (text.startsWith('$(', i)) {
			const close = text.indexOf(')', i);
			const end = close < 0 ? text.length : close + 1;
			parts[parts.length - 1] += text.slice(i, end);
			i = end - 1;
			continue;
		}
		if (text[i] === separator && text[i - 1] !== '\\') parts.push('');
		else parts[parts.length - 1] += text[i];
	}
	return parts;
}

/**
 * A line's words as the shell reads them: whitespace splits outside quotes, a quoted part stays in its word, a backslash keeps the
 * next character, and an expression stays one word whatever its quotes and spaces (its whitespace removed).
 */
function words(line) {
	const text = line.replace(/\\(?= |$)/g, ' ');
	const spans = expressions(text);
	const result = [];
	let current = '';
	let started = false;
	let quote = null;
	for (let i = 0; i < text.length; i++) {
		const span = spans.find((candidate) => candidate.start === i);
		if (span) {
			current += text.slice(span.start, span.end).replace(/\s+/g, '');
			started = true;
			i = span.end - 1;
			continue;
		}
		const char = text[i];
		if (char === '\\' && quote !== "'" && i + 1 < text.length) {
			current += char + text[i + 1];
			started = true;
			i++;
			continue;
		}
		if (quote) {
			current += char;
			if (char === quote) quote = null;
			continue;
		}
		if (char === '"' || char === "'") {
			quote = char;
			current += char;
			started = true;
			continue;
		}
		if (/\s/.test(char)) {
			if (started) result.push(current);
			current = '';
			started = false;
			continue;
		}
		current += char;
		started = true;
	}
	if (started) result.push(current);
	return result;
}

const unquote = (value) => value.replace(/^["']|["']$/g, '');
const indentOf = (line) => line.match(/^\s*/)[0].length;

/**
 * The variables a release file declares inline with a literal value (an Azure DevOps `variables:` list entry `- name: X`
 * followed by `value:` holding no `$(`, `$[` or `${{`): a value read from one of them is the file's own text, not a secret. A
 * variable group's (`- group:`) are not, and a group listed after a name may set it again (the later definition wins), so a
 * group ends every name declared before it. Names are kept in lower case, as Azure DevOps reads them.
 */
function inlineVariables(text) {
	const names = new Set();
	const lines = text.split(/\r?\n/);
	lines.forEach((line, index) => {
		if (/^\s*-\s*group\s*:/.test(line)) {
			names.clear();
			return;
		}
		const name = line.match(/^\s*-\s*name:\s*['"]?([A-Za-z0-9_.-]+)['"]?\s*(?:#.*)?$/)?.[1];
		if (!name) return;
		const rest = lines.slice(index + 1);
		const at = rest.findIndex((candidate) => candidate.trim() !== '' && !candidate.trim().startsWith('#'));
		const valueLine = at < 0 ? null : rest[at].match(/^(\s*)value:\s*(.*)$/);
		if (!valueLine) return;
		// The value with the lines of its block scalar, if any.
		const below = rest.slice(at + 1);
		const end = below.findIndex((candidate) => candidate.trim() !== '' && indentOf(candidate) <= valueLine[1].length);
		const value = [valueLine[2], ...below.slice(0, end < 0 ? below.length : end)].join('\n');
		if (/\$\(|\$\[|\$\{\{/.test(value)) names.delete(name.toLowerCase());
		else names.add(name.toLowerCase());
	});
	return names;
}

/**
 * The env entries of a file, name and value: every `env:` block at any indentation (a step's, a job's, the workflow's), its
 * direct children with their continuation lines, and a flow mapping `env: { A: b }`.
 */
function envEntries(text) {
	const entries = [];
	const lines = text.split(/\r?\n/);
	lines.forEach((line, index) => {
		const head = line.match(/^(\s*(?:-\s+)?)env:\s*(.*?)\s*$/);
		if (!head) return;
		const own = head[2].replace(/(^|\s)#.*$/, '').trim();
		if (own.startsWith('{')) {
			for (const pair of splitOutside(own.replace(/^\{|\}$/g, ''), ',')) {
				const entry = pair.match(/^\s*['"]?([A-Za-z_][A-Za-z0-9_.-]*)['"]?\s*:\s*(.*)$/);
				if (entry) entries.push({ name: entry[1], value: entry[2].trim() });
			}
			return;
		}
		if (own !== '') return;
		const column = head[1].length;
		const body = [];
		for (const candidate of lines.slice(index + 1)) {
			if (candidate.trim() !== '' && !candidate.trim().startsWith('#') && indentOf(candidate) <= column) break;
			body.push(candidate);
		}
		const first = body.find((candidate) => candidate.trim() !== '' && !candidate.trim().startsWith('#'));
		if (!first) return;
		const childIndent = indentOf(first);
		let current = null;
		for (const candidate of body) {
			const entry = indentOf(candidate) === childIndent ? candidate.match(/^\s*['"]?([A-Za-z_][A-Za-z0-9_.-]*)['"]?\s*:(?:\s+(.*))?$/) : null;
			if (entry) {
				current = { name: entry[1], value: entry[2] ?? '' };
				entries.push(current);
			} else if (current && candidate.trim() !== '') {
				current.value += `\n${candidate}`;
			}
		}
	});
	return entries;
}

/**
 * Why a value given to Helm reads a secret, or null:
 * - a GitHub expression (`${{ }}`) whose body names the `secrets` context anywhere outside its strings, whatever the function
 *   around it (`secrets.X || ''`, `format('{0}', secrets.X)`, `fromJSON(secrets.X).pw`, `toJSON(secrets)`);
 * - an Azure DevOps variable: a `$(X)` macro, or `variables.X` or `variables['X']` anywhere inside a `$[ ]` or `${{ }}` body,
 *   other than a predefined one whose name holds no credential's word or one the file declares inline with a literal value
 *   (inlineVariables), since a variable group's may be named anything;
 * - a variable an env entry of the file maps from a secret (envSecrets: `$NAME`, `${NAME}`, `$env:NAME`, `${{ env.NAME }}`);
 * - any other variable (`${{ env.X }}`, `${{ steps.s.outputs.X }}`, `${{ parameters.X }}`, a shell `$X`, `${X}` or `${X:-}`)
 *   any part of whose name holds a credential's word (namesCredential).
 */
function secretReference(value, inline, envSecrets) {
	const spans = expressions(value);
	for (const { kind, body } of spans) {
		if (kind !== '${{' || !/\bsecrets\b/i.test(withoutStrings(body))) continue;
		const named = body.match(/\bsecrets\s*(?:\.\s*([A-Za-z0-9_-]+)|\[\s*'([^']+)'\s*\])/i);
		return named ? `the GitHub secret ${named[1] ?? named[2]}` : 'the GitHub secrets context';
	}
	const azure = [...value.matchAll(/\$\(([^)\s]+)\)/g)].map((match) => match[1]);
	for (const { body } of spans) {
		for (const match of body.matchAll(/\bvariables\s*(?:\.\s*([A-Za-z0-9_.-]*[A-Za-z0-9_])|\[\s*['"]([^'"]+)['"]\s*\])/gi)) azure.push(match[1] ?? match[2]);
	}
	for (const name of azure) {
		if (AZURE_PREDEFINED.test(name) ? !namesCredential(name) : inline.has(name.toLowerCase())) continue;
		return AZURE_PREDEFINED.test(name) ? `the predefined variable ${name}, whose name is a credential's`
			: `the pipeline variable ${name}, which this file does not declare with a literal value (a variable group's may be a secret whatever its name)`;
	}
	const others = [];
	for (const { kind, body } of spans) {
		if (kind !== '${{') continue;
		for (const match of body.matchAll(/\b(env|vars|inputs|steps|needs|jobs|github|matrix|strategy|runner|parameters)\s*(?:\.\s*([A-Za-z0-9_.-]*[A-Za-z0-9_])|\[\s*'([^']+)'\s*\])/gi)) {
			others.push({ context: match[1].toLowerCase(), name: match[2] ?? match[3] });
		}
	}
	for (const match of value.matchAll(/\$\{[#!]?([A-Za-z_][A-Za-z0-9_]*)/g)) others.push({ context: 'shell', name: match[1] });
	for (const match of value.matchAll(/\$([A-Za-z_][A-Za-z0-9_]*)/g)) others.push({ context: 'shell', name: match[1] });
	for (const match of value.matchAll(/\$env:([A-Za-z_][A-Za-z0-9_]*)/gi)) others.push({ context: 'shell', name: match[1] });
	for (const { context, name } of others) {
		if ((context === 'shell' || context === 'env') && envSecrets.has(name)) return `the variable ${name}, which an env entry of this file maps from a secret`;
		// Every part of the name counts: a step or job named for a credential (steps.creds.outputs.value) gives one.
		if (namesCredential(name)) return `the variable ${name}, whose name is a credential's`;
	}
	return null;
}

/**
 * Why a file given to Helm or Helmfile is one the pipeline writes, or null: standard input (`-`), a process substitution
 * (`<(...)`), a path with a variable, an absolute or home path, or the pipeline's `_cicd` folder; each comma-separated path is
 * read.
 */
function pipelinePath(value) {
	for (const one of value.split(',').map((part) => part.trim())) {
		if (one === '-') return 'standard input';
		if (one.startsWith('<(')) return 'a process substitution';
		if (one.includes('$')) return 'a path with a variable';
		if (/^[/~]|^[A-Za-z]:[\\/]|(^|[\\/])_cicd([\\/]|$)/.test(one)) return 'not a repository file';
	}
	return null;
}

/**
 * ARV-209: the values a release file gives Helm or Helmfile that carry a credential, matched by the key's name and by what the
 * value reads:
 * - every pair of the value flags (RELEASE_VALUE_FLAGS, a comma-separated argument or one flag at a time, `--flag value` or
 *   `--flag=value`, a comma inside an expression or a macro kept), of a HelmDeploy task's `overrideValues:` input (one line or a
 *   block, pairs by comma or line), of Helmfile's `--args` string and of a HelmDeploy task's `arguments:` input (both read as
 *   helm command lines): a key whose last part names a credential with any value but the empty one (which turns a chart's copy
 *   off), any --set-file of such a key, a --set-file of a file the pipeline writes (pipelinePath), or a value that reads a
 *   secret (secretReference, with the env entries the file maps from a secret);
 * - a file the pipeline writes (pipelinePath) given to helm as a values file (`-f`, `--values`), to Helmfile as a values file
 *   (`--state-values-file`, `--values`) or a state file (`-f`, `--file`), or as a HelmDeploy task's `valueFile:`, which may hold
 *   anything: only the repository's own files are given to Helm.
 * A values or state file flag counts only in a helm or helmfile command (a word of the shell command that is helm or helmfile,
 * by name, by path or as `$HELM` and `$HELMFILE`), read per shell command of the line (`|`, `||`, `&&`, `;` and `&` end one), so
 * test -f, rm -f and docker build -f name other files; a short flag may carry its value glued (`-fFILE`). Lines continued with
 * a backslash are joined first, so are the lines of a folded block scalar (`>`), and a line that starts with a flag joins the
 * line before it in a block scalar (`|` with no backslash) or as a `key: value` scalar's continuation; a flag at the end of a
 * line takes the first word of the next. A quoted word with spaces (a YAML-quoted script, `bash -c '...'`) is read again as a
 * command line, and a word starting with `#` ends the line (a comment). Each finding names the file, the line and the key or
 * flag, never the value. Not covered, left to the review of the release file: a value a script assembles before the command (a
 * variable holding a whole --set argument, an array of flags, an indirect `${!name}`, a command substitution's output, a value
 * passed on through `$GITHUB_ENV` or `##vso[task.setvariable]` and read as a shell variable, a file the script writes under a
 * repository path); a secret a step or job outputs under a neutral name; values given through another action's or task's inputs
 * (a GitHub action's `with:`), a template the file includes, or a state file's own reads of the environment.
 */
export function releaseCredentialFindings(text, file) {
	const findings = [];
	const inline = inlineVariables(text);
	// The env entries whose value reads a secret, to a fixed point (an entry may read another one's name).
	const env = envEntries(text);
	const envSecrets = new Set();
	for (let changed = true; changed;) {
		changed = false;
		for (const { name, value } of env) {
			if (!envSecrets.has(name) && secretReference(value, inline, envSecrets)) {
				envSecrets.add(name);
				changed = true;
			}
		}
	}
	const physical = text.split(/\r?\n/);
	// The block scalars: a `key: |` or `key: >` line and the lines below it indented past the key.
	const blockOf = new Array(physical.length).fill(null);
	for (let index = 0; index < physical.length; index++) {
		const header = physical[index].match(/^(\s*(?:-\s+)*)([A-Za-z0-9_.-]+|"[^"]*"|'[^']*')\s*:\s*([|>])[-+0-9]*\s*(?:#.*)?$/);
		if (!header) continue;
		const block = { header: index, key: unquote(header[2]), folded: header[3] === '>' };
		let next = index + 1;
		for (; next < physical.length && (physical[next].trim() === '' || indentOf(physical[next]) > header[1].length); next++) blockOf[next] = block;
		index = next - 1;
	}
	// The logical lines, each numbered by its first physical line. scalar: a `key: value` line whose value (plain or quoted) a
	// more-indented line may continue.
	const scalarStart = /^\s*(?:-\s+)?(?:[A-Za-z0-9_.-]+|"[^"]*"|'[^']*')\s*:\s+(?![|>][-+0-9]*\s*(?:#.*)?$)[^\s#]/;
	const lines = [];
	physical.forEach((line, index) => {
		const previous = lines.at(-1);
		const block = blockOf[index];
		const flagLed = /^\s*--?[A-Za-z]/.test(line);
		const joins = previous && line.trim() !== '' && (previous.continued || (previous.block === block && block?.key !== 'overrideValues'
			&& ((block && (block.folded || flagLed)) || (!block && flagLed && previous.scalar && indentOf(line) > previous.indent))));
		if (joins) {
			previous.text += ` ${line.trim()}`;
			previous.continued = /\\$/.test(line);
		} else {
			lines.push({ text: line, number: index + 1, continued: /\\$/.test(line), block, indent: indentOf(line), scalar: !block && scalarStart.test(line) });
		}
	});
	const keep = 'which Helm keeps in its release record; create the Secret out of band (ARV-209)';
	const checkPair = (pair, flag, where) => {
		const equals = pair.indexOf('=');
		if (equals <= 0) return;
		const key = pair.slice(0, equals).trim();
		const value = unquote(pair.slice(equals + 1).trim());
		const at = `${where}: ${flag} ${key}`;
		const secret = () => secretReference(value, inline, envSecrets);
		if (RELEASE_CREDENTIAL_KEY.test(key.split('.').pop()) && (value !== '' || flag === '--set-file')) findings.push(`${at} gives Helm a credential as a value, ${keep}`);
		else if (flag === '--set-file' && pipelinePath(value)) findings.push(`${at} gives Helm a file the pipeline writes (${pipelinePath(value)}) as a value, ${keep}`);
		else if (secret()) findings.push(`${at} gives Helm ${secret()} as a value, ${keep}`);
	};
	const fileFinding = (where, flag, value, tool, what) => {
		const why = pipelinePath(value);
		if (why) findings.push(`${where}: ${flag} gives ${tool} ${what} the pipeline writes (${why}), which may hold a secret Helm keeps in its release record; give only the repository's files and create Secrets out of band (ARV-209)`);
	};
	// helm and helmfile by name or path, or through the variables HELM and HELMFILE that point at them.
	const commandOf = (segment) => segment.some((word) => /(^|\/)helmfile$|^\$\{?HELMFILE\}?$/.test(unquote(word))) ? 'helmfile'
		: segment.some((word) => /(^|\/)helm$|^\$\{?HELM\}?$/.test(unquote(word))) ? 'helm' : null;
	// forced: the command the words belong to ('helm' for --args and a HelmDeploy task's arguments), or null to read it from
	// each shell command of the line.
	const checkWords = (tokens, nextLine, where, forced) => {
		const end = tokens.findIndex((token) => token.startsWith('#'));
		const all = end < 0 ? tokens : tokens.slice(0, end);
		const commandFrom = (from) => {
			const stop = all.findIndex((token, index) => index >= from && COMMAND_SEPARATORS.has(token));
			return forced ?? commandOf(all.slice(from, stop < 0 ? all.length : stop));
		};
		let command = commandFrom(0);
		for (let i = 0; i < all.length; i++) {
			const token = all[i];
			if (COMMAND_SEPARATORS.has(token)) {
				command = commandFrom(i + 1);
				continue;
			}
			// `--flag value`, `--flag=value`, or a short flag's value glued to it (`-fvalues.yaml`, as helm's flag parser reads it).
			const argumentOf = (flag) => {
				if (token !== flag) return token.slice(token.startsWith(`${flag}=`) ? flag.length + 1 : flag.length);
				if (i + 1 < all.length) return all[++i];
				return nextLine?.()?.[0] ?? '';
			};
			const takes = (name) => token === name || token.startsWith(`${name}=`) || (/^-[A-Za-z]$/.test(name) && token.startsWith(name));
			const valueFlag = RELEASE_VALUE_FLAGS.find((name) => token === name || token.startsWith(`${name}=`));
			if (valueFlag) {
				for (const pair of splitOutside(unquote(argumentOf(valueFlag)), ',')) checkPair(pair, valueFlag, where);
				continue;
			}
			if (token === '--args' || token.startsWith('--args=')) {
				checkWords(words(unquote(argumentOf('--args'))), null, where, 'helm');
				continue;
			}
			// A quoted word with spaces: a script in YAML quotes or a shell's -c string, read as a command line of its own.
			if (/^(["']).*\1$/s.test(token) && /\s/.test(token)) {
				checkWords(words(token.slice(1, -1)), null, where, null);
				continue;
			}
			if (!command) continue;
			const stateFlag = command === 'helmfile' && HELMFILE_STATE_FLAGS.find(takes);
			if (stateFlag) {
				fileFinding(where, stateFlag, unquote(argumentOf(stateFlag)), 'Helmfile', 'a state file');
				continue;
			}
			const fileFlag = (command === 'helmfile' ? HELMFILE_VALUES_FLAGS : HELM_VALUES_FLAGS).find(takes);
			if (fileFlag) fileFinding(where, fileFlag, unquote(argumentOf(fileFlag)), command === 'helmfile' ? 'Helmfile' : 'Helm', 'a values file');
		}
	};
	// A HelmDeploy task's step: the list item the line belongs to names the task.
	const inHelmDeploy = (physicalIndex) => {
		const indent = indentOf(physical[physicalIndex]);
		let start = physicalIndex;
		while (start >= 0 && !(/^\s*-\s/.test(physical[start]) && indentOf(physical[start]) < indent)) start--;
		if (start < 0) return false;
		const dash = indentOf(physical[start]);
		for (let line = start; line < physical.length; line++) {
			if (line > start && physical[line].trim() !== '' && indentOf(physical[line]) <= dash) break;
			if (/^\s*(?:-\s+)?task:\s*['"]?HelmDeploy@/i.test(physical[line])) return true;
		}
		return false;
	};
	const consumed = new Set();
	lines.forEach(({ text: joined, number }, index) => {
		if (consumed.has(index)) return;
		const where = `${file}:${number}`;
		const input = joined.match(/^\s*(overrideValues|arguments|valueFile):\s*(.*)$/);
		if (input && (input[1] !== 'arguments' || inHelmDeploy(number - 1))) {
			// One line, or a block scalar: its lines below.
			const own = input[2].trim();
			const block = /^[|>][-+0-9]*\s*(?:#.*)?$/.test(own);
			const body = block ? lines.map((line, at) => ({ line, at })).filter(({ line }) => line.block?.header === number - 1) : [];
			for (const { at } of body) consumed.add(at);
			const values = block ? body.map(({ line }) => line.text.trim()) : [unquote(own)];
			if (input[1] === 'overrideValues') for (const pair of values.flatMap((line) => splitOutside(line, ','))) checkPair(pair, 'overrideValues', where);
			else if (input[1] === 'arguments') checkWords(words(values.join(' ')), null, where, 'helm');
			else for (const value of values) fileFinding(where, 'valueFile', unquote(value), 'Helm', 'a values file');
			return;
		}
		checkWords(words(joined), () => (lines[index + 1] ? words(lines[index + 1].text) : []), where, null);
	});
	return findings;
}

/**
 * ARV-198: with pod TLS, api-main and only it mounts the web pod's certificate secret, its tls.crt alone (never tls.key), read
 * only, at /app/secrets/web-certificate, and gets Monitoring__WebCertificatePath naming that file, for the certificateExpiry
 * alert (certificate web-tls); without pod TLS nothing mounts it. webSecret is the web pod's certificate secret's name.
 */
export const WEB_CERTIFICATE = { volume: 'web-certificate', mountPath: '/app/secrets/web-certificate', variable: 'Monitoring__WebCertificatePath', file: '/app/secrets/web-certificate/tls.crt' };

export function webCertificateFindings(docs, { tls, webSecret }) {
	const findings = [];
	for (const doc of docs.filter((d) => ['Deployment', 'StatefulSet', 'Job', 'CronJob', 'DaemonSet'].includes(d?.kind))) {
		const spec = doc.kind === 'CronJob' ? doc.spec?.jobTemplate?.spec?.template?.spec : doc.spec?.template?.spec;
		if (!spec) continue;
		const where = `${doc.kind}/${doc.metadata?.name}`;
		const isMain = doc.kind === 'Deployment' && doc.metadata?.name === 'api-main-deployment';
		const volumes = (spec.volumes ?? []).filter((volume) => volume.secret?.secretName === webSecret || volume.projected?.sources?.some((source) => source.secret?.name === webSecret));
		const web = doc.kind === 'Deployment' && doc.metadata?.name === 'web-deployment';
		if (web) continue; // the web pod's own certificate, held by the pod TLS rules
		// The ARV-198 review: no container of another workload reads the web secret through its environment either (a variable's
		// secretKeyRef, which could name tls.key, or envFrom, which takes every key), api-main included.
		const containers = [...(spec.initContainers ?? []), ...(spec.containers ?? [])];
		const byEnvironment = [
			...containers.flatMap((container) => (container.env ?? []).filter((variable) => variable.valueFrom?.secretKeyRef?.name === webSecret).map((variable) => `${container.name} ${variable.name}`)),
			...containers.flatMap((container) => (container.envFrom ?? []).filter((source) => source.secretRef?.name === webSecret).map(() => `${container.name} envFrom`))
		];
		if (byEnvironment.length) findings.push(`${where}: reads the web pod's certificate secret ${webSecret} through its environment (${byEnvironment.join(', ')}); only api-main may read it, its tls.crt alone through the ${WEB_CERTIFICATE.volume} volume`);
		if (!tls || !isMain) {
			if (volumes.length) findings.push(`${where}: mounts the web pod's certificate secret ${webSecret}; only api-main may, its tls.crt alone, with pod TLS`);
			continue;
		}
		const volume = volumes.find((v) => v.name === WEB_CERTIFICATE.volume);
		if (volumes.length !== 1 || !volume) findings.push(`${where}: must mount the web pod's certificate secret ${webSecret} once, as the volume ${WEB_CERTIFICATE.volume} (found ${volumes.length})`);
		else {
			const items = volume.secret?.items ?? [];
			if (!volume.secret || items.length !== 1 || items[0].key !== 'tls.crt' || items[0].path !== 'tls.crt') findings.push(`${where}: the ${WEB_CERTIFICATE.volume} volume must project tls.crt alone from ${webSecret}, never tls.key`);
			if (volume.secret && volume.secret.optional !== true) findings.push(`${where}: the ${WEB_CERTIFICATE.volume} volume must be optional, so a web secret not yet issued does not hold api-main's pods (web-tls then reads 0 and alerts)`);
		}
		const mounts = containers.flatMap((container) => (container.volumeMounts ?? []).filter((mount) => mount.name === WEB_CERTIFICATE.volume).map((mount) => ({ container: container.name, mount })));
		const host = spec.containers?.[0];
		if (mounts.length !== 1 || mounts[0].container !== host?.name || mounts[0].mount.mountPath !== WEB_CERTIFICATE.mountPath || mounts[0].mount.readOnly !== true || mounts[0].mount.subPath)
			findings.push(`${where}: the ${WEB_CERTIFICATE.volume} volume must be mounted once, in the host's container, read-only and whole at ${WEB_CERTIFICATE.mountPath}`);
		const variable = (host?.env ?? []).find((entry) => entry.name === WEB_CERTIFICATE.variable);
		if (variable?.value !== WEB_CERTIFICATE.file) findings.push(`${where}: the host's container must get ${WEB_CERTIFICATE.variable}=${WEB_CERTIFICATE.file}`);
	}
	return findings;
}
