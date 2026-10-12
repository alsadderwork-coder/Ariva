import { Api, fail, type Result } from './Api';

/**
 * The CSV imports of a site (ARV-123a API, ARV-123b screens): the device list of the as-built pack and the desk code
 * mappings, each a preview, then a commit of the same file with the preview's token. The device import needs
 * Device.Create and a second factor in the last 15 minutes for both steps (Api.ts answers mfa_required with the
 * step-up dialog and sends the same form again); the mapping import needs DeskCodeMapping.Create and no second factor.
 * Every answer is data: row content from the file (models, zone names) is rendered as text only (CWE-79). A device
 * commit answers each new credential once; the screens hold it in the dialog's state only, never in storage, the URL,
 * the console or a store (CWE-532).
 */

export type ImportKind = 'devices' | 'mappings';

/** A refused cell or row: its row (the header is row 1), its column (from 1; none for the whole row) and a fixed text. */
export interface RowError {
	row: number;
	column: number | null;
	reason: string;
}

export interface DeviceRow {
	row: number;
	code: string;
	family: string;
	model: string;
	terminal: string;
	level: string;
	queueZone: string;
}

export interface MappingRow {
	row: number;
	system: string;
	externalCode: string;
	checkpoint: string;
	desk: string;
}

interface PreviewBase {
	fileSha256: string;
	rows: number;
	/** The first 100 refusals, by row and column; errorCount is the total. */
	errors: RowError[];
	errorCount: number;
	/** Only when nothing was refused: what the commit of this file, at this site, by this user, needs. */
	previewToken: string | null;
	expiresUtc: string | null;
}

export interface DevicePreview extends PreviewBase {
	devices: DeviceRow[];
}

export interface MappingPreview extends PreviewBase {
	mappings: MappingRow[];
}

/** A registered device's new credential, answered this once. */
export interface ImportedCredential {
	code: string;
	credential: string;
}

export interface DeviceCommit {
	fileSha256: string;
	devices: number;
	credentials: ImportedCredential[];
	notes: string[];
}

export interface MappingCommit {
	fileSha256: string;
	mappings: number;
}

/** A file is at most 1 MB (CsvImportLimits.MaxFileBytes); the server checks again and answers 413 beyond it. */
export const maxFileBytes = 1024 * 1024;

/** The rows each import takes (CsvImportLimits): shown in the dialog, enforced by the server. */
export const maxRows: Record<ImportKind, number> = { devices: 500, mappings: 10_000 };

/** The header of each kind, required columns then optional ones, as the server names them (exact names, any order). */
export const columns: Record<ImportKind, { required: string[]; optional: string[] }> = {
	devices: {
		required: [
			'code',
			'family',
			'model',
			'transport',
			'dialect',
			'clockSource',
			'terminal',
			'level',
			'x',
			'y',
			'mountingHeightMetres',
			'orientationDegrees',
			'queueZone'
		],
		optional: [
			'mappingName',
			'footprintLengthMetres',
			'footprintWidthMetres',
			'footprintRadiusMetres'
		]
	},
	mappings: { required: ['externalSystem', 'externalCode', 'checkpoint', 'desk'], optional: [] }
};

export type FileProblem = 'notCsv' | 'empty' | 'tooLarge';

// Browsers name a .csv file differently (Windows often as Excel's type); the extension decides, an unlikely type refuses.
const csvTypes = new Set([
	'',
	'text/csv',
	'application/csv',
	'text/comma-separated-values',
	'text/x-csv',
	'text/plain',
	'application/vnd.ms-excel'
]);

/** Why a chosen file is refused before it is sent, or null; the server checks the bytes again. */
export function fileProblem(file: File): FileProblem | null {
	if (!/\.csv$/i.test(file.name) || !csvTypes.has(file.type.toLowerCase())) return 'notCsv';
	if (file.size === 0) return 'empty';
	if (file.size > maxFileBytes) return 'tooLarge';
	return null;
}

const site = /^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$/;
// A Data Protection token in base64url, as the server shapes it (CsvImportAnswers.RefuseToken).
const token = /^[A-Za-z0-9_-]{1,2048}$/;
const routes: Record<ImportKind, string> = {
	devices: 'device-imports',
	mappings: 'desk-code-mapping-imports'
};

function send<T>(
	kind: ImportKind,
	siteCode: string,
	step: 'preview' | 'commit',
	file: File,
	previewToken?: string
): Promise<Result<T>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<T>('Refused: not a site.'));
	if (fileProblem(file))
		return Promise.resolve(fail<T>('Refused: not a CSV file of at most 1 MB.'));
	if (step === 'commit' && !token.test(previewToken ?? ''))
		return Promise.resolve(fail<T>('Refused: no preview token.'));
	const form = new FormData();
	form.append('file', file);
	if (step === 'commit') form.append('previewToken', previewToken!);
	return Api.post<T>(
		`/api/v1/admin/sites/${encodeURIComponent(siteCode)}/${routes[kind]}/${step}`,
		form
	);
}

export function previewDevices(siteCode: string, file: File): Promise<Result<DevicePreview>> {
	return send<DevicePreview>('devices', siteCode, 'preview', file);
}

/** Registers every device of the previewed file, or none: the same File object, sent again with the token. */
export function commitDevices(
	siteCode: string,
	file: File,
	previewToken: string
): Promise<Result<DeviceCommit>> {
	return send<DeviceCommit>('devices', siteCode, 'commit', file, previewToken);
}

export function previewMappings(siteCode: string, file: File): Promise<Result<MappingPreview>> {
	return send<MappingPreview>('mappings', siteCode, 'preview', file);
}

export function commitMappings(
	siteCode: string,
	file: File,
	previewToken: string
): Promise<Result<MappingCommit>> {
	return send<MappingCommit>('mappings', siteCode, 'commit', file, previewToken);
}

/**
 * The server's fixed texts (Ariva.Core CsvImportErrors, the DeviceErrors and TopologyErrors a refusal may carry) and
 * their keys under csvImport.reasons. A text the screen has no words for is shown as the server wrote it, as text, and
 * is never used as a message key.
 */
const reasons: Readonly<Record<string, string>> = {
	'A CSV file is required in the form field "file".': 'fileRequired',
	'A CSV import file is at most 1 MB.': 'fileTooLarge',
	'The file is not UTF-8 text (a byte order mark is optional; UTF-16 and UTF-32 are refused).':
		'notUtf8',
	'The file has no header row.': 'noHeader',
	'The file has no rows after its header.': 'noRows',
	'A cell of the file is far longer than any column takes, so the file is refused.':
		'cellBeyondParserBound',
	'The header names each column once, by its exact name: code, family, model, transport, dialect, clockSource, terminal, level, x, y, mountingHeightMetres, orientationDegrees and queueZone, and optionally mappingName, footprintLengthMetres, footprintWidthMetres and footprintRadiusMetres.':
		'deviceHeader',
	'The header names each column once, by its exact name: externalSystem, externalCode, checkpoint and desk.':
		'mappingHeader',
	'A device import takes at most 500 rows.': 'tooManyDeviceRows',
	'A desk code mapping import takes at most 10,000 rows.': 'tooManyMappingRows',
	'previewToken is the token the preview gave.': 'previewTokenNotValid',
	"The file, the site or the user is not the preview's, or the preview is older than 15 minutes: preview the file again.":
		'notThePreview',
	'The file no longer passes its checks: preview it again to see the refused rows.': 'rowsRefused',
	'The row does not have one cell for each column of the header.': 'wrongCellCount',
	'The row is not valid CSV: a quote inside a cell that is not quoted, or text after a closing quote.':
		'badQuotes',
	'This cell is required.': 'required',
	'A cell is at most 200 characters.': 'tooLong',
	'A cell may not start with =, +, -, @, a tab or a carriage return (a spreadsheet would run it as a formula); a minus is read only as the sign of a number.':
		'formulaPrefix',
	'A cell may not hold a line break or another control character.': 'controlCharacter',
	'A number is digits with an optional leading minus sign and an optional decimal point, such as -0.5 or 12.':
		'notANumber',
	"The number is outside its column's range: x and y from -1 to 2001 m, mounting height 0 to 100 m (2 to 20 m to register), orientation 0 to 360 degrees, footprints 0 to 1000 m.":
		'outOfRange',
	'A code is 1 to 16 upper case letters or digits, with single hyphens inside.': 'code',
	'The device code appears on an earlier row of the file.': 'deviceCodeRepeated',
	'The site has no live level with this terminal and level code.': 'levelNotFound',
	'The site has two terminals with this code at different airports, so the level cannot be told apart.':
		'levelAmbiguous',
	'The registration refuses this row (for example a mounting height outside 2 to 20 m, a position outside the level, or a model with an invisible character).':
		'registrationRefused',
	'Unknown external system: Aman or Aodb.': 'unknownSystem',
	'An external code is 1 to 32 letters, digits or . _ / - characters.': 'externalCode',
	'A live mapping of this system already uses the external code, or the desk, at the site.':
		'mappingTaken',
	'The external code, or the desk, appears on an earlier row of the file for the same system.':
		'mappingRepeated',
	'The site has no live desk with this checkpoint and desk code.': 'deskNotFound',
	'An Aman code maps an immigration desk or e-gate, an Aodb code a check-in counter.':
		'mappingDoesNotFitDesk',
	'The desk code mapping service refuses this row.': 'mappingRefused',
	"The queue zone is not in the site's published zone profile or its draft.": 'zoneNotFound',
	'The queue zone is on another level than the device.': 'zoneOnAnotherLevel',
	"The device's footprint does not reach its queue zone; move it over the zone.": 'outOfReach',
	'Unknown device family.': 'unknownFamily',
	'Unknown transport.': 'unknownTransport',
	'Ariva does not support this transport yet: register the device with HTTPS push or MQTT.':
		'unsupportedTransport',
	'Unknown dialect.': 'unknownDialect',
	'Unknown clock source.': 'unknownClockSource',
	'The declarative dialect needs a mapping from the catalog shipped with Ariva (GET devices/mappings); other dialects take none.':
		'unknownMapping',
	'A record with that code already exists here.': 'duplicate'
};

/** The csvImport.reasons key of a server text, or null when the screen has no words for it. */
export function reasonKey(text: string): string | null {
	return Object.hasOwn(reasons, text) ? reasons[text] : null;
}

/** The csvImport.notes key of a device commit's note, or null. */
export function noteKey(text: string): string | null {
	return text ===
		'Devices arrived in Commissioning, each with a new credential shown this once: install it on the device and calibrate it.'
		? 'devicesArriveCommissioning'
		: null;
}

/**
 * Why a preview or commit did not work: a status the screen words itself (csvImport.failures), or the server's fixed
 * text, with the row a commit was refused at.
 */
export type Failure =
	| {
			kind: 'status';
			key: 'secondFactor' | 'forbidden' | 'notFound' | 'busy' | 'unreachable' | 'refused';
	  }
	| { kind: 'reason'; reason: string; row?: number };

// The titles CsvImportAnswers gives its problems; Api.ts joins title and detail with ': '.
const titles = /^(?:Not valid|Too large|Conflict|Not found): /;
const atRow = /^Row (\d{1,7}): (.+)$/s;

export function failureOf(result: Result<unknown>): Failure {
	switch (result.status) {
		case undefined:
			return { kind: 'status', key: 'refused' };
		case 0:
			return { kind: 'status', key: 'unreachable' };
		case 401:
			// The step-up dialog was cancelled (an ended session goes back to sign-in in Api.ts).
			return { kind: 'status', key: 'secondFactor' };
		case 403:
			return { kind: 'status', key: 'forbidden' };
		case 404:
			return { kind: 'status', key: 'notFound' };
		case 413:
			return { kind: 'reason', reason: 'A CSV import file is at most 1 MB.' };
		case 429:
			return { kind: 'status', key: 'busy' };
	}
	const detail = (result.errorMessages[0] ?? '').replace(titles, '');
	const row = atRow.exec(detail);
	return row
		? { kind: 'reason', row: Number(row[1]), reason: row[2] }
		: { kind: 'reason', reason: detail };
}

/** A column number as a spreadsheet names it (1 is A, 27 is AA), for the refusals table. */
export function columnLetters(column: number): string {
	let n = Math.floor(column);
	let letters = '';
	while (n > 0 && letters.length < 4) {
		const rest = (n - 1) % 26;
		letters = String.fromCharCode(65 + rest) + letters;
		n = Math.floor((n - 1) / 26);
	}
	return letters;
}
