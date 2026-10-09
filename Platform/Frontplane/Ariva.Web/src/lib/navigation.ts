import type { Component } from 'svelte';
import {
	Activity,
	BellRing,
	ClipboardCheck,
	Cpu,
	FileChartColumn,
	Map,
	MonitorPlay,
	ScanLine,
	Timer,
	UserCog
} from '@lucide/svelte';

export type NavGroup = 'operations' | 'oversight' | 'administration';

export interface NavItem {
	/** Key under navigation.items in the dictionaries */
	id: string;
	href: string;
	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	icon: Component<any>;
	group: NavGroup;
	/** False until the screen's story is done; the item is shown but not navigable */
	ready: boolean;
	/** Backlog story that delivers the screen */
	story: string;
	/**
	 * Permission ("Entity.Action", GET /api/auth/me) the screen's data needs; the item is hidden without it. Hiding is
	 * only convenience: the server checks every call.
	 */
	permission: string;
}

/** Sidebar navigation, grouped like Aman.Web (Operations, Oversight, Administration). */
export const navItems: readonly NavItem[] = [
	{
		id: 'liveOperations',
		href: '/',
		icon: Activity,
		group: 'operations',
		ready: true,
		story: 'ARV-055',
		permission: 'LiveQueue.View'
	},
	{
		id: 'immigration',
		href: '/immigration',
		icon: ScanLine,
		group: 'operations',
		ready: true,
		story: 'ARV-057',
		permission: 'Immigration.View'
	},
	{
		id: 'alertRules',
		href: '/alert-rules',
		icon: BellRing,
		group: 'operations',
		ready: true,
		story: 'ARV-056',
		permission: 'AlertRule.Search'
	},
	{
		id: 'displays',
		href: '/displays',
		icon: MonitorPlay,
		group: 'operations',
		ready: true,
		story: 'ARV-058',
		permission: 'Display.Search'
	},
	{
		// The observer tablet (ARV-104c): only the Validation observer role holds Validation.Capture.
		id: 'validationCapture',
		href: '/validation/capture',
		icon: ClipboardCheck,
		group: 'operations',
		ready: true,
		story: 'ARV-104c',
		permission: 'Validation.Capture'
	},
	{
		id: 'reports',
		href: '/reports',
		icon: FileChartColumn,
		group: 'oversight',
		ready: true,
		story: 'ARV-061',
		permission: 'Report.View'
	},
	{
		id: 'topology',
		href: '/topology',
		icon: Map,
		group: 'administration',
		ready: true,
		story: 'ARV-052',
		permission: 'Site.View'
	},
	{
		id: 'zones',
		href: '/zones',
		icon: Timer,
		group: 'administration',
		ready: true,
		story: 'ARV-053',
		permission: 'ZoneProfile.View'
	},
	{
		id: 'devices',
		href: '/devices',
		icon: Cpu,
		group: 'administration',
		ready: true,
		story: 'ARV-054',
		permission: 'Device.View'
	},
	{
		id: 'users',
		href: '/users',
		icon: UserCog,
		group: 'administration',
		ready: true,
		story: 'ARV-059',
		permission: 'User.Search'
	}
];

/** The items a user with these permissions may see. */
export function visibleItems(can: (permission: string) => boolean): NavItem[] {
	return navItems.filter((item) => can(item.permission));
}

export const navGroups: readonly NavGroup[] = ['operations', 'oversight', 'administration'];

/** Segment-aware match, as in Aman.Web: "/zones" is active on "/zones/12" but not on "/zones-archive". */
export function isActive(href: string, pathname: string): boolean {
	if (href === '/') return pathname === '/';
	return pathname === href || pathname.startsWith(`${href}/`);
}

export function findNavItem(pathname: string): NavItem | undefined {
	return navItems.find((item) => isActive(item.href, pathname));
}
