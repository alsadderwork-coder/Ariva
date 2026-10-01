<script lang="ts">
	import type { Snippet } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';
	import AppHeader from './AppHeader.svelte';
	import AppSidebar from './AppSidebar.svelte';
	import Logo from './Logo.svelte';
	import SidebarNav from './SidebarNav.svelte';

	let { children }: { children: Snippet } = $props();

	// Same cookie as Aman.Web's SidebarProvider, so the preference survives reloads.
	const SIDEBAR_COOKIE = 'sidebar:state';
	const SIDEBAR_COOKIE_MAX_AGE = 60 * 60 * 24 * 7;

	function readSidebarCookie(): boolean {
		const match = document.cookie.match(/(?:^|;\s*)sidebar:state=([^;]*)/);
		return match ? match[1] === 'true' : true;
	}

	let sidebarOpen = $state(readSidebarCookie());
	let mobileOpen = $state(false);

	function toggleSidebar(): void {
		if (window.matchMedia('(min-width: 768px)').matches) {
			sidebarOpen = !sidebarOpen;
			document.cookie = `${SIDEBAR_COOKIE}=${sidebarOpen}; path=/; max-age=${SIDEBAR_COOKIE_MAX_AGE}; SameSite=Strict`;
		} else {
			mobileOpen = !mobileOpen;
		}
	}

	// Mod+B toggles the sidebar, as in Aman.Web.
	function onKeydown(event: KeyboardEvent): void {
		if (event.key.toLowerCase() === 'b' && (event.metaKey || event.ctrlKey)) {
			event.preventDefault();
			toggleSidebar();
		}
		if (event.key === 'Escape' && mobileOpen) mobileOpen = false;
	}
</script>

<svelte:window onkeydown={onKeydown} />

<a
	href="#main"
	class="sr-only z-50 rounded-md bg-card px-4 py-2 text-sm font-medium shadow focus:not-sr-only focus:fixed focus:start-4 focus:top-4"
>
	{$_('shell.skipToContent')}
</a>

<div class="flex min-h-dvh w-full bg-background">
	<AppSidebar open={sidebarOpen} />

	{#if mobileOpen}
		<div
			class="fixed inset-0 z-50 md:hidden"
			role="dialog"
			aria-modal="true"
			aria-label={$_('navigation.label')}
		>
			<button
				type="button"
				class="absolute inset-0 bg-black/40"
				aria-label={$_('shell.closeMenu')}
				onclick={() => (mobileOpen = false)}
			></button>
			<div
				class="absolute inset-y-0 start-0 flex w-80 flex-col border-e border-sidebar-border bg-sidebar text-sidebar-foreground shadow-xl"
			>
				<div class="flex items-center gap-3 px-6 pt-4.5">
					<Logo class="size-8" />
					<span class="text-lg font-bold tracking-tight">{$_('app.title')}</span>
				</div>
				<div class="sidebar-scroll flex-1 overflow-y-auto p-4">
					<SidebarNav onNavigate={() => (mobileOpen = false)} />
				</div>
			</div>
		</div>
	{/if}

	<div class="flex min-w-0 flex-1 flex-col">
		<AppHeader {sidebarOpen} onToggleSidebar={toggleSidebar} />
		<main
			id="main"
			tabindex="-1"
			class={cn(
				'min-w-0 flex-1 overflow-x-clip bg-background p-4 pt-0.5 focus:outline-none md:p-6 md:pt-1.5 lg:p-8 lg:pt-1.5'
			)}
		>
			{@render children()}
		</main>
	</div>
</div>
