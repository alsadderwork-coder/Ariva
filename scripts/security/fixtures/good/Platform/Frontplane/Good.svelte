<script lang="ts">
  import { api } from '$lib/core/Api';
  let { userInput } = $props();
  const zones = api.get('zones');
  function paint(el: HTMLElement) { el.textContent = userInput; }
</script>

<div>{userInput}</div>
