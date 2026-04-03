// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
	site: 'https://neftedollar.com',
	base: '/orleans-fsharp',
	trailingSlash: 'always',
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
				{ label: 'How To', slug: 'how-to' },
				{ label: 'FAQ', slug: 'faq' },
				{ label: 'Comparison', slug: 'comparison' },
			],
			customCss: [],
			head: [
				// SEO meta
				{ tag: 'meta', attrs: { name: 'keywords', content: 'fsharp, f#, orleans, dotnet, .net, actors, distributed systems, computation expressions, virtual actors, grains, microsoft orleans, functional programming' } },
				{ tag: 'meta', attrs: { name: 'author', content: 'Orleans.FSharp Contributors' } },
				// Open Graph
				{ tag: 'meta', attrs: { property: 'og:type', content: 'website' } },
				{ tag: 'meta', attrs: { property: 'og:site_name', content: 'Orleans.FSharp' } },
				{ tag: 'meta', attrs: { property: 'og:title', content: 'Orleans.FSharp — Idiomatic F# for Microsoft Orleans' } },
				{ tag: 'meta', attrs: { property: 'og:description', content: 'Full Orleans 10.0.1 parity with computation expressions. 800+ tests. grain {}, siloConfig {}, eventSourcedGrain {} CEs. Zero boilerplate.' } },
				{ tag: 'meta', attrs: { property: 'og:locale', content: 'en_US' } },
				// Twitter Card
				{ tag: 'meta', attrs: { name: 'twitter:card', content: 'summary_large_image' } },
				{ tag: 'meta', attrs: { name: 'twitter:title', content: 'Orleans.FSharp — Idiomatic F# for Microsoft Orleans' } },
				{ tag: 'meta', attrs: { name: 'twitter:description', content: 'Full Orleans 10.0.1 parity with F# computation expressions. grain {}, siloConfig {}, eventSourcedGrain {} CEs.' } },
				// JSON-LD structured data
				{ tag: 'script', attrs: { type: 'application/ld+json' }, content: JSON.stringify({
					"@context": "https://schema.org",
					"@type": "SoftwareSourceCode",
					"name": "Orleans.FSharp",
					"description": "Idiomatic F# API for Microsoft Orleans — computation expressions, not boilerplate",
					"url": "https://neftedollar.com/orleans-fsharp/",
					"codeRepository": "https://github.com/Neftedollar/orleans-fsharp",
					"programmingLanguage": ["F#", "C#"],
					"runtimePlatform": ".NET 10",
					"license": "https://opensource.org/licenses/MIT",
					"operatingSystem": "Cross-platform",
					"applicationCategory": "Developer Tools",
					"keywords": "F#, Orleans, actors, distributed systems, computation expressions"
				})},
			],
		}),
	],
});
