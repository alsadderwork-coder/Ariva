<script lang="ts">
	import { CalendarClock, Pencil, RotateCcw } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import * as plans from '$lib/core/advertising-plans';
	import type { Playlist, Schedule, ScheduleRequest } from '$lib/core/advertising-plans';
	import ScheduleForm from './ScheduleForm.svelte';

	/**
	 * The site's advertising schedules (ARV-143a): which playlist plays on which displays, on which days and hours of the
	 * site's local time. A schedule whose playlist is retired applies nowhere until the playlist is restored, and says so.
	 * Holders of Advertising.Manage create, change, retire and restore them; the server decides every change.
	 */
	interface Props {
		siteCode: string;
		schedules: Schedule[];
		playlists: Playlist[];
		zoneNames: string[];
		displayCodes: string[];
		canManage: boolean;
		onChanged: () => Promise<void>;
		onRefused: () => void;
	}

	let {
		siteCode,
		schedules,
		playlists,
		zoneNames,
		displayCodes,
		canManage,
		onChanged,
		onRefused
	}: Props = $props();

	let editing = $state<Schedule | null>(null);
	let formKey = $state(0);
	let busy = $state(false);

	const separator = $derived($_('advertisingPlans.listSeparator'));

	function failed(reason: string): void {
		toast.error($_('advertisingPlans.messages.failed', { values: { reason } }));
	}

	function startEdit(schedule: Schedule): void {
		editing = schedule;
		formKey++;
	}

	function stopEdit(): void {
		editing = null;
		formKey++;
	}

	function target(schedule: Schedule): string {
		if (schedule.scope === 'Site') return $_('advertisingPlans.schedules.target.Site');
		return $_(`advertisingPlans.schedules.target.${schedule.scope}`, {
			values: { targets: schedule.targets.join(separator) }
		});
	}

	function dayList(schedule: Schedule): string {
		if (schedule.days.length === plans.weekDays.length)
			return $_('advertisingPlans.schedules.everyDay');
		return schedule.days.map((d) => $_(`advertisingPlans.days.${d}`)).join(separator);
	}

	function windowOf(schedule: Schedule): string {
		const values = { opens: schedule.opens, closes: schedule.closes };
		return schedule.closes !== '24:00' && schedule.closes <= schedule.opens
			? $_('advertisingPlans.schedules.windowNextDay', { values })
			: $_('advertisingPlans.schedules.window', { values });
	}

	async function submit(request: ScheduleRequest): Promise<string[]> {
		const changing = editing;
		const result = changing
			? await plans.updateSchedule(siteCode, changing.id, request)
			: await plans.createSchedule(siteCode, request);
		if (result.hasErrors || !result.data) {
			if (result.status === 403) {
				onRefused();
				return [];
			}
			return result.errorMessages;
		}
		toast.success(
			$_(
				changing
					? 'advertisingPlans.messages.scheduleSaved'
					: 'advertisingPlans.messages.scheduleCreated'
			)
		);
		stopEdit();
		await onChanged();
		return [];
	}

	async function setStatus(schedule: Schedule, retire: boolean): Promise<void> {
		if (busy) return;
		busy = true;
		const result = retire
			? await plans.retireSchedule(siteCode, schedule.id)
			: await plans.restoreSchedule(siteCode, schedule.id);
		busy = false;
		if (result.hasErrors) {
			if (result.status === 403) return onRefused();
			failed(result.errorMessages.join(' '));
			return;
		}
		if (editing?.id === schedule.id) stopEdit();
		toast.success(
			$_(
				retire
					? 'advertisingPlans.messages.scheduleRetired'
					: 'advertisingPlans.messages.scheduleRestored'
			)
		);
		await onChanged();
	}

	const buttonClass =
		'inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60';
</script>

<section
	class="rounded-xl border bg-card"
	aria-labelledby="advertising-schedules-title"
	data-testid="schedules"
>
	<div class="border-b px-4 py-3">
		<h2 id="advertising-schedules-title" class="flex items-center gap-2 text-base font-semibold">
			<CalendarClock class="size-4" aria-hidden="true" />
			{$_('advertisingPlans.schedules.title')}
		</h2>
		<p class="text-xs text-muted-foreground">{$_('advertisingPlans.schedules.description')}</p>
	</div>
	<div class="flex flex-col gap-4 px-4 py-3 text-sm">
		{#if schedules.length === 0}
			<p class="text-muted-foreground" data-testid="schedules-empty">
				{$_('advertisingPlans.schedules.none')}
			</p>
		{:else}
			<div class="overflow-x-auto rounded-lg border">
				<table class="w-full text-sm" data-testid="schedules-table">
					<thead class="bg-surface-2 text-xs font-semibold tracking-wide text-tertiary uppercase">
						<tr>
							<th class="px-4 py-2 text-start"
								>{$_('advertisingPlans.schedules.columns.playlist')}</th
							>
							<th class="px-4 py-2 text-start">{$_('advertisingPlans.schedules.columns.target')}</th
							>
							<th class="px-4 py-2 text-start">{$_('advertisingPlans.schedules.columns.days')}</th>
							<th class="px-4 py-2 text-start">{$_('advertisingPlans.schedules.columns.window')}</th
							>
							<th class="px-4 py-2 text-start">{$_('advertisingPlans.schedules.columns.status')}</th
							>
							<th class="px-4 py-2 text-end"
								><span class="sr-only">{$_('advertisingPlans.schedules.columns.actions')}</span></th
							>
						</tr>
					</thead>
					<tbody>
						{#each schedules as schedule (schedule.id)}
							<tr
								class="border-t align-top odd:bg-surface even:bg-surface-2/50"
								data-testid="schedule-row"
								data-id={schedule.id}
								data-status={schedule.status}
								data-applied={schedule.status === 'Active' && schedule.playlistActive}
							>
								<td class="px-4 py-3">
									<div class="flex min-w-0 flex-col gap-1">
										<span class="font-medium break-words" data-testid="schedule-row-playlist"
											>{schedule.playlistName}</span
										>
										{#if !schedule.playlistActive}
											<span
												class="text-xs text-status-warning-foreground"
												data-testid="schedule-not-applied"
												>{$_('advertisingPlans.schedules.notApplied')}</span
											>
										{/if}
									</div>
								</td>
								<td class="px-4 py-3 text-xs break-words" data-testid="schedule-row-target"
									>{target(schedule)}</td
								>
								<td class="px-4 py-3 text-xs" data-testid="schedule-row-days"
									>{dayList(schedule)}</td
								>
								<td
									class="px-4 py-3 text-xs whitespace-nowrap tabular-nums"
									data-testid="schedule-row-window">{windowOf(schedule)}</td
								>
								<td class="px-4 py-3">
									<StatusBadge
										tone={schedule.status === 'Active' ? 'success' : 'neutral'}
										label={$_(`advertisingPlans.schedules.status.${schedule.status}`)}
									/>
								</td>
								<td class="px-4 py-3">
									{#if canManage}
										<span class="flex flex-wrap justify-end gap-2">
											{#if schedule.status === 'Active'}
												<button
													type="button"
													data-testid="schedule-edit"
													disabled={busy}
													onclick={() => startEdit(schedule)}
													class={buttonClass}
												>
													<Pencil class="size-4" aria-hidden="true" />
													{$_('advertisingPlans.schedules.actions.edit')}
												</button>
												<button
													type="button"
													data-testid="schedule-retire"
													disabled={busy}
													onclick={() => setStatus(schedule, true)}
													class={buttonClass}
												>
													{$_('advertisingPlans.schedules.actions.retire')}
												</button>
											{:else}
												<button
													type="button"
													data-testid="schedule-restore"
													disabled={busy}
													onclick={() => setStatus(schedule, false)}
													class={buttonClass}
												>
													<RotateCcw class="size-4" aria-hidden="true" />
													{$_('advertisingPlans.schedules.actions.restore')}
												</button>
											{/if}
										</span>
									{/if}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}

		{#if canManage}
			{#key formKey}
				<ScheduleForm
					{editing}
					{playlists}
					{zoneNames}
					{displayCodes}
					onSubmit={submit}
					onCancel={stopEdit}
				/>
			{/key}
		{/if}
	</div>
</section>
