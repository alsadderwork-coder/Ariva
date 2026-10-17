<script lang="ts">
	import { ShieldCheck } from '@lucide/svelte';
	import { onDestroy, untrack } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import * as operations from '$lib/core/operations';
	import type { ZoneHealthBin } from '$lib/core/operations';
	import { completionOf, latestBins, occupancyOf, residualOf } from './quality';

	interface Props {
		siteCode: string;
		/** Queue zones of the site's published profile, by name, with the label to show (a lane's name, for example). */
		zones: { name: string; label?: string }[];
		/** How many of each zone's latest bins to show. */
		bins: number;
		heading: string;
		description: string;
	}

	let { siteCode, zones, bins, heading, description }: Props = $props();

	/** At most this many zones are read for one panel (one request each). */
	const maxZones = 12;
	/** The bins read: those that started within the last two hours. */
	const lookBackMs = 2 * 60 * 60 * 1000;
	const refreshMs = 60_000;
	const id = $props.id();

	/** Each zone's bins by name; null when the read failed. */
	let health = $state<Record<string, ZoneHealthBin[] | null>>({});
	let loaded = $state(false);
	let generation = 0;
	let destroyed = false;

	const isArabic = $derived($locale?.startsWith('ar') ?? false);
	const numbers = $derived(
		new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en', { maximumFractionDigits: 0 })
	);
	const signed = $derived(
		new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en', { signDisplay: 'exceptZero' })
	);
	const percent = $derived(
		new Intl.NumberFormat(isArabic ? 'ar-u-nu-latn' : 'en', {
			style: 'percent',
			maximumFractionDigits: 1
		})
	);
	const shown = $derived(zones.slice(0, maxZones));
	const many = $derived(shown.length > 1);

	/**
	 * Reads the zones' bins for the site and zones given; an answer that arrives after another site or other zones were
	 * chosen (a later generation) is dropped, so one zone's figures never show under another's name.
	 */
	async function load(site: string, names: string[]): Promise<void> {
		const mine = ++generation;
		const to = new Date(Date.now() + 60_000);
		const from = new Date(to.getTime() - lookBackMs);
		const answers = await Promise.all(
			names.map(async (name) => {
				const result = await operations.zoneHealth(site, name, from, to);
				return [name, result.hasErrors || !result.data ? null : result.data.bins] as const;
			})
		);
		if (destroyed || mine !== generation) return;
		health = Object.fromEntries(answers);
		loaded = true;
	}

	/**
	 * What the panel reads, as one text: the parent may hand over a new list with the same zones (the immigration view is
	 * read again every minute), which must neither reload the panel nor blank it.
	 */
	const key = $derived(JSON.stringify([siteCode, ...shown.map((zone) => zone.name)]));

	$effect(() => {
		void key;
		const [site, names] = untrack(() => [siteCode, shown.map((zone) => zone.name)] as const);
		untrack(() => {
			loaded = false;
			health = {};
			void load(site, names);
		});
		const timer = setInterval(() => void load(site, names), refreshMs);
		return () => clearInterval(timer);
	});

	onDestroy(() => {
		destroyed = true;
	});

	function time(value: string): string {
		return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
	}

	function statusLabel(status: string): string {
		return status === 'Final' || status === 'Provisional'
			? $_(`liveOperations.quality.status.${status}`)
			: status;
	}

	function residualLabel(bin: ZoneHealthBin): string {
		const judged = residualOf(bin);
		return judged.kind === 'notMeasured'
			? $_('liveOperations.quality.residual.notMeasured')
			: $_('liveOperations.quality.residual.value', {
					values: { value: signed.format(judged.value) }
				});
	}

	function completionLabel(bin: ZoneHealthBin): string {
		const judged = completionOf(bin);
		if (judged.kind === 'noTracks') return $_('liveOperations.quality.completion.noTracks');
		return $_(`liveOperations.quality.completion.${judged.kind}`, {
			values: {
				percent: percent.format(judged.rate),
				exited: numbers.format(judged.exited),
				entered: numbers.format(judged.entered)
			}
		});
	}

	function occupancyLabel(bin: ZoneHealthBin): string {
		const judged = occupancyOf(bin);
		if (judged.kind === 'notChecked') return $_('liveOperations.quality.occupancy.notChecked');
		if (judged.kind === 'within')
			return $_('liveOperations.quality.occupancy.within', {
				values: { checked: numbers.format(judged.checked) }
			});
		return $_('liveOperations.quality.occupancy.outside', {
			values: { minutes: numbers.format(judged.minutes), checked: numbers.format(judged.checked) }
		});
	}
</script>

<!-- Zone names come from the zone profile and are text; every judgement is a word as well as a colour. -->
<section
	class="flex flex-col gap-3 rounded-xl border bg-card p-4"
	aria-labelledby="{id}-title"
	data-testid="data-quality"
>
	<div>
		<h2 id="{id}-title" class="flex items-center gap-2 text-base font-semibold">
			<ShieldCheck class="size-4" aria-hidden="true" />
			{heading}
		</h2>
		<p class="text-xs text-muted-foreground">{description}</p>
	</div>
	{#if !loaded}
		<p class="text-sm text-muted-foreground" aria-busy="true">
			{$_('liveOperations.quality.loading')}
		</p>
	{:else if !many && shown.length === 1 && (health[shown[0].name] ?? []).length === 0}
		<p class="text-sm text-muted-foreground" data-testid="data-quality-empty">
			{health[shown[0].name] === null
				? $_('liveOperations.quality.failed')
				: $_('liveOperations.quality.none')}
		</p>
	{:else}
		<!-- A list rather than a table: the three checks wrap in a narrow column instead of scrolling it. -->
		<ul class="flex flex-col gap-2">
			{#each shown as zone (zone.name)}
				{@const found = health[zone.name]}
				{@const rows = latestBins(found ?? [], bins)}
				{#if rows.length === 0}
					<li
						data-testid="quality-row"
						data-zone={zone.name}
						class="flex flex-wrap items-baseline gap-x-2 rounded-lg border px-3 py-2 text-sm"
					>
						{#if many}
							<span data-testid="quality-zone" class="font-medium">{zone.label ?? zone.name}</span>
						{/if}
						<span class="text-xs text-muted-foreground">
							{found === null
								? $_('liveOperations.quality.failed')
								: $_('liveOperations.quality.noBin')}
						</span>
					</li>
				{:else}
					{#each rows as bin (bin.startUtc)}
						{@const residual = residualOf(bin)}
						{@const completion = completionOf(bin)}
						{@const occupancy = occupancyOf(bin)}
						<li
							data-testid="quality-row"
							data-zone={zone.name}
							data-status={bin.status}
							class="flex flex-wrap items-center gap-x-6 gap-y-2 rounded-lg border px-3 py-2"
						>
							<div class="flex min-w-44 flex-wrap items-baseline gap-x-2 text-sm">
								{#if many}
									<span data-testid="quality-zone" class="font-medium"
										>{zone.label ?? zone.name}</span
									>
								{/if}
								<span class="text-xs text-muted-foreground"
									>{$_('liveOperations.quality.columns.bin')}
									<time datetime={bin.startUtc} class="tabular-nums">{time(bin.startUtc)}</time> · {statusLabel(
										bin.status
									)}</span
								>
							</div>
							<dl class="flex flex-wrap gap-x-5 gap-y-2">
								<div class="flex flex-col gap-1">
									<dt
										class="text-xs text-muted-foreground"
										title={$_('liveOperations.quality.residual.hint')}
									>
										{$_('liveOperations.quality.columns.residual')}
									</dt>
									<dd data-testid="quality-residual" data-tone={residual.tone}>
										<StatusBadge tone={residual.tone} label={residualLabel(bin)} />
									</dd>
								</div>
								<div class="flex flex-col gap-1">
									<dt class="text-xs text-muted-foreground">
										{$_('liveOperations.quality.columns.completion')}
									</dt>
									<dd data-testid="quality-completion" data-tone={completion.tone}>
										<StatusBadge tone={completion.tone} label={completionLabel(bin)} />
									</dd>
								</div>
								<div class="flex flex-col gap-1">
									<dt class="text-xs text-muted-foreground">
										{$_('liveOperations.quality.columns.occupancy')}
									</dt>
									<dd data-testid="quality-occupancy" data-tone={occupancy.tone}>
										<StatusBadge tone={occupancy.tone} label={occupancyLabel(bin)} />
									</dd>
								</div>
							</dl>
						</li>
					{/each}
				{/if}
			{/each}
		</ul>
	{/if}
</section>
