// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
	site: 'https://nikiforovall.github.io',
	base: '/adaptive-rate-limiting-dotnet',
	integrations: [
		starlight({
			title: 'Nall.RateLimiting.Adaptive',
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
					label: 'Guide',
					items: [
						{ slug: 'guide/why' },
						{ slug: 'guide/getting-started' },
						{ slug: 'guide/how-it-works' },
						{ slug: 'guide/controllers' },
						{ slug: 'guide/configuration' },
						{ slug: 'guide/metrics' },
						{ slug: 'guide/instance-count' },
						{ slug: 'guide/microsoft-graph' },
					],
				},
				{
					label: 'API reference',
					collapsed: true,
					items: [{ autogenerate: { directory: 'reference' } }],
				},
			],
		}),
	],
});
