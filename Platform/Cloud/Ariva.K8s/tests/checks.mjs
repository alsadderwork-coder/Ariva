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
 * retries needs up to 25 s), then disposes and exits, so its pod's grace period is at least 60 s, and the host is told the
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

/** Environment variable names that hold credentials: their values come from secrets, never from the manifest (CWE-798). */
const SECRET_ENV = /(PASSWORD|PASSWD|SECRET|TOKEN|PRIVATE_?KEY|API_?KEY)/i;

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
	for (const container of pod.containers ?? []) {
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

/**
 * ARV-125b: the criterion rules a render lacks. Every rule of tests/monitoring.json must be a key of ariva-monitoring-alerts
 * (hostDown once per host, as hostDown-<host>), but for amanFeedStale, which is off unless the render turns it on.
 */
export function missingMonitoringRules(docs, { amanFeedStale = false } = {}) {
	const alerts = docs.find((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === 'ariva-monitoring-alerts');
	if (!alerts) return ['ConfigMap/ariva-monitoring-alerts: not rendered'];
	const keys = Object.keys(alerts.data ?? {}).map((key) => key.replace(/\.json$/, ''));
	const missing = MONITORING.criterionRules
		.filter((rule) => rule !== 'amanFeedStale' || amanFeedStale)
		.filter((rule) => !keys.some((key) => key === rule || key.startsWith(`${rule}-`)))
		.map((rule) => `ConfigMap/ariva-monitoring-alerts: rule ${rule} is missing`);
	if (!amanFeedStale && keys.includes('amanFeedStale')) missing.push('ConfigMap/ariva-monitoring-alerts: amanFeedStale is rendered although it is off');
	if (!docs.some((doc) => doc?.kind === 'ConfigMap' && doc.metadata?.name === 'ariva-monitoring-dashboards')) missing.push('ConfigMap/ariva-monitoring-dashboards: not rendered');
	return missing;
}

/**
 * @param {any[]} docs parsed manifests
 * @param {{ environment: string }} context
 * @returns {string[]} findings, empty when every rule holds
 */
export function checkManifests(docs, { environment }) {
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
			// The SignalR hubs take the access token in the URL (ARV-035, CWE-598): an ingress that serves /hubs keeps no
			// access log, no ModSecurity audit log and only critical errors, and serves nothing else.
			const paths = (doc.spec?.rules ?? []).flatMap((rule) => (rule.http?.paths ?? []).map((entry) => entry.path));
			if (paths.some((value) => value === '/hubs' || value?.startsWith('/hubs/'))) {
				const annotations = doc.metadata?.annotations ?? {};
				if (annotations['nginx.ingress.kubernetes.io/enable-access-log'] !== 'false') findings.push(`${id}: /hubs carries tokens in its URL; the ingress must disable the access log`);
				if (!/SecAuditEngine\s+Off/.test(annotations['nginx.ingress.kubernetes.io/modsecurity-snippet'] ?? '')) findings.push(`${id}: /hubs carries tokens in its URL; the ingress must turn the ModSecurity audit log off`);
				if (!/error_log\s+\S+\s+crit;/.test(annotations['nginx.ingress.kubernetes.io/configuration-snippet'] ?? '')) findings.push(`${id}: /hubs carries tokens in its URL; the ingress must log only critical errors`);
				if (paths.some((value) => !(value === '/hubs' || value?.startsWith('/hubs/')))) findings.push(`${id}: /hubs carries tokens in its URL; serve it from its own ingress`);
			}
		}
	}
	return findings;
}
