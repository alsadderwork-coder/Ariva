<script lang="ts">
	import { Trash2 } from '@lucide/svelte';
	import { onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import FormField from '$lib/components/shared/FormField.svelte';
	import SelectField from '$lib/components/shared/SelectField.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as topology from '$lib/core/topology';
	import type { DeskCodeMapping } from '$lib/core/topology';

	let { deskId }: { deskId: string } = $props();

	let items = $state<DeskCodeMapping[]>([]);
	let system = $state<string>('Aman');
	let externalCode = $state('');
	let busy = $state(false);
	let failedToLoad = $state('');
	/** The mapping waiting for its delete to be confirmed on the page. */
	let confirming = $state<string | null>(null);

	const canSee = $derived(auth.can('DeskCodeMapping.Search'));
	const canAdd = $derived(auth.can('DeskCodeMapping.Create'));
	const canRemove = $derived(auth.can('DeskCodeMapping.Delete'));

	async function load(): Promise<void> {
		const result = await topology.mappings(deskId);
		failedToLoad = result.hasErrors ? (result.errorMessages[0] ?? '') : '';
		items = result.data?.data ?? [];
	}

	onMount(() => {
		if (canSee) void load();
	});

	async function add(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		busy = true;
		const result = await topology.createMapping({
			system,
			externalCode: externalCode.trim(),
			deskId
		});
		busy = false;
		if (result.hasErrors) {
			toast.error(
				$_('topology.messages.failed', { values: { reason: result.errorMessages[0] ?? '' } })
			);
			return;
		}
		externalCode = '';
		toast.success(
			$_('topology.messages.created', { values: { code: result.data?.externalCode ?? '' } })
		);
		await load();
	}

	async function remove(mapping: DeskCodeMapping): Promise<void> {
		if (busy) return;
		busy = true;
		const result = await topology.removeMapping(mapping.id);
		busy = false;
		confirming = null;
		if (result.hasErrors) {
			toast.error(
				$_('topology.messages.failed', { values: { reason: result.errorMessages[0] ?? '' } })
			);
			return;
		}
		toast.success($_('topology.messages.deleted', { values: { code: mapping.externalCode } }));
		await load();
	}
</script>

{#if canSee}
	<section aria-labelledby="mappings-title" data-testid="desk-mappings" class="flex flex-col gap-3">
		<div>
			<h3 id="mappings-title" class="text-base font-semibold">{$_('topology.mappings.title')}</h3>
			<p class="text-xs text-muted-foreground">{$_('topology.mappings.description')}</p>
		</div>
		{#if failedToLoad}
			<p role="alert" class="text-sm text-status-danger-foreground">
				{$_('topology.messages.failed', { values: { reason: failedToLoad } })}
			</p>
		{:else if items.length === 0}
			<p class="text-sm text-muted-foreground">{$_('topology.mappings.none')}</p>
		{:else}
			<ul class="flex flex-col divide-y rounded-lg border">
				{#each items as mapping (mapping.id)}
					<li class="flex flex-wrap items-center gap-3 px-3 py-2 text-sm">
						<span class="w-14 text-xs font-semibold text-tertiary uppercase"
							>{$_(`topology.mappings.${mapping.system}`, { default: mapping.system })}</span
						>
						<span class="flex-1 font-mono">{mapping.externalCode}</span>
						{#if canRemove}
							{#if confirming === mapping.id}
								<button
									type="button"
									data-testid="confirm-remove-mapping"
									disabled={busy}
									onclick={() => remove(mapping)}
									class="inline-flex h-7 items-center gap-1 rounded-md border border-status-danger-border bg-status-danger px-2 text-xs font-medium text-status-danger-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
								>
									<Trash2 class="size-3.5" aria-hidden="true" />
									{$_('topology.actions.confirmDelete', { values: { code: mapping.externalCode } })}
								</button>
								<button
									type="button"
									onclick={() => (confirming = null)}
									class="inline-flex h-7 items-center rounded-md border bg-card px-2 text-xs font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
								>
									{$_('topology.actions.cancel')}
								</button>
							{:else}
								<button
									type="button"
									data-testid="remove-mapping"
									onclick={() => (confirming = mapping.id)}
									aria-label="{$_('topology.actions.delete')} {mapping.externalCode}"
									class="inline-flex size-7 items-center justify-center rounded-md text-muted-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
								>
									<Trash2 class="size-4" aria-hidden="true" />
								</button>
							{/if}
						{/if}
					</li>
				{/each}
			</ul>
		{/if}
		{#if canAdd}
			<form
				class="grid grid-cols-[7rem_1fr_auto] items-end gap-2"
				onsubmit={add}
				data-testid="add-mapping"
			>
				<SelectField
					id="mapping-system"
					label={$_('topology.mappings.system')}
					bind:value={system}
					options={topology.externalSystems.map((s) => ({
						value: s,
						label: $_(`topology.mappings.${s}`)
					}))}
				/>
				<FormField
					id="mapping-code"
					label={$_('topology.mappings.externalCode')}
					bind:value={externalCode}
					required
					maxlength={32}
				/>
				<button
					type="submit"
					disabled={busy || !externalCode.trim()}
					class="inline-flex h-9 items-center rounded-md bg-button-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
				>
					{$_('topology.mappings.add')}
				</button>
			</form>
		{/if}
	</section>
{/if}
