import { Api, fail, type Result } from './Api';

/**
 * A site's advertisement library (ARV-142a, videos ARV-142b): images typed by their bytes and written anew by the server,
 * and MP4 or WebM videos whose containers it checked and stripped of their metadata, each with who advertises, the
 * campaign and the site's local days it may play. An item plays only once approved, by a person other than
 * the one who uploaded it and the one who last changed it; approving asks for a second factor (Api.ts opens the step-up
 * dialog). Every call answers 403 while the site's advertising policy is off. Holders of Advertising.Manage upload, change
 * and retire; holders of Advertising.Approve approve. The file is fetched with the user's token and shown through an
 * object URL (img-src blob: for an image, media-src blob: for a video); nothing here ever builds a URL from an item's text.
 */

const site = /^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$/;
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;
const date = /^\d{4}-\d{2}-\d{2}$/;

/**
 * maxBytes is an image's limit; maxVideoBytes the most any installation takes for a video (the installation's own size and
 * length, at most 100 MB and 120 seconds, are the server's to apply).
 */
export const limits = {
	maxBytes: 10 * 1024 * 1024,
	maxVideoBytes: 100 * 1024 * 1024,
	maxName: 120,
	maxDays: 731
} as const;

/** The types the server takes; anything else is refused there whatever this says. */
export const accept = 'image/png,image/jpeg,image/webp,video/mp4,video/webm';

const videoTypes = new Set(['video/mp4', 'video/webm']);

/** Whether a type (an item's, or a chosen file's as the browser guesses it) is a video type the library takes. */
export function isVideoType(contentType: string | null | undefined): boolean {
	return videoTypes.has(contentType ?? '');
}

export type MediaStatus = 'Draft' | 'Approved' | 'Retired';

export interface AdvertisingMedia {
	id: string;
	siteCode: string;
	contentType: string;
	sizeBytes: number;
	sha256: string;
	widthPixels: number;
	heightPixels: number;
	/** A video's length (null for an image). */
	durationMilliseconds: number | null;
	originalFileName: string | null;
	advertiser: string;
	campaign: string;
	startsOn: string;
	endsOn: string;
	status: MediaStatus;
	uploadedBy: string | null;
	uploadedOn: string | null;
	approvedBy: string | null;
	approvedOn: string | null;
	retiredOn: string | null;
	/** Approved and within its dates on the site's local day now. */
	playable: boolean;
	/** A draft the caller neither uploaded nor last changed (the server still decides). */
	canApprove: boolean;
	/** What an approval of the item as shown names; the server refuses it (409) once the item has changed. */
	reviewToken: string;
}

export interface MediaDetails {
	advertiser: string;
	campaign: string;
	startsOn: string;
	endsOn: string;
}

const path = (siteCode: string, id?: string, action?: 'content' | 'approve' | 'retire') =>
	`/api/v1/admin/sites/${encodeURIComponent(siteCode)}/advertising/media${id ? `/${id}` : ''}${action ? `/${action}` : ''}`;

function checked<T>(
	siteCode: string,
	id: string | null,
	call: () => Promise<Result<T>>
): Promise<Result<T>> {
	if (!site.test(siteCode)) return Promise.resolve(fail<T>('Refused: not a site.'));
	if (id !== null && !guid.test(id)) return Promise.resolve(fail<T>('Refused: not an id.'));
	return call();
}

export function list(siteCode: string): Promise<Result<AdvertisingMedia[]>> {
	return checked(siteCode, null, () => Api.get<AdvertisingMedia[]>(path(siteCode)));
}

export function upload(
	siteCode: string,
	file: File,
	details: MediaDetails
): Promise<Result<AdvertisingMedia>> {
	if (!date.test(details.startsOn) || !date.test(details.endsOn))
		return Promise.resolve(fail<AdvertisingMedia>('Refused: not a date.'));
	const form = new FormData();
	form.append('file', file);
	form.append('advertiser', details.advertiser);
	form.append('campaign', details.campaign);
	form.append('startsOn', details.startsOn);
	form.append('endsOn', details.endsOn);
	return checked(siteCode, null, () => Api.post<AdvertisingMedia>(path(siteCode), form));
}

export function change(
	siteCode: string,
	id: string,
	details: MediaDetails
): Promise<Result<AdvertisingMedia>> {
	if (!date.test(details.startsOn) || !date.test(details.endsOn))
		return Promise.resolve(fail<AdvertisingMedia>('Refused: not a date.'));
	return checked(siteCode, id, () => Api.put<AdvertisingMedia>(path(siteCode, id), details));
}

/** Approves the item as the caller was shown it: its review token goes with the approval. */
export function approve(
	siteCode: string,
	id: string,
	reviewed: string
): Promise<Result<AdvertisingMedia>> {
	if (!/^[0-9a-f]{64}$/.test(reviewed))
		return Promise.resolve(fail<AdvertisingMedia>('Refused: not a review token.'));
	return checked(siteCode, id, () =>
		Api.post<AdvertisingMedia>(path(siteCode, id, 'approve'), { reviewed })
	);
}

export function retire(siteCode: string, id: string): Promise<Result<AdvertisingMedia>> {
	return checked(siteCode, id, () => Api.post<AdvertisingMedia>(path(siteCode, id, 'retire')));
}

/** The stored image or video, fetched with the user's token, for an object URL the caller revokes. */
export function content(siteCode: string, id: string): Promise<Result<Blob>> {
	return checked(siteCode, id, () =>
		Api.get<Blob>(path(siteCode, id, 'content'), { responseType: 'blob' })
	);
}
