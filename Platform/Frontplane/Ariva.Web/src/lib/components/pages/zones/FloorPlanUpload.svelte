<script lang="ts">
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import FormField from '$lib/components/shared/FormField.svelte';
	import * as zones from '$lib/core/zones';

	interface Props {
		levelId: string;
		onUploaded: () => void | Promise<void>;
		onCancel: () => void;
	}

	let { levelId, onUploaded, onCancel }: Props = $props();

	let files = $state<FileList>();
	let metresPerPixel = $state(0.05);
	let originX = $state(0);
	let originY = $state(0);
	let busy = $state(false);

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		const file = files?.[0];
		if (!file || busy) return;
		busy = true;
		const result = await zones.uploadFloorPlan(levelId, file, metresPerPixel, originX, originY);
		busy = false;
		if (result.hasErrors) {
			toast.error(
				$_('topology.messages.failed', { values: { reason: result.errorMessages[0] ?? '' } })
			);
			return;
		}
		toast.success($_('zones.planUploaded'));
		await onUploaded();
	}
</script>

<form
	class="flex flex-col gap-2.5 rounded-xl border bg-card p-4"
	onsubmit={submit}
	data-testid="plan-form"
>
	<div class="flex flex-col gap-1">
		<label for="plan-file" class="text-xs font-medium">{$_('zones.planFile')}</label>
		<input
			id="plan-file"
			type="file"
			accept="image/png,image/jpeg,image/svg+xml"
			bind:files
			required
			class="text-sm"
		/>
		<p class="text-xs text-muted-foreground">{$_('zones.planHint')}</p>
	</div>
	<div class="grid grid-cols-3 gap-2">
		<FormField
			id="plan-scale"
			label={$_('zones.metresPerPixel')}
			type="number"
			bind:value={metresPerPixel}
			min={0.000001}
			max={10}
			step="any"
			required
		/>
		<FormField
			id="plan-x"
			label={$_('zones.originX')}
			type="number"
			bind:value={originX}
			step="any"
			required
		/>
		<FormField
			id="plan-y"
			label={$_('zones.originY')}
			type="number"
			bind:value={originY}
			step="any"
			required
		/>
	</div>
	<div class="flex gap-2">
		<button
			type="submit"
			disabled={busy || !files?.length}
			class="inline-flex h-9 items-center rounded-md bg-button-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
		>
			{$_('zones.upload')}
		</button>
		<button
			type="button"
			onclick={onCancel}
			class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('topology.actions.cancel')}
		</button>
	</div>
</form>
