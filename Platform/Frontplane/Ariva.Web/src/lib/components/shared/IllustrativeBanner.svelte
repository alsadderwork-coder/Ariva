<script lang="ts">
	import { TriangleAlert } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import type { Site } from '$lib/core/topology';

	/**
	 * ARV-139a: the persistent "Illustrative, not surveyed" banner of a demo site modelled on a real airport from public
	 * information only. Shown on every screen while the chosen site carries the flag, which only the server's demo seed
	 * sets; nothing here can hide it except choosing another site. Text from the dictionaries only (CWE-79).
	 */
	let { site }: { site: Site | undefined } = $props();
</script>

{#if site?.illustrative === true}
	<div
		role="note"
		data-testid="illustrative-banner"
		class="mb-4 flex items-start gap-3 rounded-xl border border-status-warning-border bg-status-warning px-4 py-3 text-status-warning-foreground"
	>
		<TriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
		<p class="text-sm">
			<strong class="font-semibold" data-testid="illustrative-banner-title"
				>{$_('site.illustrative.title')}</strong
			>
			<span>{$_('site.illustrative.detail', { values: { site: site.code } })}</span>
		</p>
	</div>
{/if}
