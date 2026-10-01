<script lang="ts" module>
	export type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';
</script>

<script lang="ts">
	import { cn } from '$lib/utils';

	let { tone, label }: { tone: StatusTone; label: string } = $props();

	const tones: Record<StatusTone, string> = {
		success: 'border-status-success-border bg-status-success text-status-success-foreground',
		warning: 'border-status-warning-border bg-status-warning text-status-warning-foreground',
		danger: 'border-status-danger-border bg-status-danger text-status-danger-foreground',
		info: 'border-status-info-border bg-status-info text-status-info-foreground',
		neutral: 'border-status-neutral-border bg-status-neutral text-status-neutral-foreground'
	};

	const dots: Record<StatusTone, string> = {
		success: 'bg-status-success-solid',
		warning: 'bg-status-warning-solid',
		danger: 'bg-status-danger-solid',
		info: 'bg-status-info-solid',
		neutral: 'bg-status-neutral-foreground'
	};
</script>

<!-- Status is always paired with text; colour is never the only signal. -->
<span
	data-status={tone}
	class={cn(
		'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium whitespace-nowrap',
		tones[tone]
	)}
>
	<span class={cn('size-1.5 rounded-full', dots[tone])} aria-hidden="true"></span>
	{label}
</span>
