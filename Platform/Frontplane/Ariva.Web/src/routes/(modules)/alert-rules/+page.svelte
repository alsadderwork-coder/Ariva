<script lang="ts">
	import { BellRing, Copy, Pencil, Plus } from '@lucide/svelte';
	import { onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import RuleForm from '$lib/components/pages/alert-rules/RuleForm.svelte';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';
	import IllustrativeBanner from '$lib/components/shared/IllustrativeBanner.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import StatusBadge, { type StatusTone } from '$lib/components/shared/StatusBadge.svelte';
	import * as rules from '$lib/core/alertRules';
	import type { AlertRule, AlertRuleRequest } from '$lib/core/alertRules';
	import { auth } from '$lib/core/auth.svelte';
	import * as topology from '$lib/core/topology';
	import type { Site } from '$lib/core/topology';
	import * as zonesApi from '$lib/core/zones';

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	let list = $state<AlertRule[]>([]);
	let zoneNames = $state<string[]>([]);
	let loading = $state(true);
	/** The open form: a new rule (editing null) or a saved one; the key makes a fresh form for each start. */
	let form = $state<{ key: number; editing: AlertRule | null; initial: AlertRuleRequest } | null>(
		null
	);
	let formKey = 0;

	const severityTone: Record<string, StatusTone> = {
		Info: 'info',
		Warning: 'warning',
		Critical: 'danger'
	};
	const canCreate = $derived(auth.can('AlertRule.Create'));
	const canEdit = $derived(auth.can('AlertRule.Edit'));
	const canDelete = $derived(auth.can('AlertRule.Delete'));
	// The preview shows past queue values: it needs the live queues as well as creating rules.
	const canPreview = $derived(canCreate && auth.can('LiveQueue.View'));
	const isAdministrator = $derived(auth.user?.roles.includes('SystemAdministrator') ?? false);
	/** Owners the caller may give: their own operational roles, or every one for an administrator. */
	const roles = $derived(
		rules.operationalRoles.filter((r) => isAdministrator || (auth.user?.roles ?? []).includes(r))
	);

	function failed(reason: string): void {
		toast.error($_('alertRules.messages.failed', { values: { reason } }));
	}

	function known(group: string, value: string | null, values: readonly string[]): string {
		if (!value) return '';
		return values.includes(value) ? $_(`alertRules.${group}.${value}`) : value;
	}

	/** One line for the rule's condition, from typed fields only. */
	function condition(rule: AlertRule): string {
		const metric = known('metrics', rule.metric, rules.metrics);
		const unit = rules.unitOf(rule.metric);
		if (unit === 'condition')
			return $_('alertRules.summary.condition', {
				values: { metric, sustain: rule.sustainMinutes }
			});
		return $_('alertRules.summary.threshold', {
			values: {
				metric,
				comparator: known('comparatorSigns', rule.comparator, rules.comparators),
				threshold: rule.threshold ?? '',
				unit: $_(`alertRules.units.${unit}`),
				sustain: rule.sustainMinutes
			}
		});
	}

	async function loadSite(): Promise<void> {
		form = null;
		loading = true;
		const [found, profiles] = await Promise.all([
			rules.search(siteCode),
			zonesApi.history(siteCode)
		]);
		if (found.hasErrors) failed(found.errorMessages.join(' '));
		list = found.data?.data ?? [];
		const published = (profiles.data ?? []).find((p) => p.status === 'Published');
		const zones = published ? ((await zonesApi.get(published.id)).data?.zones ?? []) : [];
		zoneNames = zones
			.filter((z) => z.kind === 'Queue' || z.kind === 'Overflow')
			.map((z) => z.name)
			.sort((a, b) => a.localeCompare(b));
		loading = false;
	}

	function defaults(): AlertRuleRequest {
		return {
			siteCode,
			name: '',
			zones: [],
			metric: 'Nowcast',
			comparator: 'GreaterThan',
			threshold: 15,
			minQueueLength: null,
			clearThreshold: null,
			sustainMinutes: 3,
			clearAfterMinutes: 5,
			severity: 'Warning',
			ownerRole: roles[0] ?? null,
			escalateAfterMinutes: null,
			escalateToRole: null,
			escalationContact: null,
			notifyByEmail: false,
			enabled: true,
			leadMinutes: null
		};
	}

	function open(editing: AlertRule | null, initial: AlertRuleRequest): void {
		form = { key: ++formKey, editing, initial };
	}

	function duplicate(rule: AlertRule): void {
		const name = $_('alertRules.copyOf', { values: { name: rule.name } }).slice(
			0,
			rules.limits.nameLength
		);
		open(null, { ...rules.requestOf(rule), name });
	}

	async function saved(rule: AlertRule): Promise<void> {
		toast.success($_('alertRules.messages.saved', { values: { code: rule.code } }));
		form = null;
		await loadSite();
	}

	async function toggle(rule: AlertRule): Promise<void> {
		const result = await rules.update(rule.id, {
			...rules.requestOf(rule),
			enabled: !rule.enabled
		});
		if (result.hasErrors || !result.data) return failed(result.errorMessages.join(' '));
		list = list.map((r) => (r.id === rule.id ? result.data! : r));
	}

	async function remove(rule: AlertRule): Promise<void> {
		const result = await rules.remove(rule.id);
		if (result.hasErrors) return failed(result.errorMessages.join(' '));
		toast.success($_('alertRules.messages.deleted', { values: { code: rule.code } }));
		if (form?.editing?.id === rule.id) form = null;
		await loadSite();
	}

	onMount(async () => {
		const result = await topology.sites();
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		if (siteCode) await loadSite();
		else loading = false;
	});
</script>

<svelte:head>
	<title>{$_('alertRules.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={BellRing}
	title={$_('alertRules.title')}
	description={$_('alertRules.description')}
>
	{#snippet actions()}
		<label for="rules-site" class="sr-only">{$_('topology.site')}</label>
		<select
			id="rules-site"
			bind:value={siteCode}
			onchange={loadSite}
			class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{#each siteList as site (site.code)}
				<option value={site.code}
					>{site.name && site.name !== site.code ? `${site.code}, ${site.name}` : site.code}</option
				>
			{/each}
		</select>
		{#if canCreate}
			<button
				type="button"
				data-testid="add-rule"
				disabled={loading || !siteCode}
				onclick={() => open(null, defaults())}
				class="inline-flex h-10 items-center gap-2 rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
			>
				<Plus class="size-4" aria-hidden="true" />
				{$_('alertRules.actions.add')}
			</button>
		{/if}
	{/snippet}
</SimplePageHeader>

<IllustrativeBanner site={siteList.find((s) => s.code === siteCode)} />

<div class="flex flex-col gap-4">
	{#if form}
		{#key form.key}
			<RuleForm
				editing={form.editing}
				initial={form.initial}
				{zoneNames}
				{roles}
				allowNoOwner={isAdministrator}
				{canPreview}
				onSaved={saved}
				onCancel={() => (form = null)}
			/>
		{/key}
	{/if}

	<section class="overflow-hidden rounded-xl border bg-card" aria-labelledby="rules-title">
		<div class="border-b px-4 py-3">
			<h2 id="rules-title" class="text-base font-semibold">{$_('alertRules.listTitle')}</h2>
			<p class="text-xs text-muted-foreground">
				{$_('alertRules.count', { values: { count: list.length } })}
			</p>
		</div>
		{#if !loading && list.length === 0}
			<p class="px-4 py-6 text-sm text-muted-foreground">{$_('alertRules.none')}</p>
		{:else}
			<div class="overflow-x-auto">
				<table class="w-full text-sm" data-testid="rules-table">
					<thead class="bg-muted/50 text-xs text-muted-foreground">
						<tr>
							<th class="px-4 py-2 text-start font-medium">{$_('alertRules.columns.code')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('alertRules.columns.name')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('alertRules.columns.condition')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('alertRules.columns.zones')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('alertRules.columns.severity')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('alertRules.columns.owner')}</th>
							<th class="px-4 py-2 text-start font-medium">{$_('alertRules.columns.status')}</th>
							<th class="px-4 py-2 text-end font-medium"
								><span class="sr-only">{$_('alertRules.columns.actions')}</span></th
							>
						</tr>
					</thead>
					<tbody>
						{#each list as rule (rule.id)}
							<tr
								class="border-t align-top"
								data-testid="rule-row"
								data-code={rule.code}
								data-enabled={rule.enabled}
							>
								<td class="px-4 py-2 font-mono text-xs">{rule.code}</td>
								<td class="px-4 py-2">{rule.name}</td>
								<td class="px-4 py-2 text-xs">{condition(rule)}</td>
								<td class="max-w-56 px-4 py-2 text-xs break-words">{rule.zones.join(', ')}</td>
								<td class="px-4 py-2">
									<StatusBadge
										tone={severityTone[rule.severity] ?? 'neutral'}
										label={known('severities', rule.severity, rules.severities)}
									/>
								</td>
								<td class="px-4 py-2 text-xs"
									>{rule.ownerRole
										? known('roles', rule.ownerRole, rules.roleCodes)
										: $_('alertRules.form.noOwner')}</td
								>
								<td class="px-4 py-2">
									{#if canEdit}
										<button
											type="button"
											role="switch"
											aria-checked={rule.enabled}
											aria-label={$_('alertRules.actions.enable', { values: { code: rule.code } })}
											data-testid="toggle-rule"
											onclick={() => toggle(rule)}
											class="inline-flex h-7 items-center rounded-full border px-2.5 text-xs font-medium focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none {rule.enabled
												? 'border-status-success-border bg-status-success text-status-success-foreground'
												: 'bg-muted text-muted-foreground'}"
										>
											{rule.enabled ? $_('alertRules.enabled') : $_('alertRules.disabled')}
										</button>
										{#if rule.enabled && rules.notEvaluated.has(rule.metric)}
											<span data-testid="not-evaluated" class="ms-1.5"
												><StatusBadge tone="warning" label={$_('alertRules.notEvaluated')} /></span
											>
										{/if}
									{:else}
										<StatusBadge
											tone={rule.enabled ? 'success' : 'neutral'}
											label={rule.enabled ? $_('alertRules.enabled') : $_('alertRules.disabled')}
										/>
										{#if rule.enabled && rules.notEvaluated.has(rule.metric)}
											<span data-testid="not-evaluated" class="ms-1.5"
												><StatusBadge tone="warning" label={$_('alertRules.notEvaluated')} /></span
											>
										{/if}
									{/if}
								</td>
								<td class="px-4 py-2">
									<span class="flex flex-wrap justify-end gap-1.5">
										{#if canEdit}
											<button
												type="button"
												data-testid="edit-rule"
												onclick={() => open(rule, rules.requestOf(rule))}
												class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
											>
												<Pencil class="size-4" aria-hidden="true" />
												{$_('alertRules.actions.edit')}
											</button>
										{/if}
										{#if canCreate}
											<button
												type="button"
												data-testid="duplicate-rule"
												onclick={() => duplicate(rule)}
												class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
											>
												<Copy class="size-4" aria-hidden="true" />
												{$_('alertRules.actions.duplicate')}
											</button>
										{/if}
										{#if canDelete}
											<ConfirmButton
												label={$_('alertRules.actions.delete')}
												confirmLabel={$_('alertRules.actions.confirmDelete')}
												testId="delete-rule"
												onConfirm={() => remove(rule)}
											/>
										{/if}
									</span>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</section>
</div>
