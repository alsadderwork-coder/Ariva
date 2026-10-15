import { hosts, streamUrl, webOrigin, webUrl } from './hosts';

// ARV-128 (security review recommendation 3): playwright.config.ts refuses an address that is not on this machine, but a
// port-forward or tunnel (kubectl port-forward, ssh -L) makes a deployed host answer on a local port, and outside CI the run
// reuses whatever answers there. Global setup therefore asks every host the run writes to, before anything is written, to
// show that it is a vm-local host of this run, and stops the run otherwise:
//   - the API hosts and the web app (through its /api proxy) answer a CORS preflight from the vm-local web origin
//     (http://localhost:51011) with that origin allowed; only appsettings.base.vm-local.json allows it, every deployed
//     environment allows its own web host alone (Security:Cors:AllowedOrigins);
//   - the simulator answers a read with this run's own simulator key, which only a simulator this run started holds.
// Both are reads; a host that does not answer, or answers otherwise, counts as not this run's (fail closed).

export interface RunHost {
	name: string;
	/** A CORS preflight against this address, or a read with the run's simulator read key. */
	check: 'cors' | 'simulator-key';
	url: string;
}

/** Every host the run calls or writes to; Stream only in the live pipeline runs (demo, rehearsal), which start it. */
export function runHosts(env: NodeJS.ProcessEnv = process.env): RunHost[] {
	const livePipeline = env.ARIVA_E2E_DEMO === '1' || env.ARIVA_E2E_REHEARSAL === '1';
	return [
		{ name: 'main', check: 'cors', url: `${hosts.main}/health/liveness` },
		{ name: 'ingest', check: 'cors', url: `${hosts.ingest}/health/liveness` },
		{ name: 'integration', check: 'cors', url: `${hosts.integration}/health/liveness` },
		{ name: 'cronz', check: 'cors', url: `${hosts.cronz}/health/liveness` },
		...(livePipeline ? [{ name: 'stream', check: 'cors' as const, url: `${streamUrl}/health/liveness` }] : []),
		{ name: 'web', check: 'cors', url: `${webUrl}/api/auth/me` },
		{ name: 'simulation', check: 'simulator-key', url: `${hosts.simulation}/api/v1/simulation/validation` }
	];
}

/** Whether one host shows that it is a vm-local host of this run. */
async function isThisRuns(host: RunHost, simulatorKey: string | undefined): Promise<boolean> {
	try {
		if (host.check === 'cors') {
			const answer = await fetch(host.url, {
				method: 'OPTIONS',
				headers: { Origin: webOrigin, 'Access-Control-Request-Method': 'GET' },
				redirect: 'manual',
				signal: AbortSignal.timeout(10_000)
			});
			return answer.headers.get('access-control-allow-origin') === webOrigin;
		}
		if (!simulatorKey) return false;
		const answer = await fetch(host.url, { headers: { Authorization: `Bearer ${simulatorKey}` }, redirect: 'manual', signal: AbortSignal.timeout(10_000) });
		await answer.arrayBuffer();
		return answer.status === 200;
	} catch {
		return false;
	}
}

/** The names of the hosts that do not show they are this run's vm-local hosts (never their addresses). */
export async function hostsNotOfThisRun(targets: RunHost[] = runHosts(), simulatorKey = process.env.ARIVA_E2E_SIMULATION_READ_KEY): Promise<string[]> {
	const answers = await Promise.all(targets.map(async (host) => ((await isThisRuns(host, simulatorKey)) ? undefined : host.name)));
	return answers.filter((name): name is string => name !== undefined);
}

/** Global setup's check: throws, naming the hosts, unless every host the run writes to is a vm-local host of this run. */
export async function refuseHostsNotOfThisRun(targets: RunHost[] = runHosts()): Promise<void> {
	const foreign = await hostsNotOfThisRun(targets);
	if (foreign.length > 0) {
		throw new Error(
			`ARV-128: ${foreign.join(', ')} did not show it is a vm-local host of this run (a CORS preflight from ${webOrigin}, or this run's ` +
				'simulator key), so the E2E suites, which write, attack and load, refuse to start. Close any port-forward or tunnel on the ' +
				"hosts' ports (51001 to 51005, 51011, 51020), stop hosts this run did not start, and run again."
		);
	}
}
