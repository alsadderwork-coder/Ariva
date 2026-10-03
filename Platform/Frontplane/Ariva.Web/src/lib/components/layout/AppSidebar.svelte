<script lang="ts">
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';
	import Logo from './Logo.svelte';
	import SidebarNav from './SidebarNav.svelte';
	import UserCard from './UserCard.svelte';

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

	<div class={cn('border-t border-sidebar-border', open ? 'p-4' : 'px-1.5 py-3')}>
		<UserCard collapsed={!open} />
	</div>
</aside>
