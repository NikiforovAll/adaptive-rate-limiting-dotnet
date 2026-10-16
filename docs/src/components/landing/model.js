import { compare } from '../simulator/engine.js';

export const WORLD = Object.freeze({ quota: 3000, instances: 2, demand: 2000 });

let cache;

/** Runs the default comparison once per build. The landing page shows these as model numbers. */
export function results() {
	cache ??= Object.fromEntries(compare(WORLD).map((r) => [r.mode, r]));
	return cache;
}

/** Builds an SVG path for `values` over the box, sampled every `step` seconds. */
export function linePath(history, value, box, { step = 3, max } = {}) {
	const last = history.at(-1).t;
	const top = max ?? Math.max(...history.map(value));
	const pts = [];
	for (let i = 0; i < history.length; i += step) {
		const p = history[i];
		pts.push([box.x + (p.t / last) * box.w, box.y + box.h * (1 - value(p) / top)]);
	}
	return pts.map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(1)} ${y.toFixed(1)}`).join('');
}

export const totalRate = (p) => p.rates.reduce((a, b) => a + b, 0);
