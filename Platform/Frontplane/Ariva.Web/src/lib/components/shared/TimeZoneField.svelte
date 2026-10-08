<script lang="ts">
	import { timeZoneGroups } from './time-zones';

	interface Props {
		id: string;
		label: string;
		value: string;
		required?: boolean;
	}

	let { id, label, value = $bindable(), required = false }: Props = $props();

	const groups = $derived(timeZoneGroups(value));
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
		{#each groups as group (group.region)}
			<optgroup label={group.region}>
				{#each group.zones as zone (zone.value)}
					<option value={zone.value}>{zone.label}</option>
				{/each}
			</optgroup>
		{/each}
	</select>
</div>
