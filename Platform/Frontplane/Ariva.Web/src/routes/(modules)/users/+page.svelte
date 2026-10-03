<script lang="ts">
	import { ChevronLeft, ChevronRight, Plus, UserCog } from '@lucide/svelte';
	import { onDestroy, onMount } from 'svelte';
	import { _ } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import CredentialReveal from '$lib/components/pages/devices/CredentialReveal.svelte';
	import AuditLog from '$lib/components/pages/users/AuditLog.svelte';
	import CreateUserForm from '$lib/components/pages/users/CreateUserForm.svelte';
	import UserPanel from '$lib/components/pages/users/UserPanel.svelte';
	import UserStatus from '$lib/components/pages/users/UserStatus.svelte';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
	import { auth } from '$lib/core/auth.svelte';
	import * as topology from '$lib/core/topology';
	import type { Site } from '$lib/core/topology';
	import * as users from '$lib/core/users';
	import type { CreateUserRequest, Role, User } from '$lib/core/users';

	/**
	 * Users and access (ARV-059): accounts, their roles and sites, resets, and the audit trail. What the page offers
	 * follows the caller's permissions and rank, and an administrator's own account is read only here; the server checks
	 * every change again (CWE-269) and is the only authority.
	 */
	const pageSize = 25;

	let tab = $state<'users' | 'audit'>('users');
	let roleList = $state<Role[]>([]);
	let siteList = $state<Site[]>([]);
	let text = $state('');
	let role = $state('');
	let status = $state<'' | 'active' | 'disabled'>('');
	let pageIndex = $state(1);
	let list = $state<User[]>([]);
	let total = $state(0);
	let loading = $state(true);
	let selected = $state<User | null>(null);
	let creating = $state(false);
	let formKey = $state(0);
	/** A temporary password, shown once after a creation or a reset, then dropped. */
	let revealed = $state<{ userName: string; password: string } | null>(null);
	let auditFilter = $state<{ targetId?: string; actorId?: string; label: string } | null>(null);
	let request = 0;

	const canSearch = $derived(auth.can('User.Search'));
	const canCreate = $derived(auth.can('User.Create'));
	const canEdit = $derived(auth.can('User.Edit'));
	const canAudit = $derived(auth.can('AuditEntry.Search'));
	const ownRank = $derived(users.highestRank(auth.user?.roles ?? [], roleList));
	const pages = $derived(Math.max(1, Math.ceil(total / pageSize)));

	async function load(): Promise<void> {
		const mine = ++request;
		loading = true;
		const result = await users.search({
			text,
			role,
			isDisabled: status === '' ? null : status === 'disabled',
			pageIndex,
			pageSize
		});
		if (mine !== request) return;
		if (result.hasErrors)
			toast.error(
				$_('users.messages.failed', { values: { reason: result.errorMessages.join(' ') } })
			);
		list = result.data?.data ?? [];
		total = result.data?.totalCount ?? 0;
		loading = false;
	}

	function search(event: SubmitEvent): void {
		event.preventDefault();
		pageIndex = 1;
		void load();
	}

	function go(next: number): void {
		pageIndex = Math.min(Math.max(1, next), pages);
		void load();
	}

	function open(user: User): void {
		creating = false;
		revealed = null;
		selected = user;
	}

	function changed(user: User): void {
		selected = user;
		list = list.map((u) => (u.id === user.id ? user : u));
	}

	async function create(requestBody: CreateUserRequest): Promise<string[]> {
		const result = await users.create(requestBody);
		if (result.hasErrors || !result.data) return result.errorMessages;
		creating = false;
		revealed = { userName: result.data.user.userName, password: result.data.temporaryPassword };
		selected = result.data.user;
		toast.success($_('users.messages.created', { values: { user: result.data.user.userName } }));
		// The list shows the new account, wherever it would fall among the others.
		text = result.data.user.userName;
		pageIndex = 1;
		await load();
		return [];
	}

	function showAudit(filter: { targetId?: string; actorId?: string; label: string }): void {
		auditFilter = filter;
		show('audit');
	}

	function dropRevealed(): void {
		revealed = null;
	}

	/** Switching tabs drops a shown temporary password: it is shown once, not again on the way back. */
	function show(next: 'users' | 'audit'): void {
		revealed = null;
		tab = next;
	}

	onMount(async () => {
		// The temporary password never outlives the page (registered first, so destroy always removes it).
		window.addEventListener('pagehide', dropRevealed);
		if (!canSearch) return;
		const [roles, sites] = await Promise.all([users.roles(), topology.sites()]);
		roleList = roles.data ?? [];
		siteList = sites.data ?? [];
		await load();
	});

	onDestroy(() => {
		revealed = null;
		if (typeof window !== 'undefined') window.removeEventListener('pagehide', dropRevealed);
	});
</script>

<svelte:head>
	<title>{$_('users.title')} · {$_('app.title')}</title>
</svelte:head>

<SimplePageHeader icon={UserCog} title={$_('users.title')} description={$_('users.description')}>
	{#snippet actions()}
		{#if canSearch && canCreate && tab === 'users'}
			<button
				type="button"
				data-testid="add-user"
				onclick={() => {
					revealed = null;
					selected = null;
					creating = true;
					formKey++;
				}}
				class="inline-flex h-10 items-center gap-2 rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
			>
				<Plus class="size-4" aria-hidden="true" />
				{$_('users.actions.add')}
			</button>
		{/if}
	{/snippet}
</SimplePageHeader>

{#if !canSearch}
	<section class="rounded-xl border bg-card p-6" data-testid="no-access">
		<h2 class="text-base font-semibold">{$_('access.title')}</h2>
		<p class="mt-1 text-sm text-muted-foreground">{$_('access.description')}</p>
	</section>
{:else}
	<div class="flex flex-col gap-4">
		<div role="tablist" aria-label={$_('users.tabs.label')} class="flex gap-1 border-b">
			<button
				type="button"
				role="tab"
				id="tab-users"
				aria-selected={tab === 'users'}
				aria-controls="panel-users"
				data-testid="tab-users"
				onclick={() => show('users')}
				class="-mb-px border-b-2 px-3 py-2 text-sm font-medium focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none {tab ===
				'users'
					? 'border-primary text-foreground'
					: 'border-transparent text-muted-foreground hover:text-foreground'}"
			>
				{$_('users.tabs.users')}
			</button>
			{#if canAudit}
				<button
					type="button"
					role="tab"
					id="tab-audit"
					aria-selected={tab === 'audit'}
					aria-controls="panel-audit"
					data-testid="tab-audit"
					onclick={() => show('audit')}
					class="-mb-px border-b-2 px-3 py-2 text-sm font-medium focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none {tab ===
					'audit'
						? 'border-primary text-foreground'
						: 'border-transparent text-muted-foreground hover:text-foreground'}"
				>
					{$_('users.tabs.audit')}
				</button>
			{/if}
		</div>

		{#if tab === 'audit' && canAudit}
			<div role="tabpanel" id="panel-audit" aria-labelledby="tab-audit">
				<AuditLog filter={auditFilter} onClearFilter={() => (auditFilter = null)} />
			</div>
		{:else}
			<div role="tabpanel" id="panel-users" aria-labelledby="tab-users" class="flex flex-col gap-4">
				{#if revealed}
					<!-- Shown once; the account changes it at its first sign-in. -->
					<CredentialReveal
						code={revealed.userName}
						credential={revealed.password}
						words="users.temporaryPassword"
						onDone={dropRevealed}
					/>
				{/if}

				{#if creating}
					{#key formKey}
						<CreateUserForm
							roles={roleList}
							{ownRank}
							sites={siteList}
							callerAllSites={auth.user?.allSites ?? false}
							onSubmit={create}
							onCancel={() => (creating = false)}
						/>
					{/key}
				{/if}

				<form
					class="flex flex-wrap items-end gap-2"
					onsubmit={search}
					aria-label={$_('users.filters')}
				>
					<label class="flex flex-col gap-1 text-xs font-medium">
						{$_('users.search')}
						<input
							type="search"
							bind:value={text}
							maxlength={64}
							data-testid="user-search"
							class="h-9 w-56 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						/>
					</label>
					<label class="flex flex-col gap-1 text-xs font-medium">
						{$_('users.fields.role')}
						<select
							bind:value={role}
							class="h-9 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						>
							<option value="">{$_('users.anyRole')}</option>
							{#each roleList as r (r.code)}<option value={r.code}
									>{$_(`users.roles.${r.code}`)}</option
								>{/each}
						</select>
					</label>
					<label class="flex flex-col gap-1 text-xs font-medium">
						{$_('users.fields.status')}
						<select
							bind:value={status}
							class="h-9 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
						>
							<option value="">{$_('users.anyStatus')}</option>
							<option value="active">{$_('users.status.active')}</option>
							<option value="disabled">{$_('users.status.disabled')}</option>
						</select>
					</label>
					<button
						type="submit"
						data-testid="search-users"
						class="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
					>
						{$_('users.actions.search')}
					</button>
				</form>

				<div class="grid gap-4 xl:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)]">
					<section class="overflow-hidden rounded-xl border bg-card" aria-labelledby="users-title">
						<div class="border-b px-4 py-3">
							<h2 id="users-title" class="text-base font-semibold">{$_('users.listTitle')}</h2>
						</div>
						{#if !loading && list.length === 0}
							<p class="px-4 py-6 text-sm text-muted-foreground">{$_('users.none')}</p>
						{:else}
							<div class="overflow-x-auto">
								<table class="w-full text-sm" data-testid="users-table" aria-busy={loading}>
									<thead class="bg-muted/50 text-xs text-muted-foreground">
										<tr>
											<th class="px-4 py-2 text-start font-medium">{$_('users.fields.userName')}</th
											>
											<th class="px-4 py-2 text-start font-medium">{$_('users.fields.roles')}</th>
											<th class="px-4 py-2 text-start font-medium">{$_('users.fields.sites')}</th>
											<th class="px-4 py-2 text-start font-medium">{$_('users.fields.status')}</th>
										</tr>
									</thead>
									<tbody>
										{#each list as user (user.id)}
											<tr
												class="border-t align-top {selected?.id === user.id ? 'bg-accent/60' : ''}"
												data-testid="user-row"
												data-user={user.userName}
											>
												<td class="px-4 py-2">
													<button
														type="button"
														data-testid="open-user"
														onclick={() => open(user)}
														aria-current={selected?.id === user.id ? 'true' : undefined}
														class="text-start font-mono text-xs break-all underline-offset-2 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
													>
														{user.userName}
													</button>
													{#if user.displayName}<div
															class="text-xs break-all text-muted-foreground"
														>
															{user.displayName}
														</div>{/if}
												</td>
												<td class="px-4 py-2 text-xs">
													{user.roles.map((r) => $_(`users.roles.${r}`)).join(', ')}
												</td>
												<td class="px-4 py-2 font-mono text-xs">
													{user.allSites ? $_('users.allSites') : user.sites.join(', ')}
												</td>
												<td class="px-4 py-2"><UserStatus {user} /></td>
											</tr>
										{/each}
									</tbody>
								</table>
							</div>
						{/if}
						<div class="flex items-center justify-between gap-2 border-t px-4 py-2 text-sm">
							<span class="text-muted-foreground tabular-nums" data-testid="users-count">
								{$_('users.count', { values: { total, page: pageIndex, pages } })}
							</span>
							<span class="flex gap-1.5">
								<button
									type="button"
									disabled={pageIndex <= 1 || loading}
									onclick={() => go(pageIndex - 1)}
									aria-label={$_('users.actions.previous')}
									class="inline-flex size-9 items-center justify-center rounded-md border bg-card hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-50"
								>
									<ChevronLeft class="size-4 rtl:rotate-180" aria-hidden="true" />
								</button>
								<button
									type="button"
									disabled={pageIndex >= pages || loading}
									onclick={() => go(pageIndex + 1)}
									aria-label={$_('users.actions.next')}
									class="inline-flex size-9 items-center justify-center rounded-md border bg-card hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-50"
								>
									<ChevronRight class="size-4 rtl:rotate-180" aria-hidden="true" />
								</button>
							</span>
						</div>
					</section>

					{#if selected}
						{#key selected.id}
							<UserPanel
								user={selected}
								roles={roleList}
								{ownRank}
								self={selected.userName.toLowerCase() === auth.user?.userName.toLowerCase()}
								{canEdit}
								sites={siteList}
								callerAllSites={auth.user?.allSites ?? false}
								{canAudit}
								onChanged={changed}
								onTemporaryPassword={(userName, password) => (revealed = { userName, password })}
								onAudit={showAudit}
							/>
						{/key}
					{:else}
						<p class="rounded-xl border border-dashed p-6 text-sm text-muted-foreground">
							{$_('users.pick')}
						</p>
					{/if}
				</div>
			</div>
		{/if}
	</div>
{/if}
