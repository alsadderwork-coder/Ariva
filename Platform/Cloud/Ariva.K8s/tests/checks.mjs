// Pure checks over rendered Kubernetes manifests; chart-security.mjs feeds them helm template output.
// Each finding names the object and the rule, so a failing render points straight at the template to fix.

const WORKLOADS = new Set(['Deployment', 'StatefulSet', 'DaemonSet', 'Job', 'CronJob']);

function podSpecOf(doc) {
	if (doc.kind === 'CronJob') return doc.spec?.jobTemplate?.spec?.template?.spec;
	return doc.spec?.template?.spec;
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
				const tag = imageTag(container.image ?? '');
				if (!tag) findings.push(`${cid}: image ${container.image} has no tag`);
				if (tag === 'latest') findings.push(`${cid}: image tag latest is not allowed`);
				if (environment === 'k8s-prd' && (tag === 'trunk' || tag === '')) findings.push(`${cid}: production must pin a build number, not ${tag || 'an empty tag'}`);
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
