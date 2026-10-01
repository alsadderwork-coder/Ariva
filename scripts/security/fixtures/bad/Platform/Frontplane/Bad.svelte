<script lang="ts">
  let { userInput, url } = $props();
  const result = eval(userInput);
  localStorage.setItem('access_token', userInput);
  fetch(url);
  function paint(el: HTMLElement) { el.innerHTML = userInput; }
</script>

<div>{@html userInput}</div>
