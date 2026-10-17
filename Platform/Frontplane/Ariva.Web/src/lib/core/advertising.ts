import { Api, fail, type Result } from './Api';

/**
 * A site's advertising policy (ARV-145): whether its passenger displays may show advertising in their second region, and
 * the queue zones where it stays off even where the site allows it. Off for a site that never had one. The roles that
 * manage displays see it (AdvertisingPolicy.View); only system administrators change it (AdvertisingPolicy.Edit), with a
 * second factor (the API answers mfa_required and Api.ts opens the step-up dialog) and a reason naming who authorised it.
 */

const site = /^[A-Z0-9]{2,8}(-[A-Z0-9]{1,8})?$/;

export const limits = { zones: 20, minReason: 10, maxReason: 500 } as const;

export interface AdvertisingPolicy {
	siteCode: string;
	allowed: boolean;
	excludedZones: string[];
	reason: string | null;
	changedOn: string | null;
	/** Excluded zones that are not queue zones of the published profile. */
	unpublishedZones: string[];
	/** The site's displays set to advertising that show instructions because of this policy. */
	displaysFallingBack: string[];
	/** Another zone profile is published since the policy was saved: no display shows advertising until it is saved again. */
	profileChanged: boolean;
	/** The site's published zone profile now, which a change names as the one it was reviewed against. */
	publishedProfileId: string | null;
	publishedProfileVersion: number | null;
	/** The zone profile version the policy was last saved against. */
	confirmedProfileVersion: number | null;
	/** The queue zones of the published profile, read with it: the ones the screen offers to exclude. */
	publishedQueueZones: string[];
}

export interface AdvertisingPolicyRequest {
	allowed: boolean;
	excludedZones: string[];
	reason: string;
	/** The published zone profile the exclusions were reviewed against (409 when another has been published since). */
	reviewedProfileId: string | null;
}

const path = (siteCode: string) =>
	`/api/v1/admin/sites/${encodeURIComponent(siteCode)}/advertising-policy`;

export function get(siteCode: string): Promise<Result<AdvertisingPolicy>> {
	return site.test(siteCode)
		? Api.get<AdvertisingPolicy>(path(siteCode))
		: Promise.resolve(fail<AdvertisingPolicy>('Refused: not a site.'));
}

export function change(
	siteCode: string,
	request: AdvertisingPolicyRequest
): Promise<Result<AdvertisingPolicy>> {
	return site.test(siteCode)
		? Api.put<AdvertisingPolicy>(path(siteCode), request)
		: Promise.resolve(fail<AdvertisingPolicy>('Refused: not a site.'));
}

/** Whether a display showing these queue zones may show advertising under the policy (the server decides; this guides the form). */
export function allowsDisplayOf(policy: AdvertisingPolicy | null, zones: string[]): boolean {
	return (
		!!policy?.allowed &&
		!policy.profileChanged &&
		policy.unpublishedZones.length === 0 &&
		zones.every((zone) => (policy.publishedQueueZones ?? []).includes(zone.trim())) &&
		!zones.some((zone) => policy.excludedZones.includes(zone.trim()))
	);
}
