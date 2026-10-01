/**
 * Base URLs and routes of the Ariva hosts. Local defaults match each host's launchSettings.json;
 * deployed builds override them with VITE_ARIVA_* variables at build time.
 */
const env = import.meta.env;

const health = {
	startup: '/health/startup',
	readiness: '/health/readiness',
	liveness: '/health/liveness'
} as const;

export const Endpoints = {
	main: {
		baseUrl: (env.VITE_ARIVA_API_MAIN_URL as string | undefined) ?? 'http://localhost:51001',
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
