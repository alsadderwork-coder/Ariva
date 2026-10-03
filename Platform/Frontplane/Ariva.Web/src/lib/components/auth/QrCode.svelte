<script lang="ts">
	import qrcode from 'qrcode-generator';

	let { value, label }: { value: string; label: string } = $props();

	/** The dark modules as one SVG path, drawn by Svelte as an attribute: no markup string, no innerHTML (CWE-79). */
	const code = $derived.by(() => {
		const qr = qrcode(0, 'M');
		qr.addData(value);
		qr.make();
		const size = qr.getModuleCount();
		let path = '';
		for (let row = 0; row < size; row++) {
			for (let column = 0; column < size; column++) {
				if (qr.isDark(row, column)) path += `M${column + 4} ${row + 4}h1v1h-1z`;
			}
		}
		return { size: size + 8, path };
	});
</script>

<!-- Black on white in both themes, with the four-module quiet zone, so every authenticator app can read it. -->
<svg
	data-testid="totp-qr"
	role="img"
	aria-label={label}
	viewBox="0 0 {code.size} {code.size}"
	shape-rendering="crispEdges"
	class="size-52 rounded-md border bg-white"
>
	<rect width={code.size} height={code.size} class="fill-white" />
	<path d={code.path} class="fill-black" />
</svg>
