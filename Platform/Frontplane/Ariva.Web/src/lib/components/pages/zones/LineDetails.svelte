<script lang="ts">
	import { Undo2 } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import type { Line, Point } from '$lib/core/zones';
	import { round } from '$lib/core/zones';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';

	interface Props {
		line: Line;
		ends: [Point, Point];
		zoneName: string | null;
		editable: boolean;
		dirty: boolean;
		width: number;
		depth: number;
		onEnds: (ends: [Point, Point]) => void;
		onSave: () => void | Promise<void>;
		onRevert: () => void;
		onDelete: () => void | Promise<void>;
	}

	let {
		line,
		ends,
		zoneName,
		editable,
		dirty,
		width,
		depth,
		onEnds,
		onSave,
		onRevert,
		onDelete
	}: Props = $props();

	function set(which: 0 | 1, axis: 'x' | 'y', value: number): void {
		if (!Number.isFinite(value)) return;
		const limit = axis === 'x' ? width : depth;
		const next: [Point, Point] = [{ ...ends[0] }, { ...ends[1] }];
		next[which] = { ...next[which], [axis]: round(Math.min(Math.max(value, 0), limit)) };
		onEnds(next);
	}
</script>

<section
	data-testid="line-details"
	aria-labelledby="line-details-title"
	class="flex flex-col gap-3"
>
	<div>
		<p class="text-xs font-semibold tracking-wide text-tertiary uppercase">
			{$_(`zones.roles.${line.role}`)}
		</p>
		<h2 id="line-details-title" class="text-lg font-semibold">{line.name}</h2>
		<p class="text-xs text-muted-foreground tabular-nums">
			{$_('zones.length', { values: { length: line.lengthMetres.toFixed(1) } })}
			{#if zoneName}· {$_('zones.queueOf', { values: { name: zoneName } })}{/if}
		</p>
	</div>
	<div class="grid grid-cols-2 gap-3 text-sm">
		{#each [0, 1] as const as which (which)}
			<fieldset class="flex flex-col gap-1.5">
				<legend class="mb-1 text-xs font-semibold"
					>{which === 0 ? $_('zones.start') : $_('zones.end')}</legend
				>
				{#each ['x', 'y'] as const as axis (axis)}
					<label class="flex items-center gap-2 text-xs">
						<span class="w-8">{axis} (m)</span>
						<input
							type="number"
							step="0.1"
							min="0"
							max={axis === 'x' ? width : depth}
							value={ends[which][axis]}
							readonly={!editable}
							onchange={(event) => set(which, axis, event.currentTarget.valueAsNumber)}
							class="h-8 w-24 rounded-md border border-input bg-background px-2 text-sm tabular-nums read-only:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						/>
					</label>
				{/each}
			</fieldset>
		{/each}
	</div>
	{#if editable}
		<div class="flex flex-wrap items-center gap-2">
			<button
				type="button"
				data-testid="save-line"
				onclick={onSave}
				disabled={!dirty}
				class="inline-flex h-9 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
			>
				{$_('zones.saveLine')}
			</button>
			{#if dirty}
				<button
					type="button"
					onclick={onRevert}
					class="inline-flex h-9 items-center gap-1 rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					<Undo2 class="size-4" aria-hidden="true" />
					{$_('zones.revert')}
				</button>
			{/if}
			<ConfirmButton
				label={$_('topology.actions.delete')}
				confirmLabel={$_('topology.actions.confirmDelete', { values: { code: line.name } })}
				testId="delete-line"
				onConfirm={onDelete}
			/>
		</div>
	{/if}
</section>
