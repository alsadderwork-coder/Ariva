<script lang="ts">
	import {
		History,
		KeyRound,
		LockOpen,
		ScrollText,
		ShieldOff,
		UserCheck,
		UserX
	} from '@lucide/svelte';
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import type { Site } from '$lib/core/topology';
	import * as users from '$lib/core/users';
	import type { Role, User } from '$lib/core/users';
	import ConfirmAction from './ConfirmAction.svelte';
	import UserStatus from './UserStatus.svelte';

	interface Props {
		user: User;
		roles: Role[];
		/** The caller's highest rank: a role above it cannot be granted or revoked (the server refuses it too). */
		ownRank: number;
		/** True when the panel shows the caller's own account: roles, sites, resets and status are another administrator's. */
		self: boolean;
		canEdit: boolean;
		/** The sites the caller may grant (its own); "every site" only for a caller with every site. */
		sites: Site[];
		callerAllSites: boolean;
		canAudit: boolean;
		onChanged: (user: User) => void;
		onTemporaryPassword: (userName: string, password: string) => void;
		onAudit: (filter: { targetId?: string; actorId?: string; label: string }) => void;
	}

	let {
		user,
		roles,
		ownRank,
		self,
		canEdit,
		sites,
		callerAllSites,
		canAudit,
		onChanged,
		onTemporaryPassword,
		onAudit
	}: Props = $props();

	const start = untrack(() => user);
	let displayName = $state(start.displayName ?? '');
	let email = $state(start.email ?? '');
	let allSites = $state(start.allSites);
	let siteCodes = $state<string[]>([...start.sites]);
	let problems = $state<string[]>([]);
	let busy = $state(false);

	const locked = $derived(self || !canEdit);

	/** Runs one change, then reads the account again so the panel shows what the server holds. */
	async function change(
		run: () => Promise<{ hasErrors: boolean; errorMessages: string[] }>
	): Promise<boolean> {
		if (busy) return false;
		busy = true;
		problems = [];
		const result = await run();
		if (result.hasErrors) problems = result.errorMessages;
		const fresh = await users.get(user.id);
		if (fresh.data) onChanged(fresh.data);
		busy = false;
		return !result.hasErrors;
	}

	async function saveProfile(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		await change(() =>
			users.update(user.id, {
				displayName: displayName.trim() || null,
				email: email.trim() || null
			})
		);
	}

	async function toggleRole(code: string, on: boolean, input: HTMLInputElement): Promise<void> {
		const done = await change(() =>
			on ? users.grant(user.id, code) : users.revoke(user.id, code)
		);
		// A refused change leaves the box as the server has it.
		if (!done) input.checked = !on;
	}

	function toggleSite(code: string, on: boolean): void {
		siteCodes = on ? [...siteCodes, code] : siteCodes.filter((c) => c !== code);
	}

	async function saveSites(): Promise<void> {
		await change(() => users.setSites(user.id, allSites, siteCodes));
	}

	async function resetPassword(): Promise<void> {
		let password = '';
		await change(async () => {
			const result = await users.resetPassword(user.id);
			password = result.data?.temporaryPassword ?? '';
			return result;
		});
		if (password) onTemporaryPassword(user.userName, password);
	}
</script>

<section
	class="flex flex-col gap-4 rounded-xl border bg-card p-4"
	data-testid="user-panel"
	data-user={user.userName}
	aria-labelledby="user-panel-title"
>
	<div class="flex flex-wrap items-start justify-between gap-3">
		<div class="flex min-w-0 flex-col gap-1">
			<h2 id="user-panel-title" class="text-base font-semibold break-all">
				<span class="font-mono">{user.userName}</span>
			</h2>
			<UserStatus {user} />
			<p class="text-xs text-muted-foreground tabular-nums">
				{$_('users.lastSignIn', {
					values: { when: user.lastLoginOn ? users.utcText(user.lastLoginOn) : $_('users.never') }
				})}
			</p>
		</div>
		{#if canAudit}
			<div class="flex flex-wrap gap-1.5">
				<button
					type="button"
					data-testid="audit-about"
					onclick={() =>
						onAudit({
							targetId: user.id,
							label: $_('users.audit.about', { values: { user: user.userName } })
						})}
					class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					<History class="size-4" aria-hidden="true" />
					{$_('users.actions.auditAbout')}
				</button>
				<button
					type="button"
					data-testid="audit-by"
					onclick={() =>
						onAudit({
							actorId: user.id,
							label: $_('users.audit.by', { values: { user: user.userName } })
						})}
					class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
				>
					<ScrollText class="size-4" aria-hidden="true" />
					{$_('users.actions.auditBy')}
				</button>
			</div>
		{/if}
	</div>

	{#if self}
		<p
			class="rounded-lg border border-status-info-border bg-status-info px-3 py-2 text-sm text-status-info-foreground"
			data-testid="own-account"
		>
			{$_('users.ownAccount')}
		</p>
	{/if}

	<form class="flex flex-col gap-3" onsubmit={saveProfile} aria-label={$_('users.profile')}>
		<div class="grid gap-3 sm:grid-cols-2">
			<FormField
				id="panel-display-name"
				label={$_('users.fields.displayName')}
				bind:value={displayName}
				maxlength={users.limits.displayName}
				readonly={locked}
			/>
			<FormField
				id="panel-email"
				label={$_('users.fields.email')}
				bind:value={email}
				maxlength={users.limits.email}
				readonly={locked}
			/>
		</div>
		{#if !locked}
			<button
				type="submit"
				data-testid="save-profile"
				disabled={busy}
				class="inline-flex h-9 w-fit items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
			>
				{$_('users.actions.saveProfile')}
			</button>
		{/if}
	</form>

	<fieldset class="flex flex-col gap-1.5" data-testid="user-roles">
		<legend class="text-xs font-medium">{$_('users.fields.roles')}</legend>
		<div class="flex flex-wrap gap-x-4 gap-y-1.5">
			{#each roles as role (role.code)}
				<label class="inline-flex items-center gap-1.5 text-sm">
					<input
						type="checkbox"
						data-role={role.code}
						checked={user.roles.includes(role.code)}
						disabled={locked || busy || role.rank > ownRank}
						onchange={(event) =>
							toggleRole(role.code, event.currentTarget.checked, event.currentTarget)}
					/>
					<span>{$_(`users.roles.${role.code}`)}</span>
				</label>
			{/each}
		</div>
		{#if !self && canEdit}
			<p class="text-xs text-muted-foreground">{$_('users.rolesHint')}</p>
		{/if}
	</fieldset>

	<fieldset class="flex flex-col gap-2" data-testid="user-sites">
		<legend class="text-xs font-medium">{$_('users.fields.sites')}</legend>
		<div class="flex flex-wrap gap-x-4 gap-y-1.5">
			<label class="inline-flex items-center gap-1.5 text-sm">
				<input
					type="radio"
					name="site-access"
					value="all"
					checked={allSites}
					disabled={locked || !callerAllSites}
					onchange={() => (allSites = true)}
				/>
				<span>{$_('users.allSites')}</span>
			</label>
			<label class="inline-flex items-center gap-1.5 text-sm">
				<input
					type="radio"
					name="site-access"
					value="some"
					checked={!allSites}
					disabled={locked}
					onchange={() => (allSites = false)}
				/>
				<span>{$_('users.someSites')}</span>
			</label>
		</div>
		{#if !allSites}
			<div class="flex flex-wrap gap-x-4 gap-y-1.5 ps-5">
				{#each sites as site (site.code)}
					<label class="inline-flex items-center gap-1.5 text-sm">
						<input
							type="checkbox"
							data-site={site.code}
							checked={siteCodes.includes(site.code)}
							disabled={locked}
							onchange={(event) => toggleSite(site.code, event.currentTarget.checked)}
						/>
						<span class="font-mono text-xs">{site.code}</span>
					</label>
				{/each}
				{#each siteCodes.filter((c) => !sites.some((s) => s.code === c)) as code (code)}
					<!-- A site the caller cannot grant stays as it is; it is shown, not offered. -->
					<span class="font-mono text-xs text-muted-foreground">{code}</span>
				{/each}
			</div>
		{/if}
		{#if !locked}
			<button
				type="button"
				data-testid="save-sites"
				disabled={busy}
				onclick={saveSites}
				class="inline-flex h-9 w-fit items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
			>
				{$_('users.actions.saveSites')}
			</button>
			{#if !callerAllSites}
				<p class="text-xs text-muted-foreground">{$_('users.sitesHint')}</p>
			{/if}
		{/if}
	</fieldset>

	{#if canEdit}
		<div class="flex flex-wrap gap-1.5" data-testid="user-actions">
			<ConfirmAction
				label={$_('users.actions.resetPassword')}
				confirmLabel={$_('users.actions.confirmResetPassword')}
				icon={KeyRound}
				testId="reset-password"
				disabled={self || busy}
				onConfirm={resetPassword}
			/>
			<ConfirmAction
				label={$_('users.actions.resetTotp')}
				confirmLabel={$_('users.actions.confirmResetTotp')}
				icon={ShieldOff}
				testId="reset-totp"
				disabled={self || busy}
				onConfirm={() => change(() => users.resetTotp(user.id)).then(() => undefined)}
			/>
			{#if user.isLocked}
				<button
					type="button"
					data-testid="unlock-user"
					disabled={self || busy}
					onclick={() => change(() => users.unlock(user.id))}
					class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-50"
				>
					<LockOpen class="size-4" aria-hidden="true" />
					{$_('users.actions.unlock')}
				</button>
			{/if}
			{#if user.isDisabled}
				<button
					type="button"
					data-testid="enable-user"
					disabled={self || busy}
					onclick={() => change(() => users.enable(user.id))}
					class="inline-flex h-9 items-center gap-1.5 rounded-md border bg-card px-2.5 text-sm hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-50"
				>
					<UserCheck class="size-4" aria-hidden="true" />
					{$_('users.actions.enable')}
				</button>
			{:else}
				<ConfirmAction
					label={$_('users.actions.disable')}
					confirmLabel={$_('users.actions.confirmDisable')}
					icon={UserX}
					testId="disable-user"
					disabled={self || busy}
					onConfirm={() => change(() => users.disable(user.id)).then(() => undefined)}
				/>
			{/if}
		</div>
	{/if}

	{#if problems.length}
		<ul
			class="list-disc ps-5 text-sm text-status-danger-foreground"
			data-testid="panel-problems"
			role="alert"
		>
			{#each problems as problem, index (index)}<li>{problem}</li>{/each}
		</ul>
	{/if}
</section>
