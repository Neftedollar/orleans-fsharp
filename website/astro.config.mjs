// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightThemeBlack from 'starlight-theme-black';

// https://astro.build/config
export default defineConfig({
	site: 'https://neftedollar.com',
	base: '/orleans-fsharp',
	trailingSlash: 'always',
	integrations: [
		starlight({
			title: 'Orleans.FSharp',
			description: 'Idiomatic F# API for Microsoft Orleans',
			plugins: [starlightThemeBlack({})],
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/Neftedollar/orleans-fsharp' },
			],
			editLink: {
				baseUrl: 'https://github.com/Neftedollar/orleans-fsharp/edit/main/website/',
			},
			sidebar: [
				{ label: 'Getting Started', link: '/getting-started' },
				{
					label: 'Core Guides',
					items: [
						{ label: 'Grain Definition', link: '/grain-definition' },
						{ label: 'Silo Configuration', link: '/silo-configuration' },
						{ label: 'Client Configuration', link: '/client-configuration' },
						{ label: 'Serialization', link: '/serialization' },
						{ label: 'Streaming', link: '/streaming' },
						{ label: 'Event Sourcing', link: '/event-sourcing' },
						{ label: 'Testing', link: '/testing' },
						{ label: 'Security', link: '/security' },
						{ label: 'Resilience', link: '/resilience' },
						{ label: 'Advanced', link: '/advanced' },
						{ label: 'Analyzers', link: '/analyzers' },
					],
				},
				{ label: 'Redis Example', link: '/redis-example' },
				{ label: 'API Reference', link: '/api-reference' },
				{ label: 'How To', link: '/how-to' },
				{ label: 'Comparison', link: '/comparison' },
				{ label: 'FAQ', link: '/faq' },
			],
			customCss: ['./src/styles/custom.css'],
			head: [
				// SEO meta
				{ tag: 'meta', attrs: { name: 'keywords', content: 'fsharp, f#, orleans, dotnet, .net, actors, distributed systems, computation expressions, virtual actors, grains, microsoft orleans, functional programming' } },
				{ tag: 'meta', attrs: { name: 'author', content: 'Orleans.FSharp Contributors' } },
				// Open Graph
				{ tag: 'meta', attrs: { property: 'og:type', content: 'website' } },
				{ tag: 'meta', attrs: { property: 'og:site_name', content: 'Orleans.FSharp' } },
				{ tag: 'meta', attrs: { property: 'og:title', content: 'Orleans.FSharp — Idiomatic F# for Microsoft Orleans' } },
				{ tag: 'meta', attrs: { property: 'og:description', content: 'Full Orleans 10 parity with idiomatic F# computation expressions. 1500+ tests. grain {}, siloConfig {}, eventSourcedGrain {}.' } },
				{ tag: 'meta', attrs: { property: 'og:locale', content: 'en_US' } },
				// Twitter Card
				{ tag: 'meta', attrs: { name: 'twitter:card', content: 'summary_large_image' } },
				{ tag: 'meta', attrs: { name: 'twitter:title', content: 'Orleans.FSharp — Idiomatic F# for Microsoft Orleans' } },
				{ tag: 'meta', attrs: { name: 'twitter:description', content: 'Full Orleans 10 parity with idiomatic F# computation expressions. 1500+ tests. grain {}, siloConfig {}, eventSourcedGrain {}.' } },
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
