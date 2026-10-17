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
 * Connecting, joining and leaving run one at a time and always towards the latest watch: a screen that changes site
 * while the first connection is still opening ends on the zones it asked for last, never on an earlier list.
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
	/** What the screen asked for last. */
	private zones: string[] = [];
	private alertsSite: string | null = null;
	/** What the current connection has joined (a reconnect is a new connection with no groups). */
	private joinedZones: string[] = [];
	private joinedAlerts: string | null = null;
	/** The connect, join and leave work, one step after another. */
	private work: Promise<void> = Promise.resolve();
	/** Once stopped (the screen was left), the connection never opens again. */
	private stopped = false;

	constructor(
		private readonly onZone: (snapshot: ZoneSnapshot) => void,
		private readonly onAlert: (notice: AlertNotice) => void
	) {}

	/**
	 * Connects (once) and joins the zones and the site's alerts; earlier groups are left. A call made while an earlier
	 * one is still connecting or joining waits for it, and the last call's zones are the ones joined.
	 */
	watch(zoneKeys: string[], alertsSite: string | null): Promise<void> {
		if (this.stopped) return Promise.resolve();
		this.zones = [...zoneKeys];
		this.alertsSite = alertsSite;
		return this.serially(async () => {
			await this.connect();
			await this.sync();
		});
	}

	async stop(): Promise<void> {
		this.stopped = true;
		const connection = this.connection;
		this.connection = null;
		this.zones = [];
		this.alertsSite = null;
		this.joinedZones = [];
		this.joinedAlerts = null;
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
			// The hub knows the reconnected connection under a new id, in no group yet.
			this.joinedZones = [];
			this.joinedAlerts = null;
			void this.serially(() => this.sync());
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

	/** Runs a step after every earlier one has finished, whether or not it succeeded. */
	private serially(step: () => Promise<void>): Promise<void> {
		const run = this.work.then(step);
		this.work = run.catch(() => undefined);
		return run;
	}

	/** Leaves the groups joined so far and joins the ones asked for last. */
	private async sync(): Promise<void> {
		const connection = this.connection;
		if (this.stopped || !connection || connection.state !== HubConnectionState.Connected) return;
		for (const key of this.joinedZones)
			await connection.invoke('LeaveZone', key).catch(() => undefined);
		if (this.joinedAlerts)
			await connection.invoke('LeaveAlerts', this.joinedAlerts).catch(() => undefined);

		// Counted as joined before asking: a join that fails late may have joined the group, and leaving is harmless.
		const zones = [...this.zones];
		const alertsSite = this.alertsSite;
		this.joinedZones = zones;
		this.joinedAlerts = alertsSite;
		const refused: string[] = [];
		for (const key of zones) {
			try {
				const snapshot = asZoneSnapshot(await connection.invoke<unknown>('JoinZone', key));
				if (snapshot) this.onZone(snapshot);
			} catch {
				refused.push(key);
			}
		}
		this.refused = refused;
		if (alertsSite) await connection.invoke('JoinAlerts', alertsSite).catch(() => undefined);
	}
}
