<script lang="ts">
	import Skeleton from './Skeleton.svelte';

	interface Props {
		/** What is loading, for screen readers: the screen's own loading text. */
		label: string;
		rows?: number;
		columns?: number;
		testId?: string;
	}

	let { label, rows = 5, columns = 4, testId = 'list-skeleton' }: Props = $props();

	/** Bar widths that vary by row and column, so the placeholder reads as rows of text rather than a block. */
	const widths = ['w-3/4', 'w-1/2', 'w-2/3', 'w-2/5', 'w-5/6', 'w-1/3'];
	const grid = $derived(
		Array.from({ length: rows }, (_, row) =>
			Array.from({ length: columns }, (_, column) => widths[(row * 2 + column * 3) % widths.length])
		)
	);
</script>

<!-- Rows of placeholder bars where a list or table is about to appear (ARV-299a); the loading text is read out. -->
<div role="status" aria-live="polite" data-testid={testId} class="flex flex-col divide-y">
	<span class="sr-only">{label}</span>
	{#each grid as cells, row (row)}
		<div class="flex items-center gap-6 px-4 py-3.5">
			{#each cells as width, column (column)}
				<span class="min-w-0 flex-1"><Skeleton class={width} /></span>
			{/each}
		</div>
	{/each}
</div>
