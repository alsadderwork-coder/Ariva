import fs from 'node:fs';
import path from 'node:path';
import { supportFolder } from './guard';

// ARV-128: the smoke account's role, its permissions and the critical permissions, without side effects (no target is read
// here), so the E2E suite can compare them with what the API reports (tests/api/smoke-config.spec.ts).

const criticalActionsFile = path.resolve(supportFolder, '..', '..', '..', '..', '..', '..', 'security', 'critical-actions.json');
const actions = ['View', 'Search', 'Create', 'Edit', 'Delete', 'Publish', 'Capture', 'Manage', 'Transfer'];

/** The permission codes ("Entity.Action") of security/critical-actions.json's criticalPermissions (named "<Action><Entity>"). */
export function criticalPermissionCodes(): string[] {
	const names = (JSON.parse(fs.readFileSync(criticalActionsFile, 'utf8')) as { criticalPermissions: string[] }).criticalPermissions;
	return names.map((name) => {
		const action = actions.find((a) => name.startsWith(a) && name.length > a.length);
		if (!action) throw new Error(`critical-actions.json: ${name} does not start with an action`);
		return `${name.slice(action.length)}.${action}`;
	});
}

/** The role the smoke account holds, and no other (owner decision 2026-10-15): the least privileged existing role. */
export const smokeRole = 'HandlerStationManager';

/**
 * The permissions of that role, as GET /api/auth/me lists them: RolePermissions.HandlerStationManager in
 * Ariva.Core/Security/RolePermissions.cs. tests/api/smoke-config.spec.ts checks this list against the role's permissions as
 * GET /api/v1/admin/roles reports them, so a change of the role fails the E2E run until this list follows it. None of them is
 * a critical permission; the one write, Alert.Edit (acknowledging the role's own alerts), is never sent by the smoke client.
 */
export const smokeRolePermissions = [
	'Site.View', 'Site.Search', 'Airport.View', 'Airport.Search', 'Terminal.View', 'Terminal.Search', 'Level.View', 'Level.Search',
	'Checkpoint.View', 'Checkpoint.Search', 'Desk.View', 'Desk.Search', 'FloorPlan.View', 'FloorPlan.Search',
	'ZoneProfile.View', 'ZoneProfile.Search', 'LiveQueue.View', 'AlertRule.View', 'AlertRule.Search', 'Alert.View', 'Alert.Search', 'Alert.Edit'
] as const;
