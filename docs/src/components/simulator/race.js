import { MODES } from './engine.js';
import { axes, clock, esc, fmt, MODE_CLASS, path, sumRates, svg } from './draw.js';

const at = (history, t) => history[Math.max(0, Math.min(history.length - 1, Math.floor(t) - 1))];

/** Draws the race at time `cursor`. All modes ran on the same seed and story, so their clocks match. */
export function drawRace(target, results, plan, cursor, width) {
	const chart = target.querySelector('[data-role="compare-chart"]');
	const legend = target.querySelector('[data-role="compare-legend"]');
	const board = target.querySelector('[data-role="compare-board"]');
	const table = target.querySelector('[data-role="compare-table"]');
	const T = plan.duration;
	const done = cursor >= T;
	const W = Math.max(300, width);
	const H = 240;
	const pad = { l: 40, r: 8, t: 8, b: 34 };
	const quota = Math.max(...results[0].history.map((p) => p.limit));
	const ymax = quota * 1.6;
	const x = (t) => pad.l + (t / T) * (W - pad.l - pad.r);
	const ticks = [];
	const tickStep = T > 600 ? 180 : 120;
	for (let t = 0; t <= T; t += tickStep) ticks.push([x(t), `${t / 60}m`]);
	const ax = axes(W, H, pad, ymax, 'sum of rates, units per minute (No limiter: send rate, clipped)', ticks);
	let s = '';
	if (plan.hasWindow) {
		s += `<rect class="f-drop" x="${x(plan.dropAt)}" y="${pad.t}" width="${x(plan.dropAt + plan.dropFor) - x(plan.dropAt)}" height="${H - pad.t - pad.b}"/>`;
	}
	s += ax.s;
	for (const m of results[0].marks ?? []) {
		s += `<line class="st-event" x1="${x(m.t)}" x2="${x(m.t)}" y1="${pad.t}" y2="${H - pad.b}"><title>${esc(`${clock(m.t)} ${m.text}`)}</title></line>`;
	}
	const visible = (h) => h.filter((p) => p.t <= cursor);
	s += `<path class="st-limit" d="${path(visible(results[0].history).map((p) => [x(p.t), ax.y(p.limit)]))}"/>`;
	for (const r of results) {
		const pts = visible(r.history).map((p) => [x(p.t), ax.y(sumRates(p))]);
		if (pts.length > 1) s += `<path class="st-series ${MODE_CLASS[r.mode]}" d="${path(pts)}"/>`;
		if (!done && pts.length) s += `<circle class="race-head ${MODE_CLASS[r.mode]}" cx="${pts.at(-1)[0]}" cy="${pts.at(-1)[1]}" r="3.5"/>`;
	}
	if (!done) s += `<line class="st-cursor" x1="${x(cursor)}" x2="${x(cursor)}" y1="${pad.t}" y2="${H - pad.b}"/>`;
	if (chart) svg(chart, W, H, s, `Race of ${results.length} modes on one seed, at ${clock(cursor)} of ${clock(T)}.`);

	if (legend) {
		legend.innerHTML =
			results.map((r) => `<span><i class="k-line ${MODE_CLASS[r.mode]}"></i>${MODES[r.mode].label}</span>`).join('') +
			`<span><i class="k-dash"></i>Hidden quota</span>${plan.hasWindow ? '<span><i class="k-drop"></i>The event</span>' : ''}`;
	}

	if (board) {
		const rows = results.map((r) => ({ r, p: at(r.history, cursor).total }));
		const top = Math.max(1, ...results.map((r) => r.history.at(-1).total.accepted));
		const leader = (get, dir) => {
			const values = rows.map(({ p }) => get(p));
			const best = dir > 0 ? Math.max(...values) : Math.min(...values);
			return values.filter((v) => v === best).length < values.length ? best : null;
		};
		const most = leader((p) => p.accepted, 1);
		const fewest = leader((p) => p.n429, -1);
		const badge = (on, text) => (on ? ` <span class="race-badge">${text}</span>` : '');
		board.innerHTML =
			`<p class="race-clock">${done ? 'Finish' : 'Race'} <b>${clock(Math.min(cursor, T))}</b> / ${clock(T)}. Bars: units that the API accepted.</p>` +
			rows
				.map(
					({ r, p }) =>
						`<div class="race-row"><span class="race-name"><i class="k-line ${MODE_CLASS[r.mode]}"></i>${MODES[r.mode].label}</span>` +
						`<span class="race-track"><span class="race-bar ${MODE_CLASS[r.mode]}" style="width:${((100 * p.accepted) / top).toFixed(1)}%"></span></span>` +
						`<span class="race-num">${fmt(p.accepted)} units${badge(done && p.accepted === most, 'most')}<br><span class="${p.n429 > p.accepted / 10 ? 'race-hot' : ''}">${fmt(p.n429)} throttled</span>${badge(done && p.n429 === fewest, 'fewest')}, ${fmt(p.lost)} lost</span></div>`,
				)
				.join('');
	}

	if (!table) return;
	if (!done) {
		table.innerHTML = '';
		return;
	}
	const columns = [
		['Throughput', (r) => r.throughput, (v) => `${fmt(v)}/min`, 'high'],
		['Share of quota', (r) => r.share, (v) => `${Math.round(v * 100)}%`, 'high'],
		['429s', (r) => r.n429, fmt, 'low'],
		['Rejected', (r) => r.rejected, fmt, 'low'],
		['Gave up', (r) => r.gaveUp, fmt, 'low'],
		['p95 wait', (r) => r.p95, (v) => `${v.toFixed(1)} s`, 'low'],
	];
	if (plan.hasWindow) columns.push(['Recovery', (r) => r.recovery ?? Infinity, (v) => (v === Infinity ? 'not in run' : `${v} s`), 'low']);
	let t = `<thead><tr><th scope="col">Mode</th>${columns.map(([name]) => `<th scope="col">${name}</th>`).join('')}</tr></thead><tbody>`;
	const best = columns.map(([, get, , dir]) => {
		const values = results.map(get);
		return dir === 'high' ? Math.max(...values) : Math.min(...values);
	});
	for (const r of results) {
		t += `<tr><th scope="row"><i class="k-line ${MODE_CLASS[r.mode]}"></i>${MODES[r.mode].label}</th>`;
		columns.forEach(([, get, format], i) => {
			const v = get(r);
			const win = results.length > 1 && v === best[i] && results.filter((o) => get(o) === v).length < results.length;
			t += `<td class="num${win ? ' best' : ''}">${format(v)}</td>`;
		});
		t += '</tr>';
	}
	table.innerHTML = `${t}</tbody>`;
}
