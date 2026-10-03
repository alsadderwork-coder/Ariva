<script lang="ts">
	import { page } from '$app/state';
	import { CircleAlert } from '@lucide/svelte';
	import { _ } from 'svelte-i18n';
	import PublicFrame from '$lib/components/auth/PublicFrame.svelte';
</script>

<svelte:head>
	<title>{$_('errors.notFound.title')} · {$_('app.title')}</title>
</svelte:head>

<!-- Outside the signed-in shell: an unknown address shows nothing about the app beyond this page. -->
<PublicFrame>
	<section
		class="w-full max-w-md rounded-xl border bg-card p-8 text-center"
		aria-labelledby="error-title"
	>
		<CircleAlert class="mx-auto mb-3 size-8 text-muted-foreground" aria-hidden="true" />
		<h1 id="error-title" class="text-[28px] leading-none font-semibold tracking-[-0.14px]">
			{$_('errors.notFound.title')}
		</h1>
		<p class="mt-2 text-[13px] text-secondary-foreground">{$_('errors.notFound.description')}</p>
		<p class="mt-6 text-5xl font-bold text-muted-foreground tabular-nums">{page.status}</p>
		<a
			href="/"
			class="mt-6 inline-flex h-10 items-center rounded-md bg-button-primary px-4 text-sm font-medium text-primary-foreground hover:opacity-90 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none dark:text-white"
		>
			{$_('errors.notFound.back')}
		</a>
	</section>
</PublicFrame>
