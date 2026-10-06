// Pure checks over rendered Kubernetes manifests; chart-security.mjs feeds them helm template output.
// Each finding names the object and the rule, so a failing render points straight at the template to fix.

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
		const match = /^(?:--|\/)environment(?:=(.*))?$/i.exec(word);
		if (match) fromArgs = match[1] ?? words[index + 1] ?? '';
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

/** Environment variable names that hold credentials: their values come from secrets, never from the manifest (CWE-798). */
const SECRET_ENV = /(PASSWORD|PASSWD|SECRET|TOKEN|PRIVATE_?KEY|API_?KEY)/i;

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
