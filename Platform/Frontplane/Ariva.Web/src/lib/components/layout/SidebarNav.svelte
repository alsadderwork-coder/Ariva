<script lang="ts">
	import { page } from '$app/state';
	import { _ } from 'svelte-i18n';
	import { cn } from '$lib/utils';
	import { auth } from '$lib/core/auth.svelte';
	import { isActive, navGroups, visibleItems } from '$lib/navigation';

	let { collapsed = false, onNavigate }: { collapsed?: boolean; onNavigate?: () => void } =
		$props();

	// Only the screens the signed-in user's permissions cover (GET /api/auth/me); the server still checks every call.
	const allowed = $derived(visibleItems((permission) => auth.can(permission)));
</script>

<nav aria-label={$_('navigation.label')} class="flex flex-col">
	{#each navGroups as group (group)}
		{@const items = allowed.filter((item) => item.group === group)}
		{#if items.length}
			<p
				class={cn(
					'px-3 pt-4 pb-1 text-xs font-semibold tracking-wider text-sidebar-muted uppercase',
					collapsed && 'sr-only'
				)}
			>
				{$_(`navigation.groups.${group}`)}
			</p>
			<ul class={cn('flex flex-col gap-0.5', collapsed && 'items-center')}>
				{#each items as item (item.id)}
					{@const Icon = item.icon}
					{@const active = isActive(item.href, page.url.pathname)}
					{@const label = $_(`navigation.items.${item.id}`)}
					<li class={cn(!collapsed && 'w-full')}>
						{#if item.ready}
							<a
								href={item.href}
								aria-current={active ? 'page' : undefined}
								title={collapsed ? label : undefined}
								onclick={() => onNavigate?.()}
								class={cn(
									'group relative flex items-center gap-3 rounded-sm px-3 py-2.25 text-sm text-sidebar-muted transition-colors hover:text-sidebar-primary focus-visible:ring-2 focus-visible:ring-sidebar-ring focus-visible:outline-none',
									'aria-[current=page]:bg-sidebar-accent aria-[current=page]:font-medium aria-[current=page]:text-sidebar-accent-foreground',
									"after:absolute after:inset-y-1.5 after:start-0 after:w-1 after:rounded-e-full after:content-[''] aria-[current=page]:after:bg-sidebar-primary",
									collapsed && 'size-10 justify-center px-0'
								)}
							>
								<Icon class="size-5.5 shrink-0" strokeWidth={1.75} aria-hidden="true" />
								<span class={cn('truncate', collapsed && 'sr-only')}>{label}</span>
							</a>
						{:else}
							<span
								aria-disabled="true"
								title={$_('navigation.planned', { values: { story: item.story } })}
								class={cn(
									'flex cursor-not-allowed items-center gap-3 rounded-sm px-3 py-2.25 text-sm text-sidebar-muted/60',
									collapsed && 'size-10 justify-center px-0'
								)}
							>
								<Icon class="size-5.5 shrink-0" strokeWidth={1.75} aria-hidden="true" />
								<span class={cn('flex-1 truncate', collapsed && 'sr-only')}>{label}</span>
								{#if !collapsed}
									<span
										class="rounded bg-muted px-1.5 py-0.5 text-[10px] font-medium tracking-wide text-muted-foreground uppercase"
										>{$_('navigation.soon')}</span
									>
								{/if}
							</span>
						{/if}
					</li>
				{/each}
			</ul>
		{/if}
	{/each}
</nav>
