// Example data for the live operations screen until the stream engine and SignalR hub exist (ARV-030 to ARV-035,
// screen ARV-055). The topology is the fictional Demo International Airport (DMO) from the reference scenario
// (docs/design/prototype); the figures are illustrative, not measured, and the screen says so.

export type Area = 'departures' | 'arrivals' | 'transfer';
export type DataQuality = 'Good' | 'Degraded' | 'Unknown';
export type ZoneStatus = 'withinTarget' | 'nearTarget' | 'overTarget' | 'degraded';

export interface ZoneSnapshot {
	id: string;
	name: { en: string; ar: string };
	area: Area;
	/** Passengers counted in the queue zone */
	inQueue: number;
	/** Nowcast wait in minutes; null when the data quality is not Good */
	waitMinutes: number | null;
	targetMinutes: number;
	serversOpen: number;
	serversTotal: number;
	dataQuality: DataQuality;
}

export const demoZones: readonly ZoneSnapshot[] = [
	{
		id: 'chk-a',
		name: { en: 'Check-in island A', ar: 'جزيرة تسجيل الوصول A' },
		area: 'departures',
		inQueue: 46,
		waitMinutes: 12,
		targetMinutes: 20,
		serversOpen: 9,
		serversTotal: 12,
		dataQuality: 'Good'
	},
	{
		id: 'chk-b',
		name: { en: 'Check-in island B', ar: 'جزيرة تسجيل الوصول B' },
		area: 'departures',
		inQueue: 63,
		waitMinutes: 18,
		targetMinutes: 20,
		serversOpen: 8,
		serversTotal: 12,
		dataQuality: 'Good'
	},
	{
		id: 'chk-c',
		name: { en: 'Check-in island C', ar: 'جزيرة تسجيل الوصول C' },
		area: 'departures',
		inQueue: 21,
		waitMinutes: 7,
		targetMinutes: 20,
		serversOpen: 6,
		serversTotal: 12,
		dataQuality: 'Good'
	},
	{
		id: 'chk-d',
		name: { en: 'Check-in island D', ar: 'جزيرة تسجيل الوصول D' },
		area: 'departures',
		inQueue: 88,
		waitMinutes: 24,
		targetMinutes: 20,
		serversOpen: 7,
		serversTotal: 12,
		dataQuality: 'Good'
	},
	{
		id: 'sec-n',
		name: { en: 'Security North', ar: 'التفتيش الأمني الشمالي' },
		area: 'departures',
		inQueue: 54,
		waitMinutes: 9,
		targetMinutes: 10,
		serversOpen: 5,
		serversTotal: 6,
		dataQuality: 'Good'
	},
	{
		id: 'sec-s',
		name: { en: 'Security South', ar: 'التفتيش الأمني الجنوبي' },
		area: 'departures',
		inQueue: 31,
		waitMinutes: 6,
		targetMinutes: 10,
		serversOpen: 4,
		serversTotal: 6,
		dataQuality: 'Good'
	},
	{
		id: 'imm-dep',
		name: { en: 'Departures immigration desks', ar: 'مكاتب الجوازات للمغادرة' },
		area: 'departures',
		inQueue: 40,
		waitMinutes: 11,
		targetMinutes: 15,
		serversOpen: 14,
		serversTotal: 22,
		dataQuality: 'Good'
	},
	{
		id: 'egt-dep',
		name: { en: 'Departures e-gates', ar: 'البوابات الإلكترونية للمغادرة' },
		area: 'departures',
		inQueue: 12,
		waitMinutes: 3,
		targetMinutes: 5,
		serversOpen: 4,
		serversTotal: 4,
		dataQuality: 'Good'
	},
	{
		id: 'imm-arr',
		name: { en: 'Arrivals immigration desks', ar: 'مكاتب الجوازات للقدوم' },
		area: 'arrivals',
		inQueue: 132,
		waitMinutes: 27,
		targetMinutes: 25,
		serversOpen: 16,
		serversTotal: 22,
		dataQuality: 'Good'
	},
	{
		id: 'egt-arr',
		name: { en: 'Arrivals e-gates', ar: 'البوابات الإلكترونية للقدوم' },
		area: 'arrivals',
		inQueue: 28,
		waitMinutes: 4,
		targetMinutes: 5,
		serversOpen: 5,
		serversTotal: 6,
		dataQuality: 'Good'
	},
	{
		id: 'sec-trf',
		name: { en: 'Transfer security', ar: 'التفتيش الأمني للعبور' },
		area: 'transfer',
		inQueue: 17,
		waitMinutes: null,
		targetMinutes: 10,
		serversOpen: 2,
		serversTotal: 3,
		dataQuality: 'Degraded'
	}
];

/** Within target below 80 percent of the target wait, near target up to 100 percent, over target above it. */
export function zoneStatus(zone: ZoneSnapshot): ZoneStatus {
	if (zone.dataQuality !== 'Good' || zone.waitMinutes === null) return 'degraded';
	const ratio = zone.waitMinutes / zone.targetMinutes;
	if (ratio > 1) return 'overTarget';
	if (ratio >= 0.8) return 'nearTarget';
	return 'withinTarget';
}

export const demoSensors = { online: 38, total: 40 };
