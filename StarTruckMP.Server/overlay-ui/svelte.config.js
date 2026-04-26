import adapter from '@sveltejs/adapter-static';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	compilerOptions: {
		// Force runes mode for the project, except for libraries. Can be removed in svelte 6.
		runes: ({ filename }) => (filename.split(/[/\\]/).includes('node_modules') ? undefined : true)
	},
	kit: {
		adapter: adapter({
			// Single-page app: all routes fall back to index.html
			fallback: 'index.html',
			pages: '../wwwroot/overlay',
			assets: '../wwwroot/overlay'
		}),
		paths: {
			// Must match the base path used in vite.config.ts
			base: '/overlay'
		}
	}
};

export default config;
