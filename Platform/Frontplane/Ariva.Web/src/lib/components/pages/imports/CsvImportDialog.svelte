<script lang="ts">
	import { CircleCheck, FileUp, TriangleAlert, X } from '@lucide/svelte';
	import { onDestroy } from 'svelte';
	import { _, locale } from 'svelte-i18n';
	import { toast } from 'svelte-sonner';
	import * as imports from '$lib/core/csvImports';
	import type {
		DevicePreview,
		Failure,
		FileProblem,
		ImportKind,
		ImportedCredential,
		MappingPreview
	} from '$lib/core/csvImports';
	import ImportedCredentials from './ImportedCredentials.svelte';
	import ImportPreviewRows from './ImportPreviewRows.svelte';
	import ImportRefusals from './ImportRefusals.svelte';

	interface Props {
		kind: ImportKind;
		siteCode: string;
		/** Called once the dialog has closed and dropped everything it held. */
		onClose: () => void;
		/** Called after a commit, with how many devices or mappings were created (never the credentials). */
		onCommitted: (count: number) => void | Promise<void>;
	}

	let { kind, siteCode, onClose, onCommitted }: Props = $props();

	let dialog = $state<HTMLDialogElement>();
	let input = $state<HTMLInputElement>();
	/** The chosen file: the preview and the commit send this same File object. */
	let file = $state.raw<File | null>(null);
	/** The name of the file last chosen, refused or not, shown beside the choose button. */
	let chosenName = $state('');
	let problem = $state<FileProblem | null>(null);
	let preview = $state.raw<DevicePreview | MappingPreview | null>(null);
	let failure = $state.raw<Failure | null>(null);
	let busy = $state<'preview' | 'commit' | null>(null);
	/** A device commit's new credentials: held here only while the dialog is open, dropped when it closes. */
	let credentials = $state.raw<ImportedCredential[] | null>(null);
	let notes = $state.raw<string[]>([]);
	let confirmed = $state(false);
	/** Closes asked for while new credentials were not yet confirmed as copied. */
	let prompts = $state(0);
	let closed = false;

	const words = $derived(`csvImport.${kind}`);
	const header = $derived(imports.columns[kind]);
	const devices = $derived(preview && 'devices' in preview ? preview.devices : []);
	const mappings = $derived(preview && 'mappings' in preview ? preview.mappings : []);
	const passing = $derived(kind === 'devices' ? devices.length : mappings.length);
	const ready = $derived(
		!!preview && preview.errorCount === 0 && !!preview.previewToken && passing > 0
	);
	/** When the preview's token expires, in the reader's language (the clock of this device). */
	const until = $derived(
		preview?.expiresUtc
			? new Date(preview.expiresUtc).toLocaleTimeString($locale ?? undefined, {
					hour: '2-digit',
					minute: '2-digit'
				})
			: ''
	);

	// The dialog opens as soon as it is shown: a native modal, so focus stays inside it and the page behind is inert.
	$effect(() => {
		if (dialog && !dialog.open && !closed) dialog.showModal();
	});

	/** The reason in the reader's language when the screen has words for it, else as the server wrote it (as text). */
	function reasonText(reason: string): string {
		const key = imports.reasonKey(reason);
		return key ? $_(`csvImport.reasons.${key}`) : reason;
	}

	function failureText(value: Failure): string {
		if (value.kind === 'status') return $_(`csvImport.failures.${value.key}`);
		const reason = reasonText(value.reason);
		return value.row !== undefined
			? $_('csvImport.failures.atRow', { values: { row: value.row, reason } })
			: reason;
	}

	function noteText(note: string): string {
		const key = imports.noteKey(note);
		return key ? $_(`csvImport.notes.${key}`) : note;
	}

	function choose(event: Event & { currentTarget: HTMLInputElement }): void {
		const chosen = event.currentTarget.files?.[0] ?? null;
		chosenName = chosen?.name ?? '';
		preview = null;
		failure = null;
		problem = chosen ? imports.fileProblem(chosen) : null;
		file = chosen && !problem ? chosen : null;
	}

	async function check(): Promise<void> {
		const sent = file;
		if (!sent || busy) return;
		busy = 'preview';
		failure = null;
		preview = null;
		// A device preview may open the step-up dialog (Api.ts); this dialog waits beneath it.
		const result =
			kind === 'devices'
				? await imports.previewDevices(siteCode, sent)
				: await imports.previewMappings(siteCode, sent);
		busy = null;
		if (closed || file !== sent) return;
		if (result.hasErrors || !result.data) {
			failure = imports.failureOf(result);
			return;
		}
		preview = result.data;
	}

	async function commit(): Promise<void> {
		const sent = file;
		const token = preview?.previewToken;
		if (!sent || !token || !ready || busy) return;
		busy = 'commit';
		failure = null;
		if (kind === 'devices') {
			const result = await imports.commitDevices(siteCode, sent, token);
			busy = null;
			if (closed) return;
			if (result.hasErrors || !result.data) return refused(imports.failureOf(result));
			credentials = result.data.credentials;
			notes = result.data.notes.map(noteText);
			forgetFile();
			toast.success($_('csvImport.devices.committed', { values: { count: result.data.devices } }));
			await onCommitted(result.data.devices);
		} else {
			const result = await imports.commitMappings(siteCode, sent, token);
			busy = null;
			if (closed) return;
			if (result.hasErrors || !result.data) return refused(imports.failureOf(result));
			toast.success(
				$_('csvImport.mappings.committed', { values: { count: result.data.mappings } })
			);
			await onCommitted(result.data.mappings);
			close();
		}
	}

	/** A refused commit: its token is spent unless the server was only busy, so the file is checked again. */
	function refused(value: Failure): void {
		failure = value;
		if (!(value.kind === 'status' && value.key === 'busy')) preview = null;
	}

	function forgetFile(): void {
		file = null;
		preview = null;
		problem = null;
		chosenName = '';
		if (input) input.value = '';
	}

	/** Close, unless new credentials are on screen and the user has not confirmed copying them. */
	function requestClose(): void {
		if (busy === 'commit') return;
		if (credentials && !confirmed) {
			prompts += 1;
			return;
		}
		close();
	}

	/** Drops everything the dialog held (credentials, file, preview and its token), then closes. */
	function close(): void {
		if (closed) return;
		closed = true;
		credentials = null;
		notes = [];
		forgetFile();
		failure = null;
		if (dialog?.open) dialog.close();
		onClose();
	}

	// The browser may close a modal on a repeated Escape without a cancel it lets the page refuse: a commit on its way
	// (its answer carries the credentials) and credentials not yet confirmed keep the dialog open; anything else closes
	// it as the close button does.
	function closedByBrowser(): void {
		if (closed) return;
		if (busy === 'commit' || (credentials && !confirmed)) {
			if (credentials) prompts += 1;
			dialog?.showModal();
			return;
		}
		close();
	}

	onDestroy(() => {
		closed = true;
		credentials = null;
		file = null;
		preview = null;
	});
</script>

<!-- A page that is left or put in the back/forward cache never keeps a credential or a preview token. -->
<svelte:window onpagehide={close} />

<dialog
	bind:this={dialog}
	data-testid="csv-import-dialog"
	data-kind={kind}
	aria-labelledby="csv-import-title"
	aria-describedby="csv-import-description"
	oncancel={(event) => {
		event.preventDefault();
		requestClose();
	}}
	onclose={closedByBrowser}
	class="m-auto max-h-[calc(100dvh-2rem)] w-[min(64rem,calc(100vw-2rem))] overflow-hidden rounded-xl border bg-card p-0 text-card-foreground shadow-xl backdrop:bg-black/40"
>
	<div class="flex max-h-[calc(100dvh-2rem)] flex-col">
		<header class="flex items-start gap-3 border-b px-6 py-4">
			<div
				class="flex size-10 shrink-0 items-center justify-center rounded-[9px] bg-primary/10 text-primary dark:bg-button-primary/20 dark:text-foreground"
			>
				<FileUp class="size-5" aria-hidden="true" />
			</div>
			<div class="min-w-0 flex-1">
				<h2 id="csv-import-title" class="text-xl font-semibold">{$_(`${words}.title`)}</h2>
				<p id="csv-import-description" class="mt-1 text-sm text-secondary-foreground">
					{$_(`${words}.description`, { values: { site: siteCode } })}
				</p>
			</div>
			<button
				type="button"
				data-testid="import-dismiss"
				aria-label={$_('csvImport.close')}
				disabled={busy === 'commit'}
				onclick={requestClose}
				class="inline-flex size-9 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
			>
				<X class="size-4" aria-hidden="true" />
			</button>
		</header>

		<div class="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-6 py-4">
			{#if credentials}
				<ImportedCredentials {credentials} {notes} bind:confirmed {prompts} />
			{:else}
				<div class="flex flex-col gap-1.5">
					<label for="csv-import-file" class="text-sm font-medium">{$_('csvImport.file')}</label>
					<!-- The browser's own file control speaks the browser's language: it stays hidden behind a button in the
					     reader's language, which opens it. -->
					<input
						id="csv-import-file"
						bind:this={input}
						type="file"
						accept=".csv,text/csv"
						disabled={busy !== null}
						onchange={choose}
						tabindex="-1"
						class="hidden"
					/>
					<div class="flex min-w-0 flex-wrap items-center gap-3">
						<button
							type="button"
							data-testid="import-choose"
							disabled={busy !== null}
							onclick={() => input?.click()}
							aria-describedby="csv-import-file-name csv-import-limits"
							class="inline-flex h-9 items-center gap-2 rounded-md border bg-card px-3 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
						>
							<FileUp class="size-4" aria-hidden="true" />
							{$_('csvImport.choose')}
						</button>
						<span
							id="csv-import-file-name"
							data-testid="import-file-name"
							title={chosenName}
							class="min-w-0 truncate text-sm text-muted-foreground"
							>{chosenName || $_('csvImport.noFile')}</span
						>
					</div>
					<p id="csv-import-limits" class="text-xs text-muted-foreground">
						{$_(`${words}.limits`)}
					</p>
					<details class="text-xs text-muted-foreground">
						<summary class="cursor-pointer font-medium text-foreground">
							{$_('csvImport.header')}
						</summary>
						<p class="mt-1">
							<code dir="ltr" class="font-mono break-words text-foreground"
								>{header.required.join(',')}</code
							>
						</p>
						{#if header.optional.length}
							<p class="mt-1">
								{$_('csvImport.optional')}
								<code dir="ltr" class="font-mono break-words text-foreground"
									>{header.optional.join(',')}</code
								>
							</p>
						{/if}
					</details>
				</div>

				{#if problem}
					<p
						role="alert"
						data-testid="import-file-problem"
						class="rounded-lg border border-status-danger-border bg-status-danger px-4 py-3 text-sm text-status-danger-foreground"
					>
						{$_(`csvImport.fileProblems.${problem}`)}
					</p>
				{/if}

				{#if busy}
					<p role="status" class="text-sm text-muted-foreground" data-testid="import-busy">
						{$_(busy === 'preview' ? 'csvImport.previewing' : 'csvImport.committing')}
					</p>
				{/if}

				{#if failure}
					<p
						role="alert"
						data-testid="import-failure"
						class="rounded-lg border border-status-danger-border bg-status-danger px-4 py-3 text-sm text-status-danger-foreground"
					>
						{failureText(failure)}
					</p>
				{/if}

				{#if preview}
					<div class="flex flex-col gap-1 text-sm" data-testid="import-summary">
						<p>
							{$_('csvImport.summary', {
								values: { rows: preview.rows, passing, refused: preview.errorCount }
							})}
						</p>
						<p class="text-xs text-muted-foreground">
							{$_('csvImport.sha')}
							<code dir="ltr" class="font-mono break-all">{preview.fileSha256}</code>
						</p>
					</div>
					{#if ready}
						<p
							data-testid="import-ready"
							class="flex items-start gap-2 rounded-lg border border-status-success-border bg-status-success px-4 py-3 text-sm text-status-success-foreground"
						>
							<CircleCheck class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
							{$_('csvImport.ready', { values: { time: until } })}
						</p>
					{:else}
						<p
							data-testid="import-refused"
							class="flex items-start gap-2 rounded-lg border border-status-danger-border bg-status-danger px-4 py-3 text-sm text-status-danger-foreground"
						>
							<TriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
							{preview.errorCount > 0
								? $_('csvImport.refused', { values: { count: preview.errorCount } })
								: $_('csvImport.nothingToImport')}
						</p>
					{/if}
					{#if preview.errorCount > 0}
						<ImportRefusals errors={preview.errors} total={preview.errorCount} {reasonText} />
					{/if}
					<ImportPreviewRows {kind} {devices} {mappings} />
				{/if}
			{/if}
		</div>

		<footer class="flex flex-wrap items-center justify-end gap-2 border-t px-6 py-3">
			{#if credentials}
				<button
					type="button"
					data-testid="import-close"
					disabled={!confirmed}
					onclick={requestClose}
					class="inline-flex h-10 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
				>
					{$_('csvImport.close')}
				</button>
			{:else}
				<button
					type="button"
					data-testid="import-close"
					disabled={busy === 'commit'}
					onclick={requestClose}
					class="inline-flex h-10 items-center rounded-md border bg-card px-4 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
				>
					{$_('csvImport.cancel')}
				</button>
				<button
					type="button"
					data-testid="import-preview"
					disabled={!file || busy !== null}
					onclick={check}
					class="inline-flex h-10 items-center rounded-md border bg-card px-4 text-sm font-medium hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60"
				>
					{$_('csvImport.preview')}
				</button>
				<button
					type="button"
					data-testid="import-commit"
					disabled={!ready || busy !== null}
					onclick={commit}
					class="inline-flex h-10 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:opacity-60 dark:text-white"
				>
					{$_(`${words}.commit`, { values: { count: passing } })}
				</button>
			{/if}
		</footer>
	</div>
</dialog>
