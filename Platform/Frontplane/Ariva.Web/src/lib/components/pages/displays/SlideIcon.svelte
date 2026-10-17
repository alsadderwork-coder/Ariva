<script lang="ts">
	import {
		Accessibility,
		Fingerprint,
		Glasses,
		IdCard,
		Info,
		Luggage,
		PhoneOff,
		ScanFace,
		Signpost,
		Users
	} from '@lucide/svelte';

	/**
	 * A slide's built-in icon (ARV-141), drawn from the bundled icon set by its name; an unknown name draws the information
	 * icon. Nothing is uploaded and no markup comes from the server.
	 */
	let { name, class: className = '' }: { name: string; class?: string } = $props();

	const icons = {
		Information: Info,
		Passport: IdCard,
		EGate: ScanFace,
		HatAndGlasses: Glasses,
		Fingerprint,
		Family: Users,
		Accessibility,
		Baggage: Luggage,
		NoPhone: PhoneOff,
		FollowSigns: Signpost
	} as const;

	// Own keys only: a name such as constructor or __proto__ draws the information icon, never something the map inherits.
	const Icon = $derived(Object.hasOwn(icons, name) ? icons[name as keyof typeof icons] : Info);
</script>

<Icon class={className} aria-hidden="true" data-icon={name} />
