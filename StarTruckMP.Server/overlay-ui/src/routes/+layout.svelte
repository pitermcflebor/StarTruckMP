<script lang="ts">
	import favicon from '$lib/assets/favicon.svg';
	import { dev } from '\$app/environment';
	import bkgimg from '$lib/assets/game-screenshot.webp';

	let { children } = $props();
	
	let bg = dev ? `center / contain no-repeat url("${bkgimg}")` : `transparent`;
	console.log(bg);
	// show image while developing
	window.CSS.registerProperty({
		name: '--background',
		syntax: '*',
		inherits: false,
		initialValue: bg
	})
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
		background: var(--background);
		margin: 0;
		padding: 0;
		overflow: hidden;
	}
	
	:global(*) {
		font-family: Arial, Helvetica, sans-serif;
		color: yellow;
	}
</style>