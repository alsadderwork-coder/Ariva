import { Api, fail, type Result } from './Api';
import type { Page } from './topology';

/**
 * User, role and audit administration (ARV-010a, ARV-011, ARV-012) as the Users and access screen uses it (ARV-059).
 * The server decides everything: an administrator never changes its own roles, password, authenticator, sites or
 * status here, never grants a role above its own or a site beyond its own, and sees no account beyond its sites (or
 * the break-glass account). Creating a user, resets, role grants and site access are critical actions (step-up within
 * 15 minutes), which Api.ts answers with the step-up dialog. A temporary password comes back only in the answer that
 * made it.
 */

const users = '/api/v1/admin/users';
const audit = '/api/v1/admin/audit-entries';
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

/** The fixed roles (ARV-011), in the order the server lists them. */
export const roleCodes = [
	'BorderShiftSupervisor',
	'TerminalDutyManager',
	'HandlerStationManager',
	'SystemAdministrator'
] as const;
export type RoleCode = (typeof roleCodes)[number];

/** Server limits (CreateUserRequest, UpdateUserRequest). */
export const limits = { userName: 64, displayName: 200, email: 320 } as const;
export const userNamePattern = /^[A-Za-z0-9._@-]{3,64}$/;

/** The audit actions on accounts; others (Display.Created, ZoneProfile.Published and so on) are searched as typed. */
export const userActions = [
	'User.Created',
	'User.Updated',
	'User.RoleGranted',
	'User.RoleRevoked',
	'User.PasswordReset',
	'User.TotpReset',
	'User.SitesChanged',
	'User.AllSites',
	'User.Disabled',
	'User.Enabled',
	'User.Unlocked'
] as const;

export interface User {
	id: string;
	userName: string;
	displayName: string | null;
	email: string | null;
	roles: string[];
	isDisabled: boolean;
	isLocked: boolean;
	mustChangePassword: boolean;
	totpEnrolled: boolean;
	lastLoginOn: string | null;
	createdOn: string | null;
	allSites: boolean;
	sites: string[];
}

export interface Role {
	code: string;
	rank: number;
	permissions: string[];
}

export interface AuditEntry {
	id: string;
	occurredOn: string;
	actorId: string | null;
	actorName: string | null;
	action: string;
	targetType: string | null;
	targetId: string | null;
	targetName: string | null;
	beforeSummary: string | null;
	afterSummary: string | null;
	ipAddress: string | null;
	traceId: string | null;
}

export interface UserCriteria {
	text?: string;
	role?: string;
	isDisabled?: boolean | null;
	pageIndex?: number;
	pageSize?: number;
}

export interface AuditCriteria {
	action?: string;
	actorId?: string | null;
	targetId?: string | null;
	/** UTC ISO instants. */
	fromDate?: string | null;
	toDate?: string | null;
	pageIndex?: number;
	pageSize?: number;
}

export interface CreateUserRequest {
	userName: string;
	displayName: string | null;
	email: string | null;
	roles: string[];
	/** The account's sites in the same request (ARV-059): a site-limited administrator gives at least one of its own. */
	allSites: boolean;
	siteCodes: string[];
}

function refused<T>(): Promise<Result<T>> {
	return Promise.resolve(fail<T>('Refused: not an id.'));
}

export function search(criteria: UserCriteria): Promise<Result<Page<User>>> {
	return Api.get<Page<User>>(users, {
		query: {
			text: criteria.text?.trim() || undefined,
			role: criteria.role || undefined,
			isDisabled: criteria.isDisabled ?? undefined,
			pageIndex: criteria.pageIndex ?? 1,
			pageSize: criteria.pageSize ?? 25
		}
	});
}

export function get(id: string): Promise<Result<User>> {
	return guid.test(id) ? Api.get<User>(`${users}/${id}`) : refused();
}

export function create(
	request: CreateUserRequest
): Promise<Result<{ user: User; temporaryPassword: string }>> {
	return Api.post(users, request);
}

export function update(
	id: string,
	request: { displayName: string | null; email: string | null }
): Promise<Result<User>> {
	return guid.test(id) ? Api.put<User>(`${users}/${id}`, request) : refused();
}

function roleCall(method: 'PUT' | 'DELETE', id: string, role: string): Promise<Result<User>> {
	if (!guid.test(id) || !(roleCodes as readonly string[]).includes(role)) return refused();
	const path = `${users}/${id}/roles/${role}`;
	return method === 'PUT' ? Api.put<User>(path) : Api.delete<User>(path);
}

export const grant = (id: string, role: string) => roleCall('PUT', id, role);
export const revoke = (id: string, role: string) => roleCall('DELETE', id, role);

export function setSites(
	id: string,
	allSites: boolean,
	siteCodes: string[]
): Promise<Result<User>> {
	return guid.test(id)
		? Api.put<User>(`${users}/${id}/sites`, { allSites, siteCodes: allSites ? [] : siteCodes })
		: refused();
}

export function resetPassword(id: string): Promise<Result<{ temporaryPassword: string }>> {
	return guid.test(id) ? Api.post(`${users}/${id}/reset-password`) : refused();
}

function action(
	id: string,
	name: 'reset-totp' | 'unlock' | 'disable' | 'enable'
): Promise<Result<unknown>> {
	return guid.test(id) ? Api.post<unknown>(`${users}/${id}/${name}`) : refused();
}

export const resetTotp = (id: string) => action(id, 'reset-totp');
export const unlock = (id: string) => action(id, 'unlock');
export const disable = (id: string) => action(id, 'disable');
export const enable = (id: string) => action(id, 'enable');

export function roles(): Promise<Result<Role[]>> {
	return Api.get<Role[]>('/api/v1/admin/roles');
}

export function auditEntries(criteria: AuditCriteria): Promise<Result<Page<AuditEntry>>> {
	if (
		(criteria.actorId && !guid.test(criteria.actorId)) ||
		(criteria.targetId && !guid.test(criteria.targetId))
	)
		return refused();
	return Api.get<Page<AuditEntry>>(audit, {
		query: {
			action: criteria.action?.trim() || undefined,
			actorId: criteria.actorId || undefined,
			targetId: criteria.targetId || undefined,
			fromDate: criteria.fromDate || undefined,
			toDate: criteria.toDate || undefined,
			pageIndex: criteria.pageIndex ?? 1,
			pageSize: criteria.pageSize ?? 25
		}
	});
}

/** The highest rank among the roles (0 for none), as RoleHierarchy ranks them on the server. */
export function highestRank(held: string[], all: Role[]): number {
	return Math.max(0, ...all.filter((r) => held.includes(r.code)).map((r) => r.rank));
}

/** A date input's day (YYYY-MM-DD) as the start of that UTC day, or its last millisecond for an end bound. */
export function utcDay(day: string, end = false): string | null {
	if (!/^\d{4}-\d{2}-\d{2}$/.test(day)) return null;
	const date = new Date(`${day}T00:00:00Z`);
	if (Number.isNaN(date.getTime())) return null;
	// The last millisecond of the day: the server compares the end bound inclusively.
	if (end) date.setTime(date.getTime() + 86_400_000 - 1);
	return date.toISOString();
}

/** An instant as YYYY-MM-DD HH:MM UTC, the way every screen shows times. */
export function utcText(value: string | null): string {
	if (!value) return '';
	const date = new Date(value);
	return Number.isNaN(date.getTime())
		? ''
		: `${date.toISOString().slice(0, 16).replace('T', ' ')} UTC`;
}
