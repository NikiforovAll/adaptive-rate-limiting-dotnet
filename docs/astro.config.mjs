// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
	site: 'https://nikiforovall.github.io',
	base: '/adaptive-rate-limiting-dotnet',
	integrations: [
		starlight({
			title: 'RateLimiting.Adaptive',
			description: 'Adaptive client-side rate limiting for .NET HttpClient, with pluggable rate controllers.',
			social: [
				{
					icon: 'github',
					label: 'GitHub',
					href: 'https://github.com/NikiforovAll/adaptive-rate-limiting-dotnet',
				},
			],
			editLink: {
				baseUrl: 'https://github.com/NikiforovAll/adaptive-rate-limiting-dotnet/edit/main/docs/',
			},
			sidebar: [
				{
					label: 'Start here',
					items: [{ slug: 'guide/why' }, { slug: 'guide/getting-started' }],
				},
				{
					label: 'Concepts',
					items: [
						{ slug: 'guide/how-it-works' },
						{ slug: 'guide/controllers' },
						{ slug: 'guide/playground', badge: { text: 'Live', variant: 'tip' } },
					],
				},
				{
					label: 'Guides',
					items: [
						{ slug: 'guide/configuration' },
						{ slug: 'guide/instance-count' },
						{ slug: 'guide/metrics' },
						{ slug: 'guide/microsoft-graph' },
						{ slug: 'guide/recipes' },
						{ slug: 'guide/troubleshooting' },
					],
				},
			],
			customCss: ['./src/styles/custom.css'],
			head: [
				{ tag: 'link', attrs: { rel: 'preconnect', href: 'https://fonts.googleapis.com' } },
				{ tag: 'link', attrs: { rel: 'preconnect', href: 'https://fonts.gstatic.com', crossorigin: '' } },
				{
					tag: 'link',
					attrs: {
						rel: 'stylesheet',
						href: 'https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap',
					},
				},
			],
			components: {
				Hero: './src/components/landing/Hero.astro',
			},
		}),
	],
});
