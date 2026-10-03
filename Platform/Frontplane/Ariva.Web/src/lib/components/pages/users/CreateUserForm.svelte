<script lang="ts">
	import { untrack } from 'svelte';
	import { _ } from 'svelte-i18n';
	import FormField from '$lib/components/shared/FormField.svelte';
	import * as users from '$lib/core/users';
	import type { Site } from '$lib/core/topology';
	import type { CreateUserRequest, Role } from '$lib/core/users';

	interface Props {
		/** Every role with its rank. */
		roles: Role[];
		/** The caller's highest rank: roles above it are shown but cannot be chosen (the server refuses them too). */
		ownRank: number;
		/** The sites the caller may give (its own). */
		sites: Site[];
		/** True when the caller reaches every site: only then may it give every site, or none for now. */
		callerAllSites: boolean;
		onSubmit: (request: CreateUserRequest) => Promise<string[]>;
		onCancel: () => void;
	}

	let { roles, ownRank, sites, callerAllSites, onSubmit, onCancel }: Props = $props();

	let userName = $state('');
	let displayName = $state('');
	let email = $state('');
	let chosen = $state<string[]>([]);
	let allSites = $state(false);
	/** A caller with one site gives that one by default. */
	let siteCodes = $state<string[]>(untrack(() => (sites.length === 1 ? [sites[0].code] : [])));
	let problems = $state<string[]>([]);
	let busy = $state(false);

	function toggleSite(code: string, on: boolean): void {
		siteCodes = on ? [...siteCodes, code] : siteCodes.filter((c) => c !== code);
	}

	function toggle(code: string, on: boolean): void {
		chosen = on ? [...chosen, code] : chosen.filter((c) => c !== code);
	}

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (busy) return;
		const name = userName.trim();
		if (!users.userNamePattern.test(name)) {
			problems = [$_('users.form.userNameRule')];
			return;
		}
		if (!callerAllSites && !allSites && siteCodes.length === 0) {
			problems = [$_('users.form.sitesRequired')];
			return;
		}
		busy = true;
		problems = await onSubmit({
			userName: name,
			displayName: displayName.trim() || null,
			email: email.trim() || null,
			// In the server's order, so the list reads the same everywhere.
			roles: roles.map((r) => r.code).filter((c) => chosen.includes(c)),
			allSites,
			siteCodes: allSites ? [] : sites.map((s) => s.code).filter((c) => siteCodes.includes(c))
		});
		busy = false;
	}
</script>

<form
	class="flex flex-col gap-4 rounded-xl border bg-card p-4"
	data-testid="user-form"
	aria-labelledby="user-form-title"
	onsubmit={submit}
>
	<h2 id="user-form-title" class="text-base font-semibold">{$_('users.form.createTitle')}</h2>
	<div class="grid gap-3 sm:grid-cols-3">
		<FormField
			id="user-name"
			label={$_('users.fields.userName')}
			hint={$_('users.form.userNameRule')}
			bind:value={userName}
			required
			maxlength={users.limits.userName}
		/>
		<FormField
			id="user-display-name"
			label={$_('users.fields.displayName')}
			bind:value={displayName}
			maxlength={users.limits.displayName}
		/>
		<FormField
			id="user-email"
			label={$_('users.fields.email')}
			bind:value={email}
			maxlength={users.limits.email}
		/>
	</div>
	<fieldset class="flex flex-col gap-1.5">
		<legend class="text-xs font-medium">{$_('users.fields.roles')}</legend>
		<div class="flex flex-wrap gap-x-4 gap-y-1.5">
			{#each roles as role (role.code)}
				<label class="inline-flex items-center gap-1.5 text-sm">
					<input
						type="checkbox"
						checked={chosen.includes(role.code)}
						disabled={role.rank > ownRank}
						onchange={(event) => toggle(role.code, event.currentTarget.checked)}
					/>
					<span>{$_(`users.roles.${role.code}`)}</span>
				</label>
			{/each}
		</div>
	</fieldset>
	<fieldset class="flex flex-col gap-1.5" data-testid="new-user-sites">
		<legend class="text-xs font-medium">{$_('users.fields.sites')}</legend>
		<div class="flex flex-wrap gap-x-4 gap-y-1.5">
			<label class="inline-flex items-center gap-1.5 text-sm">
				<input
					type="radio"
					name="new-site-access"
					checked={allSites}
					disabled={!callerAllSites}
					onchange={() => (allSites = true)}
				/>
				<span>{$_('users.allSites')}</span>
			</label>
			<label class="inline-flex items-center gap-1.5 text-sm">
				<input
					type="radio"
					name="new-site-access"
					checked={!allSites}
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
							onchange={(event) => toggleSite(site.code, event.currentTarget.checked)}
						/>
						<span class="font-mono text-xs">{site.code}</span>
					</label>
				{/each}
			</div>
		{/if}
		<p class="text-xs text-muted-foreground">
			{callerAllSites ? $_('users.form.sitesOptional') : $_('users.sitesHint')}
		</p>
	</fieldset>

	{#if problems.length}
		<ul class="list-disc ps-5 text-sm text-status-danger-foreground" data-testid="user-problems">
			{#each problems as problem, index (index)}<li>{problem}</li>{/each}
		</ul>
	{/if}

	<div class="flex flex-wrap gap-2">
		<button
			type="submit"
			data-testid="save-user"
			disabled={busy}
			class="inline-flex h-9 items-center rounded-md bg-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
		>
			{$_('users.actions.create')}
		</button>
		<button
			type="button"
			onclick={onCancel}
			class="inline-flex h-9 items-center rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
		>
			{$_('users.actions.cancel')}
		</button>
	</div>
</form>
