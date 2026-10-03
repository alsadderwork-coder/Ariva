import { Api, fail, type Result } from './Api';
import type { Page } from './topology';

/**
 * The device registry, calibration and health API (ARV-021 to ARV-025) as the devices screen uses it (ARV-054). Every
 * call is limited to the caller's sites by the server. Registering, a new credential, network access and retiring are
 * critical actions (step-up within 15 minutes), which Api.ts answers with the step-up dialog. A credential comes back
 * only in the answer that created it; Ariva keeps its hash.
 */

export const families = [
	'StereoVision',
	'Lidar',
	'CameraAnalytics',
	'ThermalOrTimeOfFlight',
	'Simulator'
] as const;
export const transports = [
	'HttpsPush',
	'Mqtt',
	'RestPull',
	'WebSocket',
	'TcpOrUdp',
	'FileDrop',
	'OnvifProfileM'
] as const;
export const dialects = ['Canonical', 'Xovis', 'Declarative'] as const;
export const clockSources = ['Ntp', 'Ptp'] as const;
export const calibrationMethods = ['ManualCountTally', 'ManualCountTwoObservers'] as const;

export interface Footprint {
	lengthMetres: number | null;
	widthMetres: number | null;
	radiusMetres: number | null;
	source: string;
	text: string;
	note: string | null;
}

export interface Device {
	id: string;
	code: string;
	siteCode: string;
	family: string;
	model: string;
	transport: string;
	dialect: string;
	clockSource: string;
	state: 'Commissioning' | 'Online' | 'Degraded' | 'Offline' | 'Retired';
	levelId: string;
	x: number;
	y: number;
	mountingHeightMetres: number;
	orientationDegrees: number;
	footprint: Footprint;
	queueZoneName: string;
	credentialPrefix: string | null;
	credentialIssuedOn: string | null;
	lastCalibratedOn: string | null;
	lastCalibrationPassed: boolean | null;
	retiredOn: string | null;
	createdOn: string | null;
	allowedSources: string[] | null;
	clientCertificateSha256: string | null;
	mappingName: string | null;
}

export interface Placement {
	levelId: string;
	x: number;
	y: number;
	mountingHeightMetres: number;
	orientationDegrees: number;
	queueZoneName: string;
	footprintLengthMetres?: number | null;
	footprintWidthMetres?: number | null;
	footprintRadiusMetres?: number | null;
}

export interface Calibration {
	id: string;
	method: string;
	sampleSize: number;
	countingAccuracyPercent: number;
	waitTimeErrorMinutes: number;
	thresholdPercent: number;
	passed: boolean;
	notes: string | null;
	performedOn: string;
	performedBy: string;
	deviceState: string;
}

export interface DeviceHealth {
	id: string;
	code: string;
	queueZoneName: string;
	state: string;
	lastSeenOn: string | null;
	secondsSinceSeen: number | null;
	reportedOnline: boolean | null;
	frameRate: number | null;
	temperatureCelsius: number | null;
	clockOffsetMilliseconds: number | null;
	clockState: string | null;
}

export interface ZoneHealth {
	siteCode: string;
	queueZoneName: string;
	state: string;
	devices: number;
	devicesOffline: number;
	devicesDegraded: number;
	changedOn: string;
}

export interface HealthOverview {
	heartbeatTimeoutSeconds: number;
	devices: DeviceHealth[];
	zones: ZoneHealth[];
	truncated: boolean;
}

export interface Mapping {
	name: string;
	title: string;
	source: string;
	kinds: string[];
}

/** A device with its credential, returned once by registration and rotation. */
export interface Issued {
	device: Device;
	credential: string;
}

const base = '/api/v1/admin/devices';
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

function at<T>(id: string, call: (id: string) => Promise<Result<T>>): Promise<Result<T>> {
	return guid.test(id) ? call(id) : Promise.resolve(fail<T>('Refused: not an id.'));
}

export function search(siteCode: string): Promise<Result<Page<Device>>> {
	return Api.get<Page<Device>>(base, { query: { siteCode, pageSize: 500 } });
}

export function health(siteCode: string): Promise<Result<HealthOverview>> {
	return Api.get<HealthOverview>(`${base}/health`, { query: { siteCode } });
}

export function mappings(): Promise<Result<Mapping[]>> {
	return Api.get<Mapping[]>(`${base}/mappings`);
}

export function register(body: {
	code: string;
	family: string;
	model: string;
	transport: string;
	dialect: string;
	clockSource: string;
	placement: Placement;
	mappingName?: string | null;
}): Promise<Result<Issued>> {
	return Api.post<Issued>(base, body);
}

export function update(
	id: string,
	body: {
		model: string;
		transport: string;
		dialect: string;
		clockSource: string;
		mappingName?: string | null;
	}
): Promise<Result<Device>> {
	return at(id, (i) => Api.put<Device>(`${base}/${i}`, body));
}

export function move(id: string, placement: Placement): Promise<Result<Device>> {
	return at(id, (i) => Api.put<Device>(`${base}/${i}/placement`, placement));
}

export function rotateCredential(id: string): Promise<Result<Issued>> {
	return at(id, (i) => Api.post<Issued>(`${base}/${i}/credential`));
}

export function setAccess(
	id: string,
	allowedSources: string[],
	clientCertificateSha256: string
): Promise<Result<Device>> {
	return at(id, (i) =>
		Api.put<Device>(`${base}/${i}/access`, { allowedSources, clientCertificateSha256 })
	);
}

export function calibrations(id: string): Promise<Result<Calibration[]>> {
	return at(id, (i) => Api.get<Calibration[]>(`${base}/${i}/calibrations`));
}

export function recordCalibration(
	id: string,
	body: {
		method: string;
		sampleSize: number;
		countingAccuracyPercent: number;
		waitTimeErrorMinutes: number;
		thresholdPercent?: number | null;
		notes?: string | null;
	}
): Promise<Result<Calibration>> {
	return at(id, (i) => Api.post<Calibration>(`${base}/${i}/calibrations`, body));
}

export function retire(id: string): Promise<Result<Device>> {
	return at(id, (i) => Api.post<Device>(`${base}/${i}/retire`));
}

export function remove(id: string): Promise<Result<unknown>> {
	return at(id, (i) => Api.delete<unknown>(`${base}/${i}`));
}
