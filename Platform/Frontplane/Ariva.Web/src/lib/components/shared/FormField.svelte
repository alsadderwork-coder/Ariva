<script lang="ts">
	interface Props {
		id: string;
		label: string;
		value: string | number;
		type?: 'text' | 'number';
		required?: boolean;
		maxlength?: number;
		min?: number;
		max?: number;
		step?: number | 'any';
		pattern?: string;
		hint?: string;
		uppercase?: boolean;
		readonly?: boolean;
	}

	let {
		id,
		label,
		value = $bindable(),
		type = 'text',
		required = false,
		maxlength,
		min,
		max,
		step,
		pattern,
		hint,
		uppercase = false,
		readonly = false
	}: Props = $props();
</script>

<!-- A labelled input; the value is bound, never rendered as markup. -->
<div class="flex min-w-0 flex-col gap-1">
	<label for={id} class="text-xs font-medium">{label}</label>
	<input
		{id}
		name={id}
		{type}
		bind:value
		{required}
		{maxlength}
		{min}
		{max}
		{step}
		{pattern}
		{readonly}
		autocomplete="off"
		spellcheck="false"
		autocapitalize={uppercase ? 'characters' : 'off'}
		aria-describedby={hint ? `${id}-hint` : undefined}
		class="h-9 min-w-0 rounded-md border border-input bg-background px-2.5 text-sm read-only:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
	/>
	{#if hint}<p id="{id}-hint" class="text-xs text-muted-foreground">{hint}</p>{/if}
</div>
