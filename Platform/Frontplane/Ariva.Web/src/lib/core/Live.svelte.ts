import {
	HttpTransportType,
	HubConnectionBuilder,
	HubConnectionState,
	LogLevel,
	type HubConnection
} from '@microsoft/signalr';
import { auth } from './auth.svelte';
import { Endpoints } from './Endpoints';

/**
 * The live hub client (ARV-035, ARV-055): WebSockets only (the hub refuses the other transports), the access token from
 * memory through accessTokenFactory (refreshed first when it is about to expire), automatic reconnect, and the zone and
 * alert groups joined again after a reconnect. The hub authorises every join against the caller's grants and sites.
 */

/** A queue zone's latest minute (Ariva.Infra LiveZoneSnapshot). */
export interface ZoneSnapshot {
	zoneKey: string;
	minuteUtc: string;
	queueLength: number;
	lengthFromSensors: boolean;
	lengthDegraded: boolean;
	nowcastMinutes: number | null;
	throughputPerMinute: number | null;
	noService: string | null;
	nowcastDegraded: boolean;
	publishedUtc: string;
}

/** What changed about an alert (Ariva.Infra AlertNotice); the screen reads the alert itself through the API. */
export interface AlertNotice {
	alertId: string;
	siteCode: string;
	ruleCode: string;
	ruleName: string;
	zoneName: string | null;
	state: string;
	severity: string;
	publishedUtc: string;
}

export type LiveState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

/** A snapshot older than this is shown as stale (Stream publishes every minute). */
export const staleAfterSeconds = 150;

export function isStale(snapshot: ZoneSnapshot | null | undefined, now = Date.now()): boolean {
	if (!snapshot) return true;
	const published = Date.parse(snapshot.publishedUtc);
	return !Number.isFinite(published) || now - published > staleAfterSeconds * 1000;
}

const isText = (value: unknown): value is string => typeof value === 'string';
const isTextOrNull = (value: unknown): value is string | null => value === null || isText(value);
const isNumber = (value: unknown): value is number =>
	typeof value === 'number' && Number.isFinite(value);
const isNumberOrNull = (value: unknown): value is number | null =>
	value === null || isNumber(value);
const isTime = (value: unknown): value is string =>
	isText(value) && Number.isFinite(Date.parse(value));

/** A hub message is untrusted input: a zone snapshot with the expected shape, or nothing. */
export function asZoneSnapshot(value: unknown): ZoneSnapshot | null {
	if (typeof value !== 'object' || value === null) return null;
	const v = value as Record<string, unknown>;
	return isText(v.zoneKey) &&
		isTime(v.minuteUtc) &&
		isNumber(v.queueLength) &&
		typeof v.lengthFromSensors === 'boolean' &&
		typeof v.lengthDegraded === 'boolean' &&
		isNumberOrNull(v.nowcastMinutes) &&
		isNumberOrNull(v.throughputPerMinute) &&
		isTextOrNull(v.noService) &&
		typeof v.nowcastDegraded === 'boolean' &&
		isTime(v.publishedUtc)
		? (value as ZoneSnapshot)
		: null;
}

/** A hub message is untrusted input: an alert notice with the expected shape, or nothing. */
export function asAlertNotice(value: unknown): AlertNotice | null {
	if (typeof value !== 'object' || value === null) return null;
	const v = value as Record<string, unknown>;
	return isText(v.alertId) &&
		isText(v.siteCode) &&
		isText(v.ruleCode) &&
		isText(v.ruleName) &&
		isTextOrNull(v.zoneName) &&
		isText(v.state) &&
		isText(v.severity) &&
		isTime(v.publishedUtc)
		? (value as AlertNotice)
		: null;
}

export class LiveConnection {
	state = $state<LiveState>('disconnected');
	/** Zones the hub refused (another site, not a queue zone of the published profile). */
	refused = $state<string[]>([]);

	private connection: HubConnection | null = null;
	private zones: string[] = [];
	private alertsSite: string | null = null;
	/** Once stopped (the screen was left), the connection never opens again. */
	private stopped = false;

	constructor(
		private readonly onZone: (snapshot: ZoneSnapshot) => void,
		private readonly onAlert: (notice: AlertNotice) => void
	) {}

	/** Connects (once) and joins the zones and the site's alerts; earlier groups are left. */
	async watch(zoneKeys: string[], alertsSite: string | null): Promise<void> {
		if (this.stopped) return;
		await this.connect();
		if (this.stopped) return;
		await this.leaveAll();
		this.zones = [...zoneKeys];
		this.alertsSite = alertsSite;
		await this.joinAll();
	}

	async stop(): Promise<void> {
		this.stopped = true;
		const connection = this.connection;
		this.connection = null;
		this.zones = [];
		this.alertsSite = null;
		await connection?.stop().catch(() => undefined);
		this.state = 'disconnected';
	}

	private async connect(): Promise<void> {
		if (this.connection && this.connection.state !== HubConnectionState.Disconnected) return;
		const connection = new HubConnectionBuilder()
			.withUrl(`${Endpoints.main.baseUrl}/hubs/live`, {
				transport: HttpTransportType.WebSockets,
				accessTokenFactory: async () => {
					if (!auth.token || auth.expiresAt - Date.now() < 30_000) await auth.refresh();
					return auth.token ?? '';
				}
			})
			.withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
			.configureLogging(LogLevel.None)
			.build();
		connection.on('zone', (message: unknown) => {
			const snapshot = asZoneSnapshot(message);
			if (snapshot) this.onZone(snapshot);
		});
		connection.on('alert', (message: unknown) => {
			const notice = asAlertNotice(message);
			if (notice) this.onAlert(notice);
		});
		connection.onreconnecting(() => (this.state = 'reconnecting'));
		connection.onreconnected(() => {
			this.state = 'connected';
			void this.joinAll();
		});
		connection.onclose(() => {
			if (this.connection === connection) this.state = 'disconnected';
		});
		this.connection = connection;
		this.state = 'connecting';
		try {
			await connection.start();
			if (this.stopped || this.connection !== connection) {
				await connection.stop().catch(() => undefined);
				return;
			}
			this.state = 'connected';
		} catch {
			this.state = 'disconnected';
		}
	}

	private async joinAll(): Promise<void> {
		const connection = this.connection;
		if (!connection || connection.state !== HubConnectionState.Connected) return;
		const refused: string[] = [];
		for (const key of this.zones) {
			try {
				const snapshot = asZoneSnapshot(await connection.invoke<unknown>('JoinZone', key));
				if (snapshot) this.onZone(snapshot);
			} catch {
				refused.push(key);
			}
		}
		this.refused = refused;
		if (this.alertsSite)
			await connection.invoke('JoinAlerts', this.alertsSite).catch(() => undefined);
	}

	private async leaveAll(): Promise<void> {
		const connection = this.connection;
		if (!connection || connection.state !== HubConnectionState.Connected) return;
		for (const key of this.zones) await connection.invoke('LeaveZone', key).catch(() => undefined);
		if (this.alertsSite)
			await connection.invoke('LeaveAlerts', this.alertsSite).catch(() => undefined);
	}
}
