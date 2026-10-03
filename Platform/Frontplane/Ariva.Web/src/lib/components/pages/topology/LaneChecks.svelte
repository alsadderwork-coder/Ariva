<script lang="ts">
	import { _ } from 'svelte-i18n';
	import { laneCategories } from '$lib/core/topology';

	let { id, value = $bindable([]) }: { id: string; value: string[] } = $props();

	function toggle(code: string, on: boolean): void {
		value = on ? [...value.filter((c) => c !== code), code] : value.filter((c) => c !== code);
	}
</script>

<fieldset class="flex min-w-0 flex-col gap-1">
	<legend class="mb-1 text-xs font-medium">{$_('topology.fields.laneCategories')}</legend>
	<div class="flex flex-wrap gap-x-3 gap-y-1">
		{#each laneCategories as code (code)}
			<label class="inline-flex items-center gap-1.5 text-xs" title={$_(`topology.lanes.${code}`)}>
				<input
					type="checkbox"
					name="{id}-{code}"
					checked={value.includes(code)}
					onchange={(event) => toggle(code, event.currentTarget.checked)}
					class="size-3.5 rounded border-input"
				/>
				<span class="font-mono">{code}</span>
				<span class="sr-only">{$_(`topology.lanes.${code}`)}</span>
			</label>
		{/each}
	</div>
</fieldset>
