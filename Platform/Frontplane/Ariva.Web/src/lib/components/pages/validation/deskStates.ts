import { DoorClosed, Hourglass, Pause, UserCheck } from '@lucide/svelte';
import type { DeskState } from '$lib/core/validation';

/**
 * How the desk log shows a state (ARV-104d): the live desk panel's colours (Serving green, Idle blue, Paused amber,
 * Closed grey) and an icon, always beside the state's word or its screen reader text, never colour alone.
 */
export const deskStateTone: Record<DeskState, string> = {
	Serving: 'border-status-success-border bg-status-success text-status-success-foreground',
	Idle: 'border-status-info-border bg-status-info text-status-info-foreground',
	Paused: 'border-status-warning-border bg-status-warning text-status-warning-foreground',
	Closed: 'border-status-neutral-border bg-status-neutral text-status-neutral-foreground'
};

export const deskStateIcon = {
	Serving: UserCheck,
	Idle: Hourglass,
	Paused: Pause,
	Closed: DoorClosed
} as const;
