<script lang="ts">
	import { _ } from 'svelte-i18n';
	import type { DeviceRow, ImportKind, MappingRow } from '$lib/core/csvImports';
	import { families } from '$lib/core/devices';
	import { externalSystems } from '$lib/core/topology';

	interface Props {
		kind: ImportKind;
		devices?: DeviceRow[];
		mappings?: MappingRow[];
	}

	let { kind, devices = [], mappings = [] }: Props = $props();

	/** Rows listed at most: a mapping file may pass 10,000 rows, more than a table in a dialog helps with. */
	const shown = 500;
	const total = $derived(kind === 'devices' ? devices.length : mappings.length);

	// A kind the screen has words for is translated; anything else is shown as written, never used as a message key.
	const family = (value: string): string =>
		(families as readonly string[]).includes(value) ? $_(`devices.families.${value}`) : value;
	const system = (value: string): string =>
		(externalSystems as readonly string[]).includes(value)
			? $_(`topology.mappings.${value}`)
			: value;
</script>

<!-- CWE-79 (ARV-123a review, N6): every cell is the file's own text (a model or a zone may be markup), interpolated as
     text only, never as HTML. -->
<section class="flex flex-col gap-2">
	<h3 id="import-rows-title" class="text-sm font-semibold">
		{$_('csvImport.passing', { values: { count: total } })}
	</h3>
	{#if total === 0}
		<p class="text-sm text-muted-foreground">{$_('csvImport.noPassing')}</p>
	{:else}
		{#if total > shown}
			<p class="text-xs text-muted-foreground">
				{$_('csvImport.rowsFirstOf', { values: { shown, count: total } })}
			</p>
		{/if}
		<!-- The table scrolls inside the dialog, so its region takes the keyboard focus (WCAG 2.1.1, axe
		     scrollable-region-focusable), named by the heading above it. -->
		<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
		<div
			class="max-h-72 overflow-auto rounded-lg border focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			role="region"
			aria-labelledby="import-rows-title"
			tabindex="0"
		>
			<table class="w-full text-sm" data-testid="import-rows">
				<thead
					class="sticky top-0 bg-surface-2 text-xs font-semibold tracking-wide text-tertiary uppercase"
				>
					<tr>
						<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.row')}</th>
						{#if kind === 'devices'}
							<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.code')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.family')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.model')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.terminal')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.level')}</th>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.queueZone')}</th
							>
						{:else}
							<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.system')}</th>
							<th scope="col" class="px-4 py-2.5 text-start"
								>{$_('csvImport.columns.externalCode')}</th
							>
							<th scope="col" class="px-4 py-2.5 text-start"
								>{$_('csvImport.columns.checkpoint')}</th
							>
							<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.desk')}</th>
						{/if}
					</tr>
				</thead>
				<tbody>
					{#if kind === 'devices'}
						{#each devices.slice(0, shown) as device (device.row)}
							<tr data-testid="import-row" class="odd:bg-surface even:bg-surface-2/50">
								<td class="px-4 py-2 tabular-nums">{device.row}</td>
								<td class="px-4 py-2 font-mono" dir="ltr">{device.code}</td>
								<td class="px-4 py-2">{family(device.family)}</td>
								<td class="px-4 py-2 break-all">{device.model}</td>
								<td class="px-4 py-2 font-mono" dir="ltr">{device.terminal}</td>
								<td class="px-4 py-2 font-mono" dir="ltr">{device.level}</td>
								<td class="px-4 py-2 break-all">{device.queueZone}</td>
							</tr>
						{/each}
					{:else}
						{#each mappings.slice(0, shown) as mapping (mapping.row)}
							<tr data-testid="import-row" class="odd:bg-surface even:bg-surface-2/50">
								<td class="px-4 py-2 tabular-nums">{mapping.row}</td>
								<td class="px-4 py-2">{system(mapping.system)}</td>
								<td class="px-4 py-2 font-mono break-all" dir="ltr">{mapping.externalCode}</td>
								<td class="px-4 py-2 font-mono" dir="ltr">{mapping.checkpoint}</td>
								<td class="px-4 py-2 font-mono" dir="ltr">{mapping.desk}</td>
							</tr>
						{/each}
					{/if}
				</tbody>
			</table>
		</div>
	{/if}
</section>
