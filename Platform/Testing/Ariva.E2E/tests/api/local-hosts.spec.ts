import http from 'node:http';
import type { AddressInfo } from 'node:net';
import { expect, test } from '@playwright/test';
import { hostsNotOfThisRun, refuseHostsNotOfThisRun, runHosts, type RunHost } from '../support/local-hosts';

// ARV-128 (security review recommendation 3): global setup refuses to start the suites unless every host they write to shows
// it is a vm-local host of this run, so a deployed host behind a port-forward or tunnel on a local port is never written to.
// A stand-in on loopback answers as a deployed environment's host would: its CORS policy allows its own web host only, and
// it knows none of this run's keys.

let standIn: http.Server;
let base: string;

test.beforeAll(async () => {
	standIn = http.createServer((request, response) => {
		if (request.method === 'OPTIONS') {
			response.writeHead(204, { 'Access-Control-Allow-Origin': 'https://ariva.dalilhub.tech', 'Access-Control-Allow-Credentials': 'true' });
		} else {
			response.writeHead(401, { 'Content-Type': 'application/problem+json' });
		}
		response.end();
	});
	await new Promise<void>((resolve) => standIn.listen(0, '127.0.0.1', resolve));
	base = `http://127.0.0.1:${(standIn.address() as AddressInfo).port}`;
});

test.afterAll(async () => {
	await new Promise((resolve) => standIn.close(resolve));
});

const asDeployed = (): RunHost[] => [
	{ name: 'main', check: 'cors', url: `${base}/health/liveness` },
	{ name: 'web', check: 'cors', url: `${base}/api/auth/me` },
	{ name: 'simulation', check: 'simulator-key', url: `${base}/api/v1/simulation/validation` }
];

test("every host of this run shows it is this run's vm-local host", async () => {
	expect(await hostsNotOfThisRun()).toEqual([]);
	await refuseHostsNotOfThisRun();
});

test('a host behind a tunnel that answers as a deployed host is refused by name, and so is one that does not answer', async () => {
	expect(await hostsNotOfThisRun(asDeployed())).toEqual(['main', 'web', 'simulation']);
	// Among this run's real hosts, only the stand-in is named.
	expect(await hostsNotOfThisRun([...runHosts(), { name: 'tunnel', check: 'cors', url: `${base}/health/liveness` }])).toEqual(['tunnel']);
	// Fail closed: nothing listens on port 9.
	expect(await hostsNotOfThisRun([{ name: 'gone', check: 'cors', url: 'http://127.0.0.1:9/health/liveness' }])).toEqual(['gone']);
	// The real simulator without this run's key is refused too.
	const simulator = runHosts().filter((host) => host.check === 'simulator-key');
	expect(await hostsNotOfThisRun(simulator, 'sim-e2e-not-this-runs-key')).toEqual(['simulation']);

	// The refusal names the hosts, never their addresses.
	const refusal = await refuseHostsNotOfThisRun(asDeployed()).then(
		() => undefined,
		(error: Error) => error.message
	);
	expect(refusal).toMatch(/main, web, simulation did not show it is a vm-local host of this run/);
	expect(refusal).toContain('Close any port-forward or tunnel');
	expect(refusal).not.toContain(base);
});
