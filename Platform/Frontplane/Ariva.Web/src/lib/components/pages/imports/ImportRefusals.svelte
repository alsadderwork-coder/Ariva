<script lang="ts">
	import { _ } from 'svelte-i18n';
	import { columnLetters, type RowError } from '$lib/core/csvImports';

	interface Props {
		errors: RowError[];
		/** Every refusal of the file; the answer lists the first 100. */
		total: number;
		/** The reason in the reader's language: translated when the screen has words for it, else as the server wrote it. */
		reasonText: (reason: string) => string;
	}

	let { errors, total, reasonText }: Props = $props();
</script>

<!-- CWE-79: rows, columns and reasons are numbers and the server's fixed texts, interpolated as text only. -->
<section class="flex flex-col gap-2">
	<h3 id="import-refusals-title" class="text-sm font-semibold">
		{$_('csvImport.refusals', { values: { count: total } })}
	</h3>
	{#if total > errors.length}
		<p data-testid="import-refusals-first" class="text-xs text-muted-foreground">
			{$_('csvImport.firstOf', { values: { shown: errors.length, count: total } })}
		</p>
	{/if}
	<!-- The table scrolls inside the dialog, so its region takes the keyboard focus (WCAG 2.1.1, axe
	     scrollable-region-focusable), named by the heading above it. -->
	<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
	<div
		class="max-h-72 overflow-auto rounded-lg border focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		role="region"
		aria-labelledby="import-refusals-title"
		tabindex="0"
	>
		<table class="w-full text-sm" data-testid="import-refusals">
			<thead
				class="sticky top-0 bg-surface-2 text-xs font-semibold tracking-wide text-tertiary uppercase"
			>
				<tr>
					<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.row')}</th>
					<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.column')}</th>
					<th scope="col" class="px-4 py-2.5 text-start">{$_('csvImport.columns.reason')}</th>
				</tr>
			</thead>
			<tbody>
				{#each errors as error, index (index)}
					<tr data-testid="import-refusal" class="align-top odd:bg-surface even:bg-surface-2/50">
						<td class="px-4 py-2 tabular-nums">{error.row}</td>
						<td class="px-4 py-2 whitespace-nowrap tabular-nums">
							{#if error.column && error.column > 0}
								{error.column}
								<span class="text-muted-foreground" dir="ltr">({columnLetters(error.column)})</span>
							{:else}
								<span class="text-muted-foreground">{$_('csvImport.wholeRow')}</span>
							{/if}
						</td>
						<td class="px-4 py-2">{reasonText(error.reason)}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
</section>
