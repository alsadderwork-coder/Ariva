/**
 * Base URLs of the hosts under test. The defaults are the launchSettings.json ports; ARIVA_E2E_* variables point
 * the suites at another deployment (for example the dev cluster) without changing code.
 */
export const hosts = {
	main: process.env.ARIVA_E2E_MAIN_URL || 'http://localhost:51001',
	ingest: process.env.ARIVA_E2E_INGEST_URL || 'http://localhost:51002',
	integration: process.env.ARIVA_E2E_INTEGRATION_URL || 'http://localhost:51005',
	simulation: process.env.ARIVA_E2E_SIMULATION_URL || 'http://localhost:51020'
} as const;

/**
 * Whether the run has Kafka (ARIVA_E2E_KAFKA_BOOTSTRAP): Ingest answers a push 202 only once its events are in Kafka,
 * so the accepted-push tests need it. CI always provides one.
 */
export const kafkaAvailable = !!process.env.ARIVA_E2E_KAFKA_BOOTSTRAP;

export type HostName = keyof typeof hosts;

/** Every API host the e2e web servers start, in a stable order for parameterised tests. */
export const apiHosts = Object.entries(hosts).map(([name, url]) => ({ name: name as HostName, url }));

/** The run's smtp4dev (ARV-040): its SMTP port on loopback and its web API. */
export const smtp4dev = {
	smtpPort: Number(process.env.ARIVA_E2E_SMTP_PORT || 25251),
	url: process.env.ARIVA_E2E_SMTP4DEV_URL || 'http://127.0.0.1:5081'
} as const;

/** Ariva.Web served by `vite preview` (the production build). */
export const webUrl = process.env.ARIVA_E2E_WEB_URL || 'http://localhost:51011';

/** The Kubernetes probes every host answers anonymously (allowlisted in security/allowlist.json). */
export const probes = ['/health/startup', '/health/readiness', '/health/liveness'] as const;

/** The first protected endpoint; it proves default deny until the authentication story lands. */
export const systemInfoPath = '/api/v1/system/info';

/** An origin that is not in Security:Cors:AllowedOrigins. */
export const foreignOrigin = 'https://attacker.example';

/** The vm-local web origin that is in Security:Cors:AllowedOrigins. */
export const webOrigin = 'http://localhost:51011';
