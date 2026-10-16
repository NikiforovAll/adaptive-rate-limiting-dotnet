// DefaultDocumentation writes no frontmatter and links to `*.md` files; Starlight
// needs a `title` on each page and route links, so each page is fixed after.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = join(root, 'docs/src/content/docs/reference');
const assemblies = ['Nall.RateLimiting.Adaptive', 'Nall.RateLimiting.Adaptive.MicrosoftGraph'];
const configuration = process.env.CONFIGURATION ?? 'Release';
const tfm = 'net10.0';

rmSync(out, { recursive: true, force: true });

for (const name of assemblies) {
	const dll = join(root, 'src', name, 'bin', configuration, tfm, `${name}.dll`);
	if (!existsSync(dll)) {
		throw new Error(`${dll} not found. Run: dotnet build -c ${configuration}`);
	}
	const target = join(out, name);
	mkdirSync(target, { recursive: true });
	execFileSync(
		'dotnet',
		['tool', 'run', 'defaultdocumentation', '--', '-a', dll, '-o', target, '--FileNameFactory', 'NameAndMd5Mix', '--GeneratedAccessModifiers', 'Api'],
		{ cwd: root, stdio: 'inherit' },
	);
	fixPages(target, name);
}

// Same rule as github-slugger, which Astro applies to each path segment.
function slug(name) {
	return name.toLowerCase().replace(/[^\p{L}\p{N}\s_-]/gu, '').replace(/\s/g, '-');
}

function fixPages(dir, assemblyName) {
	for (const file of readdirSync(dir).filter((f) => f.endsWith('.md'))) {
		const path = join(dir, file);
		let body = readFileSync(path, 'utf8');
		const heading = body.match(/^## (.+)$/m)?.[1] ?? assemblyName;
		const title = heading.replace(/\\(.)/g, '$1');
		// index.md is served at the folder route, one level above the other pages.
		const up = file === 'index.md' ? './' : '../';
		body = body.replace(/\]\(([^)\s]+?)\.md(#[^)\s]*)?( '[^']*')?\)/g, (_, target, hash = '', tip = '') => {
			const route = target === 'index' ? '' : `${slug(target)}/`;
			return `](${up}${route}${hash}${tip})`;
		});
		writeFileSync(path, `---\ntitle: ${JSON.stringify(title)}\n---\n\n${body}`);
	}
}
