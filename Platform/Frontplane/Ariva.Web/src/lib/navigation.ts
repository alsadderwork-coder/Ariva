import type { Component } from 'svelte';
import {
	Activity,
	BellRing,
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
}

/** Sidebar navigation, grouped like Aman.Web (Operations, Oversight, Administration). */
export const navItems: readonly NavItem[] = [
	{
		id: 'liveOperations',
		href: '/',
		icon: Activity,
		group: 'operations',
		ready: true,
		story: 'ARV-055'
	},
	{
		id: 'immigration',
		href: '/immigration',
		icon: ScanLine,
		group: 'operations',
		ready: false,
		story: 'ARV-057'
	},
	{
		id: 'alerts',
		href: '/alerts',
		icon: BellRing,
		group: 'operations',
		ready: false,
		story: 'ARV-056'
	},
	{
		id: 'displays',
		href: '/displays',
		icon: MonitorPlay,
		group: 'operations',
		ready: false,
		story: 'ARV-058'
	},
	{
		id: 'reports',
		href: '/reports',
		icon: FileChartColumn,
		group: 'oversight',
		ready: false,
		story: 'ARV-061'
	},
	{
		id: 'topology',
		href: '/topology',
		icon: Map,
		group: 'administration',
		ready: false,
		story: 'ARV-052'
	},
	{
		id: 'zones',
		href: '/zones',
		icon: Timer,
		group: 'administration',
		ready: false,
		story: 'ARV-053'
	},
	{
		id: 'devices',
		href: '/devices',
		icon: Cpu,
		group: 'administration',
		ready: false,
		story: 'ARV-054'
	},
	{
		id: 'users',
		href: '/users',
		icon: UserCog,
		group: 'administration',
		ready: false,
		story: 'ARV-059'
	}
];

export const navGroups: readonly NavGroup[] = ['operations', 'oversight', 'administration'];

/** Segment-aware match, as in Aman.Web: "/zones" is active on "/zones/12" but not on "/zones-archive". */
export function isActive(href: string, pathname: string): boolean {
	if (href === '/') return pathname === '/';
	return pathname === href || pathname.startsWith(`${href}/`);
}

export function findNavItem(pathname: string): NavItem | undefined {
	return navItems.find((item) => isActive(item.href, pathname));
}
