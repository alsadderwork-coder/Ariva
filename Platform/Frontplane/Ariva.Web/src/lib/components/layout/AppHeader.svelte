<script lang="ts">
	import { page } from '$app/state';
	import { ChevronRight, Languages, Moon, PanelLeft, Sun } from '@lucide/svelte';
	import { _, locale } from 'svelte-i18n';
	import { findNavItem } from '$lib/navigation';
	import { theme } from '$lib/theme/theme.svelte';

	let { sidebarOpen, onToggleSidebar }: { sidebarOpen: boolean; onToggleSidebar: () => void } =
		$props();

	const current = $derived(findNavItem(page.url.pathname));
	const crumb = $derived(
		page.status >= 400
			? $_('errors.notFound.title')
			: current
				? $_(`navigation.items.${current.id}`)
				: ''
	);

	function toggleLanguage(): void {
		locale.set($locale?.startsWith('ar') ? 'en' : 'ar');
	}
</script>

<!-- Aman.Web header: sticky, card background, 4rem high, breadcrumb on the start side, actions on the end side. -->
<header class="sticky top-0 z-40 border-b bg-card">
	<div class="flex h-16 items-center gap-3 px-4 md:px-6">
		<button
			type="button"
			data-testid="sidebar-toggle"
			aria-label={$_('shell.toggleSidebar')}
			aria-expanded={sidebarOpen}
			onclick={onToggleSidebar}
			class="inline-flex size-9 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			<PanelLeft class="size-5 rtl:-scale-x-100" aria-hidden="true" />
		</button>

		<nav aria-label={$_('shell.breadcrumb')} class="min-w-0 flex-1">
			<ol class="flex items-center gap-1.5 text-sm text-muted-foreground">
				<li class="truncate">{$_('app.title')}</li>
				{#if crumb}
					<li aria-hidden="true"><ChevronRight class="size-4 rtl:-scale-x-100" /></li>
					<li class="truncate font-medium text-foreground" aria-current="page">{crumb}</li>
				{/if}
			</ol>
		</nav>

		<div class="flex items-center gap-2">
			<span
				data-testid="environment-chip"
				class="hidden rounded-full border border-status-info-border bg-status-info px-2.5 py-1 text-xs font-medium text-status-info-foreground sm:inline"
			>
				{$_('shell.environment.demo')}
			</span>

			<button
				type="button"
				data-testid="theme-toggle"
				aria-label={theme.resolved === 'dark'
					? $_('shell.theme.toLight')
					: $_('shell.theme.toDark')}
				onclick={() => theme.toggle()}
				class="inline-flex h-10 items-center justify-center rounded-lg border bg-card px-3 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				{#if theme.resolved === 'dark'}
					<Sun class="size-4" aria-hidden="true" />
				{:else}
					<Moon class="size-4" aria-hidden="true" />
				{/if}
			</button>

			<button
				type="button"
				data-testid="language-toggle"
				aria-label={$_('app.language.label')}
				onclick={toggleLanguage}
				class="inline-flex h-10 items-center gap-2 rounded-lg border bg-card px-3 text-sm text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				<Languages class="size-4" aria-hidden="true" />
				<span lang={$locale?.startsWith('ar') ? 'en' : 'ar'}>{$_('app.language.toggle')}</span>
			</button>
		</div>
	</div>
</header>
