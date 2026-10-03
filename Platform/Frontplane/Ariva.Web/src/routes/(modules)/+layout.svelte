<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { onMount, type Snippet } from 'svelte';
	import { _ } from 'svelte-i18n';
	import StepUpDialog from '$lib/components/auth/StepUpDialog.svelte';
	import AppShell from '$lib/components/layout/AppShell.svelte';
	import { auth, safeNext } from '$lib/core/auth.svelte';

	let { children }: { children: Snippet } = $props();

	/** Sign-in, returning to this page afterwards. */
	function toLogin(): void {
		const next = safeNext(`${page.url.pathname}${page.url.search}`);
		void goto(next === '/' ? '/login' : `/login?next=${encodeURIComponent(next)}`, {
			replaceState: true
		});
	}

	// The token lives in memory only: a reload or a new tab gets one from the refresh cookie (auth.start), then the
	// user; a pending account finishes its first sign-in under /setup. When the session ends (here, in another tab, or
	// on the server) the screen goes back to sign-in.
	onMount(() => {
		void auth.start();
	});

	$effect(() => {
		if (!auth.ready) return;
		if (!auth.token) {
			toLogin();
		} else if (!auth.user) {
			void auth.loadUser().then((user) => {
				if (!user) void auth.sessionEnded(`${page.url.pathname}${page.url.search}`);
				else if (user.pending) void goto('/setup', { replaceState: true });
			});
		} else if (auth.user.pending) {
			void goto('/setup', { replaceState: true });
		}
	});
</script>

{#if auth.token && auth.user && !auth.user.pending}
	<AppShell>
		{@render children()}
	</AppShell>
	<StepUpDialog />
{:else}
	<div class="flex min-h-dvh items-center justify-center bg-background" aria-busy="true">
		<p class="text-sm text-muted-foreground" role="status">{$_('auth.loading')}</p>
	</div>
{/if}
