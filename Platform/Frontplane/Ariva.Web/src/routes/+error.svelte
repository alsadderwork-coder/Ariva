<script lang="ts">
	import { page } from '$app/state';
	import { CircleAlert } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import SimplePageHeader from '$lib/components/shared/SimplePageHeader.svelte';
</script>

<SimplePageHeader
	icon={CircleAlert}
	title={$_('errors.notFound.title')}
	description={$_('errors.notFound.description')}
/>

<div class="rounded-xl border bg-card p-8 text-center">
	<p class="text-5xl font-bold text-muted-foreground tabular-nums">{page.status}</p>
	<a
		href="/"
		class="mt-6 inline-flex h-10 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-white"
	>
		{$_('errors.notFound.back')}
	</a>
</div>
