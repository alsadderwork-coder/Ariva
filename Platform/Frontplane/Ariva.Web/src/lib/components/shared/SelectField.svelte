<script lang="ts">
	interface Props {
		id: string;
		label: string;
		value: string;
		options: { value: string; label: string }[];
		required?: boolean;
	}

	let { id, label, value = $bindable(), options, required = false }: Props = $props();
</script>

<div class="flex min-w-0 flex-col gap-1">
	<label for={id} class="text-xs font-medium">{label}</label>
	<select
		{id}
		name={id}
		bind:value
		{required}
		class="h-9 min-w-0 rounded-md border border-input bg-background px-2 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
	>
		{#each options as option (option.value)}
			<option value={option.value}>{option.label}</option>
		{/each}
	</select>
</div>
