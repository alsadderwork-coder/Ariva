<script lang="ts">
	import { Download, FileChartColumn, Pencil, Plus } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import DailyReportView from '$lib/components/pages/reports/DailyReportView.svelte';
	import ScheduleForm from '$lib/components/pages/reports/ScheduleForm.svelte';
	import ConfirmButton from '$lib/components/shared/ConfirmButton.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as reports from '$lib/core/reports';
	import type {
		CsvSection,
		DailyReport,
		ReportRecipient,
		ReportSchedule,
		ReportScheduleRequest
	} from '$lib/core/reports';
	import * as topology from '$lib/core/topology';
	import type { Site } from '$lib/core/topology';

	let siteList = $state<Site[]>([]);
	let siteCode = $state('');
	/** The site's time zone, learnt from its first report; until then the browser's. */
	let timeZone = $state<string | null>(null);
	let date = $state(reports.localDate(null, -1));
	let report = $state<DailyReport | null>(null);
	let problem = $state('');
	let loading = $state(false);
	let exporting = $state<CsvSection | null>(null);
	let tab = $state<'report' | 'schedules'>('report');
	let scheduleList = $state<ReportSchedule[]>([]);
	let eligible = $state<ReportRecipient[]>([]);
	let form = $state<{ key: number; editing: ReportSchedule | null } | null>(null);
	let formKey = 0;
	let destroyed = false;
	let ready = $state(false);
	/** The newest request wins: a slow answer for an earlier site or date is dropped. */
	let ticket = 0;
	let scheduleTicket = 0;

	const canView = $derived(auth.can('Report.View'));
	const canSchedules = $derived(auth.can('ReportSchedule.Search'));
	const canCreate = $derived(auth.can('ReportSchedule.Create'));
	const canEdit = $derived(auth.can('ReportSchedule.Edit'));
	const canDelete = $derived(auth.can('ReportSchedule.Delete'));
	const today = $derived(reports.localDate(timeZone));
	const earliest = $derived(reports.localDate(timeZone, -reports.maxDaysBack));
	const isArabic = $derived($locale?.startsWith('ar') ?? false);
	const utc = $derived(
		new Intl.DateTimeFormat(isArabic ? 'ar-u-nu-latn' : 'en-GB', {
			dateStyle: 'medium',
			timeStyle: 'short',
			timeZone: 'UTC'
		})
	);

	async function loadReport(): Promise<void> {
		if (!siteCode || !/^\d{4}-\d{2}-\d{2}$/.test(date)) return;
		const mine = ++ticket;
		loading = true;
		const result = await reports.daily(siteCode, date);
		if (destroyed || mine !== ticket) return;
		loading = false;
		if (result.hasErrors || !result.data) {
			report = null;
			problem = result.errorMessages.join(' ') || $_('reports.messages.unavailable');
			return;
		}
		problem = '';
		report = result.data;
		if (timeZone !== result.data.timeZoneId) {
			const first = timeZone === null;
			timeZone = result.data.timeZoneId;
			// The first answer tells the site's zone: "yesterday" is the site's yesterday, not the browser's.
			const siteYesterday = reports.localDate(timeZone, -1);
			if (first && date !== siteYesterday) {
				date = siteYesterday;
				await loadReport();
			}
		}
	}

	async function loadSchedules(): Promise<void> {
		if (!siteCode || !canSchedules) return;
		const mine = ++scheduleTicket;
		const [found, people] = await Promise.all([
			reports.schedules(siteCode),
			canCreate || canEdit ? reports.recipients(siteCode) : Promise.resolve(null)
		]);
		if (destroyed || mine !== scheduleTicket) return;
		if (found.hasErrors) failed(found.errorMessages.join(' '));
		scheduleList = found.data ?? [];
		eligible = people?.data ?? [];
	}

	async function changeSite(): Promise<void> {
		report = null;
		form = null;
		timeZone = null;
		date = reports.localDate(null, -1);
		await Promise.all([loadReport(), loadSchedules()]);
	}

	function failed(reason: string): void {
		toast.error($_('reports.messages.failed', { values: { reason } }));
	}

	async function exportCsv(section: CsvSection): Promise<void> {
		if (!report) return;
		exporting = section;
		const result = await reports.csv(report.siteCode, report.date, section);
		exporting = null;
		if (result.hasErrors || !(result.data instanceof Blob))
			return failed(result.errorMessages.join(' '));
		reports.save(result.data, reports.csvName(report.siteCode, report.date, section));
	}

	async function submit(request: ReportScheduleRequest): Promise<string[]> {
		const editing = form?.editing;
		const result = editing
			? await reports.updateSchedule(editing.id, request)
			: await reports.createSchedule(request);
		if (result.hasErrors || !result.data) return result.errorMessages;
		toast.success($_('reports.schedules.saved', { values: { name: result.data.name } }));
		form = null;
		await loadSchedules();
		return [];
	}

	async function remove(schedule: ReportSchedule): Promise<void> {
		const result = await reports.removeSchedule(schedule.id);
		if (result.hasErrors) return failed(result.errorMessages.join(' '));
		toast.success($_('reports.schedules.deleted', { values: { name: schedule.name } }));
		await loadSchedules();
	}

	function show(next: 'report' | 'schedules'): void {
		tab = next;
		form = null;
	}

	onMount(async () => {
		// Without the report permission the screen says so and asks the server for nothing.
		if (!canView) return;
		const result = await topology.sites();
		if (destroyed) return;
		siteList = result.data ?? [];
		siteCode = siteList.find((s) => s.code === 'DMO')?.code ?? siteList[0]?.code ?? '';
		ready = true;
		if (siteCode) await Promise.all([loadReport(), loadSchedules()]);
	});

	onDestroy(() => {
		destroyed = true;
	});
</script>

<svelte:head>
	<title>{$_('reports.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader
	icon={FileChartColumn}
	title={$_('reports.title')}
	description={$_('reports.description')}
>
	{#snippet actions()}
		{#if canView}
			<label for="reports-site" class="sr-only">{$_('topology.site')}</label>
			<select
				id="reports-site"
				data-testid="reports-site"
				bind:value={siteCode}
				onchange={changeSite}
				class="h-10 rounded-lg border border-input bg-card px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				{#each siteList as site (site.code)}
					<option value={site.code}
						>{site.name && site.name !== site.code
							? `${site.code}, ${site.name}`
							: site.code}</option
					>
				{/each}
			</select>
		{/if}
	{/snippet}
</SimplePageHeader>

{#if !canView}
	<section class="rounded-xl border bg-card p-6" data-testid="no-access">
		<h2 class="text-base font-semibold">{$_('access.title')}</h2>
		<p class="mt-1 text-sm text-muted-foreground">{$_('access.description')}</p>
	</section>
{:else if ready && siteList.length === 0}
	<p class="text-sm text-muted-foreground">{$_('reports.noSites')}</p>
{:else}
	<div class="flex flex-col gap-4">
		{#if canSchedules}
			<div role="tablist" aria-label={$_('reports.tabs.label')} class="flex gap-1 border-b">
				{#each [['report', 'reports.tabs.report'], ['schedules', 'reports.tabs.schedules']] as [id, key] (id)}
					<button
						type="button"
						role="tab"
						id="tab-{id}"
						aria-selected={tab === id}
						aria-controls="panel-{id}"
						data-testid="tab-{id}"
						onclick={() => show(id as 'report' | 'schedules')}
						class="-mb-px border-b-2 px-3 py-2 text-sm font-medium focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none {tab ===
						id
							? 'border-primary text-foreground'
							: 'border-transparent text-muted-foreground hover:text-foreground'}"
					>
						{$_(key)}
					</button>
				{/each}
			</div>
		{/if}

		{#if tab === 'schedules' && canSchedules}
			<div
				role="tabpanel"
				id="panel-schedules"
				aria-labelledby="tab-schedules"
				class="flex flex-col gap-4"
			>
				<div class="flex flex-wrap items-center justify-between gap-2">
					<p class="max-w-prose text-sm text-muted-foreground">{$_('reports.schedules.intro')}</p>
					{#if canCreate}
						<button
							type="button"
							data-testid="add-schedule"
							disabled={!siteCode}
							onclick={() => (form = { key: ++formKey, editing: null })}
							class="inline-flex h-10 items-center gap-2 rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
						>
							<Plus class="size-4" aria-hidden="true" />
							{$_('reports.schedules.add')}
						</button>
					{/if}
				</div>

				{#if form}
					{#key form.key}
						<ScheduleForm
							{siteCode}
							editing={form.editing}
							{eligible}
							onSubmit={submit}
							onCancel={() => (form = null)}
						/>
					{/key}
				{/if}

				<section
					class="overflow-hidden rounded-xl border bg-card"
					aria-labelledby="schedules-title"
				>
					<div class="border-b px-4 py-3">
						<h2 id="schedules-title" class="text-base font-semibold">
							{$_('reports.schedules.listTitle')}
						</h2>
					</div>
					{#if scheduleList.length === 0}
						<p class="px-4 py-6 text-sm text-muted-foreground">{$_('reports.schedules.none')}</p>
					{:else}
						<div class="overflow-x-auto">
							<table class="w-full text-sm" data-testid="schedules-table">
								<thead class="bg-muted/50 text-xs text-muted-foreground">
									<tr>
										<th class="px-4 py-2 text-start font-medium"
											>{$_('reports.schedules.fields.name')}</th
										>
										<th class="px-4 py-2 text-start font-medium"
											>{$_('reports.schedules.fields.sendAt')}</th
										>
										<th class="px-4 py-2 text-start font-medium"
											>{$_('reports.schedules.recipientsColumn')}</th
										>
										<th class="px-4 py-2 text-start font-medium"
											>{$_('reports.schedules.lastSent')}</th
										>
										<th class="px-4 py-2 text-start font-medium">{$_('reports.columns.status')}</th>
										<th class="px-4 py-2 text-end font-medium"
											><span class="sr-only">{$_('reports.schedules.actions')}</span></th
										>
									</tr>
								</thead>
								<tbody>
									{#each scheduleList as schedule (schedule.id)}
										<tr class="border-t align-top" data-testid="schedule-row" data-id={schedule.id}>
											<td class="px-4 py-2" data-testid="schedule-name">{schedule.name}</td>
											<td class="px-4 py-2 tabular-nums">{schedule.sendAt}</td>
											<td class="px-4 py-2 text-xs">
												{schedule.recipients.map((r) => r.displayName || r.userName).join(', ')}
											</td>
											<td class="px-4 py-2 text-xs tabular-nums">
												{schedule.lastSentUtc
													? `${utc.format(new Date(schedule.lastSentUtc))} UTC`
													: $_('reports.schedules.notYet')}
											</td>
											<td class="px-4 py-2">
												<StatusBadge
													tone={schedule.enabled ? 'success' : 'neutral'}
													label={schedule.enabled
														? $_('reports.schedules.enabled')
														: $_('reports.schedules.disabled')}
												/>
											</td>
											<td class="px-4 py-2">
												<span class="flex flex-wrap justify-end gap-1.5">
													{#if canEdit}
														<button
															type="button"
															data-testid="edit-schedule"
															onclick={() => (form = { key: ++formKey, editing: schedule })}
															class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
														>
															<Pencil class="size-4" aria-hidden="true" />
															{$_('reports.schedules.edit')}
														</button>
													{/if}
													{#if canDelete}
														<ConfirmButton
															label={$_('reports.schedules.delete')}
															confirmLabel={$_('reports.schedules.confirmDelete')}
															testId="delete-schedule"
															onConfirm={() => remove(schedule)}
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
		{:else}
			<div
				role={canSchedules ? 'tabpanel' : undefined}
				id="panel-report"
				aria-labelledby={canSchedules ? 'tab-report' : undefined}
				class="flex flex-col gap-4"
			>
				<div class="flex flex-wrap items-end gap-3">
					<div class="flex flex-col gap-1">
						<label for="report-date" class="text-xs font-medium">{$_('reports.date')}</label>
						<input
							id="report-date"
							data-testid="report-date"
							type="date"
							min={earliest}
							max={today}
							bind:value={date}
							onchange={loadReport}
							class="h-10 rounded-lg border border-input bg-card px-3 text-sm tabular-nums focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						/>
					</div>
					<div class="flex gap-1.5">
						<button
							type="button"
							data-testid="report-yesterday"
							onclick={() => {
								date = reports.localDate(timeZone, -1);
								void loadReport();
							}}
							class="inline-flex h-10 items-center rounded-lg border bg-card px-3 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>{$_('reports.yesterday')}</button
						>
						<button
							type="button"
							data-testid="report-today"
							onclick={() => {
								date = today;
								void loadReport();
							}}
							class="inline-flex h-10 items-center rounded-lg border bg-card px-3 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
							>{$_('reports.today')}</button
						>
					</div>
					<div
						class="ms-auto flex flex-wrap items-center gap-1.5"
						role="group"
						aria-label={$_('reports.export.label')}
					>
						<span class="text-xs text-muted-foreground">{$_('reports.export.label')}</span>
						{#each reports.csvSections as section (section)}
							<button
								type="button"
								data-testid="export-{section}"
								disabled={!report || exporting !== null}
								onclick={() => exportCsv(section)}
								class="inline-flex h-10 items-center gap-1.5 rounded-lg border bg-card px-3 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
							>
								<Download class="size-4" aria-hidden="true" />
								{$_(`reports.export.${section}`)}
							</button>
						{/each}
					</div>
				</div>

				{#if date === today && report}
					<p
						class="rounded-md border border-status-info-border bg-status-info px-3 py-2 text-sm text-status-info-foreground"
						data-testid="report-today-note"
					>
						{$_('reports.todayNote')}
					</p>
				{/if}

				{#if problem}
					<p
						class="rounded-md border border-status-danger-border bg-status-danger px-3 py-2 text-sm text-status-danger-foreground"
						role="alert"
						data-testid="report-problem"
					>
						{problem}
					</p>
				{:else if report}
					<div aria-busy={loading}>
						<DailyReportView {report} />
					</div>
				{:else if loading}
					<p class="text-sm text-muted-foreground" aria-live="polite">{$_('reports.loading')}</p>
				{/if}
			</div>
		{/if}
	</div>
{/if}
