<script lang="ts">
	import favicon from '$lib/assets/favicon.svg';

	let { children } = $props();
</script>

<svelte:head>
	<link rel="icon" href={favicon} />
</svelte:head>

{@render children()}

<style>
	:global(html),
	:global(body)
	{
		/* full width and height with transparent background */
		width: 100%;
		height: 100%;
		background: transparent;
		margin: 0;
		padding: 0;
		overflow: hidden;
	}
	
	:global(*) {
		font-family: Arial, Helvetica, sans-serif;
		color: yellow;
	}
</style>