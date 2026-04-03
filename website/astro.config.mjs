// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
	site: 'https://neftedollar.github.io',
	base: '/orleans-fsharp',
	integrations: [
		starlight({
			title: 'Orleans.FSharp',
			description: 'Idiomatic F# API for Microsoft Orleans',
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/Neftedollar/orleans-fsharp' },
			],
			editLink: {
				baseUrl: 'https://github.com/Neftedollar/orleans-fsharp/edit/main/website/',
			},
			sidebar: [
				{ label: 'Getting Started', slug: 'getting-started' },
				{
					label: 'Guides',
					items: [
						{ label: 'Grain Definition', slug: 'guides/grain-definition' },
						{ label: 'Silo Configuration', slug: 'guides/silo-configuration' },
						{ label: 'Client Configuration', slug: 'guides/client-configuration' },
						{ label: 'Streaming', slug: 'guides/streaming' },
						{ label: 'Event Sourcing', slug: 'guides/event-sourcing' },
						{ label: 'Testing', slug: 'guides/testing' },
						{ label: 'Security', slug: 'guides/security' },
						{ label: 'Advanced', slug: 'guides/advanced' },
					],
				},
				{ label: 'API Reference', slug: 'api-reference' },
			],
			customCss: [],
			head: [
				// SEO
				{ tag: 'meta', attrs: { name: 'keywords', content: 'fsharp, orleans, dotnet, actors, distributed systems, computation expressions, virtual actors, grains' } },
				{ tag: 'meta', attrs: { property: 'og:type', content: 'website' } },
				{ tag: 'meta', attrs: { property: 'og:title', content: 'Orleans.FSharp — Idiomatic F# for Microsoft Orleans' } },
				{ tag: 'meta', attrs: { property: 'og:description', content: 'Full Orleans 10.0.1 parity with computation expressions. 800+ tests. grain {}, siloConfig {}, eventSourcedGrain {} CEs.' } },
			],
		}),
	],
});
