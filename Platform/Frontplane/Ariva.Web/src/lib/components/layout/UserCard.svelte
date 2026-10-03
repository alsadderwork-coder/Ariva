<script lang="ts">
	import { goto } from '$app/navigation';
	import { LogOut, ShieldCheck } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import { auth } from '$lib/core/auth.svelte';
	import { cn } from '$lib/utils';

	let { collapsed = false }: { collapsed?: boolean } = $props();

	const user = $derived(auth.user);
	const initials = $derived(
		(user?.displayName || user?.userName || '?')
			.split(/[\s._-]+/)
			.filter(Boolean)
			.slice(0, 2)
			.map((part) => part[0]!.toUpperCase())
			.join('')
	);
	const roles = $derived(
		(user?.roles ?? []).map((role) => $_(`auth.user.roles.${role}`, { default: role })).join(', ')
	);
	const sites = $derived(
		user?.allSites
			? $_('auth.user.allSites')
			: $_('auth.user.sites', { values: { sites: (user?.sites ?? []).join(', ') } })
	);

	let signingOut = $state(false);

	async function signOut(): Promise<void> {
		signingOut = true;
		try {
			if (!(await auth.signOut())) toast.error($_('auth.user.signOutFailed'), { duration: 15_000 });
		} finally {
			signingOut = false;
			await goto('/login', { replaceState: true });
		}
	}
</script>

<!-- The signed-in user, their roles and sites, account security and sign out (ARV-051). Text only: names come from
     the server and are interpolated, never rendered as markup. -->
{#if user}
	<div
		data-testid="user-card"
		class={cn('flex flex-col gap-2', collapsed ? 'items-center' : 'rounded-lg bg-muted/60 p-3')}
	>
		<div class={cn('flex items-center gap-3', collapsed && 'justify-center')}>
			<span
				class="flex size-9 shrink-0 items-center justify-center rounded-full bg-sidebar-primary text-xs font-semibold text-sidebar-primary-foreground"
				aria-hidden="true">{initials}</span
			>
			{#if !collapsed}
				<div class="grid min-w-0 text-start leading-tight">
					<span class="truncate text-sm font-semibold" data-testid="user-name"
						>{user.displayName || user.userName}</span
					>
					<span class="truncate text-xs text-muted-foreground">{roles}</span>
					<span class="truncate text-xs text-muted-foreground">{sites}</span>
				</div>
			{/if}
		</div>
		<div class={cn('flex gap-1', collapsed ? 'flex-col' : 'items-center')}>
			<a
				href="/account"
				title={collapsed ? $_('auth.user.account') : undefined}
				class={cn(
					'inline-flex h-8 items-center gap-2 rounded-md px-2 text-xs font-medium text-sidebar-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-2 focus-visible:ring-sidebar-ring focus-visible:outline-none',
					!collapsed && 'flex-1'
				)}
			>
				<ShieldCheck class="size-4 shrink-0" aria-hidden="true" />
				<span class={cn(collapsed && 'sr-only')}>{$_('auth.user.account')}</span>
			</a>
			<button
				type="button"
				data-testid="sign-out"
				disabled={signingOut}
				onclick={signOut}
				title={collapsed ? $_('auth.user.signOut') : undefined}
				class="inline-flex h-8 items-center gap-2 rounded-md px-2 text-xs font-medium text-sidebar-foreground transition-colors hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-2 focus-visible:ring-sidebar-ring focus-visible:outline-none disabled:opacity-60"
			>
				<LogOut class="size-4 shrink-0 rtl:-scale-x-100" aria-hidden="true" />
				<span class={cn(collapsed && 'sr-only')}>{$_('auth.user.signOut')}</span>
			</button>
		</div>
	</div>
{/if}
