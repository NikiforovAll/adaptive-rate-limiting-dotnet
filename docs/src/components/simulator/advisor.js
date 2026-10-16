import { runStory, withDefaults } from './engine.js';
import { axes, esc, fmt, path, short, svg } from './draw.js';
import { FIELDS, getOption, setOption } from './options.js';

const round = (v, to) => Math.max(to, Math.round(v / to) * to);

/** The settings the sweep can change, and the values it tries. */
export const SWEEPS = Object.freeze({
	maxUnitsPerMinute: {
		values: (w) => [0.5, 0.6, 0.7, 0.8, 0.85, 0.9, 0.95, 1, 1.1, 1.25, 1.5, 2].map((f) => round(f * w.quota, 50)),
		quotaLine: true,
	},
	'aimd.increaseStep': { controller: 'aimd', values: () => [30, 60, 120, 240, 360, 480, 600, 900, 1200, 1800] },
	'aimd.decreaseFactor': { controller: 'aimd', values: () => [0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95] },
	'cubic.beta': { controller: 'cubic', values: () => [0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95] },
	'cubic.scaleConstant': { controller: 'cubic', values: () => [0.05, 0.1, 0.2, 0.4, 0.8, 1.6, 3.2] },
	decreaseCooldown: { values: () => [0, 1, 2, 5, 10, 15, 20, 30] },
	maxPause: { values: () => [0, 5, 10, 15, 30, 45, 60, 90] },
});

export const nameOf = (key) => FIELDS.find((f) => f.key === key)?.name ?? key;

/** Sets `key` to `value`, and keeps Initial and Min at or below a swept Max, as the validation needs. */
export function sweepOptions(options, key, value) {
	let next = setOption(options, key, value);
	if (key === 'maxUnitsPerMinute') {
		const o = withDefaults(next);
		if (o.initialUnitsPerMinute > value) next = setOption(next, 'initialUnitsPerMinute', value);
		if (o.minUnitsPerMinute > value) next = setOption(next, 'minUnitsPerMinute', value);
	}
	return next;
}

/** Runs the story once for each value. Yields to the page between runs. `isStale` stops it. */
export async function runSweep({ world, story, seed, controller, key }, onProgress, isStale) {
	const spec = SWEEPS[key];
	const mode = spec.controller ?? controller;
	const values = spec.values(world);
	const rows = [];
	for (const value of values) {
		await new Promise((r) => setTimeout(r, 0));
		if (isStale()) return null;
		const r = runStory({ ...world, mode, options: sweepOptions(world.options, key, value) }, story, seed);
		rows.push({ value, throughput: r.throughput, share: r.share, n429: r.n429, lost: r.rejected + r.gaveUp, p95: r.p95 });
		onProgress(rows.length, values.length);
	}
	return { rows, mode, key, pick: pick(rows) };
}

/** The model pick: of the values within 2% of the best throughput, the one with the fewest throttles. */
export function pick(rows) {
	const best = Math.max(...rows.map((r) => r.throughput));
	const near = rows.filter((r) => r.throughput >= 0.98 * best);
	return near.reduce((a, r) => (r.n429 < a.n429 || (r.n429 === a.n429 && r.p95 < a.p95) ? r : a), near[0]);
}

const isUnits = (key) => key.includes('UnitsPerMinute') || key === 'aimd.increaseStep';
const tick = (key, v) => (isUnits(key) ? short(v) : label(key, v));
const label = (key, v) => (isUnits(key) ? fmt(v) : `${v}${FIELDS.find((f) => f.key === key)?.time ? ' s' : ''}`);

export function drawSweep(target, result, ctx, width) {
	const chart = target.querySelector('[data-role="sweep-chart"]');
	const table = target.querySelector('[data-role="sweep-table"]');
	const note = target.querySelector('[data-role="sweep-pick"]');
	const { rows, key, pick: chosen, mode } = result;
	const W = Math.max(300, width);
	const narrow = W < 520;
	const panel = 120;
	const gap = 34;
	const pad = { l: 44, r: 10, t: 18, b: 0 };
	const H = pad.t + panel * 2 + gap + 40;
	const xs = rows.map((_, i) => pad.l + 8 + (i / Math.max(1, rows.length - 1)) * (W - pad.l - pad.r - 16));
	const xOf = (v) => {
		const vs = rows.map((r) => r.value);
		if (v <= vs[0]) return xs[0];
		for (let i = 1; i < vs.length; i++) {
			if (v <= vs[i]) return xs[i - 1] + ((v - vs[i - 1]) / (vs[i] - vs[i - 1])) * (xs[i] - xs[i - 1]);
		}
		return xs.at(-1);
	};
	const ticks = rows.map((r, i) => (narrow && i % 2 && i !== rows.length - 1 ? null : [xs[i], tick(key, r.value)])).filter(Boolean);

	const top = { t: pad.t, b: H - pad.t - panel, l: pad.l, r: pad.r };
	const tMax = Math.max(...rows.map((r) => r.throughput)) * 1.1;
	const a1 = axes(W, H, top, tMax, '', []);
	const bottom = { t: pad.t + panel + gap, b: 40, l: pad.l, r: pad.r };
	const nMax = Math.max(10, ...rows.map((r) => r.n429)) * 1.15;
	const a2 = axes(W, H, bottom, nMax, `${nameOf(key)} (${mode.toUpperCase()})`, ticks);
	let s = a1.s + a2.s;
	s += `<text class="t-small" x="${pad.l}" y="${pad.t - 6}">Accepted units per minute: higher is better</text>`;
	s += `<text class="t-small" x="${pad.l}" y="${bottom.t - 6}">Throttled responses: lower is better</text>`;
	const marker = (x, cls, text, y) =>
		`<line class="${cls}" x1="${x}" x2="${x}" y1="${pad.t}" y2="${H - bottom.b}"/><text class="t-small" x="${Math.min(x + 4, W - 70)}" y="${y}">${esc(text)}</text>`;
	if (SWEEPS[key].quotaLine) s += marker(xOf(ctx.quota), 'st-limit', 'hidden quota', pad.t + 12);
	const current = getOption(withDefaults(ctx.options), key);
	s += marker(xOf(current), 'st-current', 'now', pad.t + 26);
	s += `<path class="st-series s-pick" d="${path(rows.map((r, i) => [xs[i], a1.y(r.throughput)]))}"/>`;
	s += `<path class="st-series s-bad" d="${path(rows.map((r, i) => [xs[i], a2.y(r.n429)]))}"/>`;
	rows.forEach((r, i) => {
		const hit = r === chosen;
		s += `<circle class="${hit ? 'dot-pick' : 'dot'}" cx="${xs[i]}" cy="${a1.y(r.throughput)}" r="${hit ? 5 : 3}"><title>${esc(`${label(key, r.value)}: ${fmt(r.throughput)}/min`)}</title></circle>`;
		s += `<circle class="${hit ? 'dot-pick' : 'dot'}" cx="${xs[i]}" cy="${a2.y(r.n429)}" r="${hit ? 5 : 3}"><title>${esc(`${label(key, r.value)}: ${fmt(r.n429)} throttled`)}</title></circle>`;
	});
	if (chart) svg(chart, W, H, s, `Sweep of ${nameOf(key)}: accepted units and throttled responses for each value.`);

	if (note) {
		const last = rows.at(-1);
		const best = Math.max(...rows.map((r) => r.throughput));
		note.innerHTML =
			`Model pick: <b>${nameOf(key)} = ${label(key, chosen.value)}</b>. It gets ${Math.round((100 * chosen.throughput) / best)}% of the best throughput, with ${fmt(chosen.n429)} throttled responses. ` +
			(last !== chosen ? `At ${label(key, last.value)}: ${fmt(last.throughput)}/min with ${fmt(last.n429)} throttled.` : '');
	}

	if (table) {
		let t = `<thead><tr><th scope="col">${esc(nameOf(key))}</th><th scope="col">Throughput</th><th scope="col">Share of quota</th><th scope="col">Throttled</th><th scope="col">Lost</th><th scope="col">p95 wait</th></tr></thead><tbody>`;
		for (const r of rows) {
			t += `<tr${r === chosen ? ' class="pick"' : ''}><th scope="row">${label(key, r.value)}</th><td class="num">${fmt(r.throughput)}/min</td><td class="num">${Math.round(r.share * 100)}%</td><td class="num">${fmt(r.n429)}</td><td class="num">${fmt(r.lost)}</td><td class="num">${r.p95.toFixed(1)} s</td></tr>`;
		}
		table.innerHTML = `${t}</tbody>`;
	}
}
