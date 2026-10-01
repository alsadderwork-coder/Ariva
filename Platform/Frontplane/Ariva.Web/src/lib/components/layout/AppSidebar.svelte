<script lang="ts">
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';
	import Logo from './Logo.svelte';
	import SidebarNav from './SidebarNav.svelte';

	let { open = true }: { open?: boolean } = $props();
</script>

<!-- Aman.Web measurements: 19.5rem expanded, 4rem icon rail. The logical border (border-e) and the flex row put
     the sidebar on the right in Arabic without a separate code path. -->
<aside
	data-testid="app-sidebar"
	data-state={open ? 'expanded' : 'collapsed'}
	class={cn(
		'sticky top-0 hidden h-dvh shrink-0 flex-col border-e border-sidebar-border bg-sidebar text-sidebar-foreground transition-[width] duration-200 ease-out md:flex',
		open ? 'w-[19.5rem]' : 'w-16'
	)}
>
	<div class={cn('flex items-center gap-3 px-4 pt-4.5', open ? 'px-6' : 'justify-center')}>
		<Logo class="size-8 shrink-0" />
		{#if open}
			<div class="grid min-w-0 text-start">
				<span class="truncate text-lg font-bold tracking-tight">{$_('app.title')}</span>
				<span
					class="truncate text-xs font-medium tracking-wider text-sidebar-foreground/70 uppercase"
					>{$_('app.tagline')}</span
				>
			</div>
		{/if}
	</div>

	<div class={cn('sidebar-scroll flex-1 overflow-y-auto p-4', !open && 'px-1.5')}>
		<SidebarNav collapsed={!open} />
	</div>

	{#if open}
		<div class="border-t border-sidebar-border p-4">
			<div class="flex items-center gap-3 rounded-lg bg-muted/60 p-3">
				<span
					class="flex size-9 shrink-0 items-center justify-center rounded-full bg-sidebar-primary text-xs font-semibold text-sidebar-primary-foreground"
					aria-hidden="true">DM</span
				>
				<div class="grid min-w-0 text-start leading-tight">
					<span class="truncate text-sm font-semibold">{$_('shell.site.name')}</span>
					<span class="truncate text-xs text-muted-foreground">{$_('shell.site.detail')}</span>
				</div>
			</div>
		</div>
	{/if}
</aside>
