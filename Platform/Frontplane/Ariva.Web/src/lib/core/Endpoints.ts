/**
 * Base URLs and routes of the Ariva hosts. Local defaults match each host's launchSettings.json;
 * deployed builds override them with VITE_ARIVA_* variables at build time.
 *
 * Ariva.Api.Main is reached on the web app's own origin (ADR-0026, ARV-051): the ingress sends /api and /hubs on the web
 * host to it, and `vite dev` and `vite preview` proxy them (vite.config.ts). The refresh cookie (Path=/api/auth,
 * SameSite=Strict) and the X-Ariva-Csrf header then need no cross-origin call. Leave VITE_ARIVA_API_MAIN_URL unset
 * unless the API has its own host and the cookie is not needed.
 */
const env = import.meta.env;

const health = {
	startup: '/health/startup',
	readiness: '/health/readiness',
	liveness: '/health/liveness'
} as const;

export const Endpoints = {
	main: {
		baseUrl: (env.VITE_ARIVA_API_MAIN_URL as string | undefined) ?? '',
		health
	},
	ingest: {
		baseUrl: (env.VITE_ARIVA_API_INGEST_URL as string | undefined) ?? 'http://localhost:51002',
		health
	},
	cronz: {
		baseUrl: (env.VITE_ARIVA_API_CRONZ_URL as string | undefined) ?? 'http://localhost:51004',
		health
	},
	integration: {
		baseUrl: (env.VITE_ARIVA_API_INTEGRATION_URL as string | undefined) ?? 'http://localhost:51005',
		health
	},
	simulation: {
		baseUrl: (env.VITE_ARIVA_SIMULATION_URL as string | undefined) ?? 'http://localhost:51020',
		health
	}
} as const;
