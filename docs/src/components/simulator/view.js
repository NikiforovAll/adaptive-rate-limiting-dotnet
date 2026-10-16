import { compare, COMPARISON, MODES, Simulation } from './engine.js';

const SVG = 'http://www.w3.org/2000/svg';
const HISTORY = 300;
const fmt = (v) => Math.round(v).toLocaleString('en-US');
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

const INSTANCE_CLASS = ['s-i1', 's-i2', 's-i3'];
const MODE_CLASS = { none: 's-none', fixed: 's-fixed', aimd: 's-aimd', cubic: 's-cubic' };

export function mountAll(root = document) {
	for (const el of root.querySelectorAll('[data-rate-sim]:not([data-mounted])')) {
		el.dataset.mounted = 'true';
		mount(el);
	}
}

function mount(el) {
	const config = JSON.parse(el.dataset.config);
	const $ = (role) => el.querySelector(`[data-role="${role}"]`);
	const controls = {
		mode: $('mode'),
		instances: $('instances'),
		quota: $('quota'),
		demand: $('demand'),
		speed: $('speed'),
		play: $('play'),
		reset: $('reset'),
		drop: $('drop'),
		quotaValue: $('quota-value'),
		demandValue: $('demand-value'),
	};
	const live = $('live');
	const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
	let options = { ...(config.options ?? {}) };
	let sim = null;
	let running = !reducedMotion;
	let visible = false;
	let raf = 0;
	let last = 0;
	let acc = 0;
	let width = el.clientWidth || 700;
	let lastComparison = null;

	const world = () => ({
		mode: controls.mode?.value ?? config.mode,
		instances: Number(controls.instances?.value ?? config.instances),
		quota: Number(controls.quota?.value ?? config.quota),
		demand: Number(controls.demand?.value ?? config.demand),
		options,
	});

	function reset() {
		sim = new Simulation({ ...world(), seed: (Date.now() & 0xffff) + 1, historyMax: HISTORY });
		render();
	}

	function setPlaying(value) {
		running = value;
		if (controls.play) {
			controls.play.textContent = running ? 'Pause' : 'Play';
			controls.play.setAttribute('aria-pressed', String(running));
		}
		schedule();
	}

	function schedule() {
		if (live && visible && running && !raf) {
			last = performance.now();
			raf = requestAnimationFrame(frame);
		}
	}

	let lastDraw = 0;
	function frame(ts) {
		raf = 0;
		if (!visible || !running) return;
		const dt = Math.min(0.25, (ts - last) / 1000);
		last = ts;
		acc += dt * Number(controls.speed?.value ?? 4);
		let steps = 0;
		while (acc >= sim.world.dt && steps < 600) {
			sim.step();
			acc -= sim.world.dt;
			steps++;
		}
		if (ts - lastDraw > 90) {
			render();
			lastDraw = ts;
		}
		raf = requestAnimationFrame(frame);
	}

	function render() {
		if (!live || !sim) return;
		drawPipeline($('pipeline'), sim, width);
		drawTiles(el, sim);
		drawRateChart($('chart'), sim, width);
		drawLegend($('legend'), sim);
		const log = $('log');
		if (log) log.textContent = sim.events.map((e) => `t=${Math.round(e.t)} s  ${e.text}`).join('\n');
	}

	function showValues() {
		if (controls.quotaValue) controls.quotaValue.textContent = fmt(Number(controls.quota.value));
		if (controls.demandValue) controls.demandValue.textContent = fmt(Number(controls.demand.value));
	}

	function runComparison() {
		const target = $('compare');
		if (!target) return;
		const base = world();
		const modes = config.compareModes ?? Object.keys(MODES);
		const results = compare({ instances: base.instances, quota: base.quota, demand: base.demand, options }, modes);
		lastComparison = [results, base];
		drawComparison(target, results, base, width);
		const note = $('compare-note');
		if (note) {
			note.textContent = `${base.instances} instance(s), quota ${fmt(base.quota)} units/min, demand ${fmt(base.demand)} per instance, ${COMPARISON.duration / 60} min, seed ${COMPARISON.seed}. Model numbers.`;
		}
	}

	function applyPreset(preset) {
		if (preset.options) options = { ...(config.options ?? {}), ...preset.options };
		for (const key of ['mode', 'instances', 'quota', 'demand']) {
			if (preset[key] != null && controls[key]) controls[key].value = String(preset[key]);
		}
		showValues();
		if (preset.reset || preset.mode || preset.options) reset();
		else if (sim) {
			sim.setInstances(world().instances);
			sim.setQuota(world().quota);
			sim.setDemand(world().demand);
		}
		if (preset.drop && sim) sim.quotaDrop();
		if ($('compare')) runComparison();
		render();
		if (live) setPlaying(true);
	}

	controls.play?.addEventListener('click', () => setPlaying(!running));
	controls.reset?.addEventListener('click', reset);
	controls.drop?.addEventListener('click', () => {
		sim.quotaDrop();
		render();
	});
	controls.mode?.addEventListener('change', () => {
		reset();
	});
	controls.instances?.addEventListener('change', () => {
		sim?.setInstances(world().instances);
		render();
	});
	controls.quota?.addEventListener('input', () => {
		showValues();
		sim?.setQuota(world().quota);
		render();
	});
	controls.demand?.addEventListener('input', () => {
		showValues();
		sim?.setDemand(world().demand);
		render();
	});
	$('compare-run')?.addEventListener('click', runComparison);
	for (const button of el.querySelectorAll('[data-preset]')) {
		button.addEventListener('click', () => applyPreset(JSON.parse(button.dataset.preset)));
	}

	new IntersectionObserver((entries) => {
		visible = entries.some((e) => e.isIntersecting);
		if (visible && !lastComparison && $('compare') && config.autoCompare !== false) runComparison();
		if (visible) schedule();
	}).observe(el);

	let resizeTimer = 0;
	new ResizeObserver(() => {
		clearTimeout(resizeTimer);
		resizeTimer = setTimeout(() => {
			const next = Math.round(el.clientWidth);
			if (next && next !== width) {
				width = next;
				render();
				if (lastComparison) drawComparison($('compare'), ...lastComparison, width);
			}
		}, 80);
	}).observe(el);

	showValues();
	setPlaying(running);
	if (live) reset();
}

function svg(target, viewWidth, height, body, label) {
	target.setAttribute('viewBox', `0 0 ${viewWidth} ${height}`);
	target.setAttribute('width', String(viewWidth));
	target.setAttribute('height', String(height));
	if (label) target.setAttribute('aria-label', label);
	target.innerHTML = body;
}

function drawPipeline(target, sim, width) {
	if (!target) return;
	const W = Math.max(300, width);
	const narrow = W < 560;
	const rowH = 66;
	const top = 22;
	const labelW = narrow ? 92 : 132;
	const serverW = narrow ? 104 : 172;
	const serverX = W - serverW;
	const bucketX = serverX - (narrow ? 64 : 92);
	const queueX = labelW;
	const queueW = bucketX - 10 - queueX;
	const n = sim.clients.length;
	const H = top + Math.max(n * rowH, 132) + 4;
	const now = sim.t;
	const mode = sim.mode;
	let s = '';
	s += `<defs><marker id="sim-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path class="f-muted" d="M0 0L10 5L0 10z"/></marker>`;
	s += `<marker id="sim-arrow-hot" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path class="f-red" d="M0 0L10 5L0 10z"/></marker></defs>`;
	s += `<text class="t-small" x="${queueX}" y="12">${narrow ? 'Queue (head right)' : 'FIFO queue, head on the right'}</text>`;
	if (mode !== 'none') s += `<text class="t-small" x="${bucketX}" y="12">Bucket</text>`;
	s += `<text class="t-small" x="${serverX}" y="12">API, last 60 s</text>`;

	sim.clients.forEach((client, i) => {
		const y = top + i * rowH;
		const cy = y + 30;
		const queue = sim.queueOf(client);
		const bucket = sim.bucketOf(client);
		const paused = bucket && bucket.isPaused(now);
		const rate = sim.rateOf(client);
		const status =
			mode === 'none'
				? 'no limit'
				: paused
					? `paused ${Math.ceil(bucket.pausedUntil - now)} s`
					: `queue ${queue.length}`;
		s += `<text class="t-head" x="0" y="${y + 18}"><tspan class="${INSTANCE_CLASS[i]} f-series">&#9679;</tspan> Instance ${i + 1}</text>`;
		s += `<text class="t-small" x="0" y="${y + 36}">${mode === 'none' ? 'sends' : 'rate'} ${fmt(rate)}/min</text>`;
		s += `<text class="t-small${paused ? ' f-red' : ''}" x="0" y="${y + 52}">${status}</text>`;
		s += `<rect class="box" x="${queueX}" y="${cy - 16}" width="${queueW}" height="32" rx="4"/>`;
		let x = queueX + queueW - 4;
		for (let k = 0; k < queue.length; k++) {
			const c = queue[k].cost;
			const w = 2 + c * 3;
			if (x - w < queueX + 34) {
				s += `<text class="t-small" x="${queueX + 5}" y="${cy + 4}">+${queue.length - k}</text>`;
				break;
			}
			x -= w;
			s += `<rect class="q-c${c}" x="${x}" y="${cy - 10}" width="${w}" height="20" rx="1.5"/>`;
			x -= 2;
		}
		if (bucket) {
			const frac = Math.max(0, Math.min(1, bucket.tokens / bucket.capacity));
			s += `<rect class="box" x="${bucketX}" y="${cy - 22}" width="28" height="44" rx="4"/>`;
			s += `<rect class="f-tokens" x="${bucketX + 2}" y="${cy + 20 - 40 * frac}" width="24" height="${40 * frac}" rx="2"/>`;
			if (paused) s += `<line class="st-red" x1="${bucketX + 4}" y1="${cy - 18}" x2="${bucketX + 24}" y2="${cy + 18}"/>`;
		}
		const hot = now < client.flashUntil;
		const ax = bucket ? bucketX + 32 : queueX + queueW + 4;
		s += `<line class="${hot ? 'st-red' : 'st-muted'}" x1="${ax}" y1="${cy}" x2="${serverX - 6}" y2="${cy}" marker-end="url(#${hot ? 'sim-arrow-hot' : 'sim-arrow'})"/>`;
		if (hot) s += `<text class="t-small f-red" x="${ax + 4}" y="${cy - 6}">429</text>`;
	});

	const limit = sim.server.limit(now);
	const used = sim.server.used;
	const frac = Math.min(1, used / Math.max(1, limit));
	const meter = used > limit * 0.999 ? 'm-over' : used > limit * 0.9 ? 'm-near' : 'm-ok';
	const gh = Math.max(n * rowH, 132) - 6;
	const mw = serverW - 20;
	s += `<rect class="box" x="${serverX}" y="${top}" width="${serverW}" height="${gh}" rx="6"/>`;
	s += `<text class="t-head" x="${serverX + 10}" y="${top + 20}">API</text>`;
	s += `<text class="t-small" x="${serverX + 10}" y="${top + 36}">${narrow ? 'shared quota' : 'one quota for all instances'}</text>`;
	s += `<rect class="box" x="${serverX + 10}" y="${top + 46}" width="${mw}" height="12" rx="3"/>`;
	s += `<rect class="${meter}" x="${serverX + 10}" y="${top + 46}" width="${mw * frac}" height="12" rx="3"/>`;
	s += `<text class="t-small" x="${serverX + 10}" y="${top + 76}">used ${fmt(used)}</text>`;
	s += `<text class="t-small" x="${serverX + 10}" y="${top + 92}">quota ${fmt(limit)}${narrow ? '' : ' (hidden)'}</text>`;
	if (now < sim.server.dropUntil) {
		s += `<text class="t-small f-red" x="${serverX + 10}" y="${top + 108}">${narrow ? 'neighbor' : 'noisy neighbor'} ${Math.ceil(sim.server.dropUntil - now)} s</text>`;
	}
	svg(target, W, H, s, `Live view at t = ${Math.round(now)} s: ${n} instances, ${fmt(used)} of ${fmt(limit)} units used in the last 60 seconds.`);
}

function drawTiles(el, sim) {
	const snap = sim.snapshot();
	const set = (role, text) => {
		const node = el.querySelector(`[data-tile="${role}"]`);
		if (node) node.textContent = text;
	};
	set('used', `${fmt(snap.used)} / ${fmt(snap.limit)}`);
	set('rate', sim.mode === 'none' ? `${fmt(snap.sumRate)} sent` : fmt(snap.sumRate));
	set('throttled', fmt(snap.n429));
	set('queue', fmt(snap.queue));
	set('wait', `${snap.p95.toFixed(1)} s`);
	set('rejected', fmt(snap.rejected + snap.gaveUp));
}

function axes(W, H, pad, ymax, xLabel, ticks) {
	const y = (v) => pad.t + (H - pad.t - pad.b) * (1 - Math.min(v, ymax) / ymax);
	const raw = ymax / 4;
	const mag = Math.pow(10, Math.floor(Math.log10(raw)));
	const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((v) => ymax / v <= 5);
	let s = '';
	for (let v = 0; v <= ymax + 1e-9; v += step) {
		s += `<line class="grid" x1="${pad.l}" x2="${W - pad.r}" y1="${y(v)}" y2="${y(v)}"/>`;
		s += `<text class="t-axis" x="${pad.l - 6}" y="${y(v) + 4}" text-anchor="end">${v >= 1000 ? `${v / 1000}k` : v}</text>`;
	}
	for (const [x, label] of ticks) s += `<text class="t-axis" x="${x}" y="${H - pad.b + 14}" text-anchor="middle">${label}</text>`;
	s += `<text class="t-axis" x="${pad.l}" y="${H - 2}">${esc(xLabel)}</text>`;
	return { s, y };
}

function path(points) {
	let d = '';
	for (let i = 0; i < points.length; i++) d += `${i ? 'L' : 'M'}${points[i][0].toFixed(1)} ${points[i][1].toFixed(1)}`;
	return d;
}

function drawRateChart(target, sim, width) {
	if (!target) return;
	const W = Math.max(300, width);
	const H = 220;
	const pad = { l: 40, r: 8, t: 8, b: 34 };
	const h = sim.history;
	const quota = sim.world.quota;
	const ymax = Math.max(quota * 1.35, ...h.map((p) => Math.min(p.rates.reduce((a, b) => a + b, 0), quota * 1.8))) * 1.05;
	const span = HISTORY;
	const t0 = Math.max(0, (h.at(-1)?.t ?? 0) - span);
	const x = (t) => pad.l + ((t - t0) / span) * (W - pad.l - pad.r);
	const ticks = [];
	for (let t = Math.ceil(t0 / 60) * 60; t <= t0 + span; t += 60) ticks.push([x(t), `${t / 60}m`]);
	const ax = axes(W, H, pad, ymax, 'units per minute over the last 5 sim minutes', ticks);
	let s = ax.s;
	if (h.length > 1) {
		const area = h.map((p) => [x(p.t), ax.y(p.used)]);
		s += `<path class="f-used" d="${path(area)}L${x(h.at(-1).t)} ${ax.y(0)}L${x(h[0].t)} ${ax.y(0)}Z"/>`;
		s += `<path class="st-limit" d="${path(h.map((p) => [x(p.t), ax.y(p.limit)]))}"/>`;
		if (h[0].rates.length > 1 || sim.clients.length > 1) {
			s += `<path class="st-sum" d="${path(h.map((p) => [x(p.t), ax.y(p.rates.reduce((a, b) => a + b, 0))]))}"/>`;
		}
		for (let k = 0; k < sim.clients.length; k++) {
			const pts = h.filter((p) => p.rates[k] != null).map((p) => [x(p.t), ax.y(p.rates[k])]);
			if (pts.length > 1) s += `<path class="st-series ${INSTANCE_CLASS[k]}" d="${path(pts)}"/>`;
		}
		for (const p of h) if (p.n429) s += `<rect class="f-red" x="${x(p.t) - 1}" y="${ax.y(0) - 6}" width="2" height="6"/>`;
	}
	svg(target, W, H, s, `Rate chart: ${MODES[sim.mode].label}, hidden quota ${fmt(quota)} units per minute.`);
}

function drawLegend(target, sim) {
	if (!target) return;
	const label = sim.mode === 'none' ? 'send rate' : 'rate';
	let s = sim.clients.map((_, i) => `<span><i class="k-line ${INSTANCE_CLASS[i]}"></i>Instance ${i + 1} ${label}</span>`).join('');
	if (sim.clients.length > 1) s += `<span><i class="k-line k-sum"></i>Sum</span>`;
	s += `<span><i class="k-dash"></i>Hidden quota</span><span><i class="k-area"></i>Units used, last 60 s</span><span><i class="k-tick"></i>429 in that second</span>`;
	target.innerHTML = s;
}

function drawComparison(target, results, base, width) {
	const chart = target.querySelector('[data-role="compare-chart"]');
	const legend = target.querySelector('[data-role="compare-legend"]');
	const table = target.querySelector('[data-role="compare-table"]');
	const W = Math.max(300, width);
	const H = 240;
	const pad = { l: 40, r: 8, t: 8, b: 34 };
	const T = COMPARISON.duration;
	const ymax = base.quota * 1.6;
	const x = (t) => pad.l + (t / T) * (W - pad.l - pad.r);
	const ticks = [];
	for (let t = 0; t <= T; t += 180) ticks.push([x(t), `${t / 60}m`]);
	const ax = axes(W, H, pad, ymax, 'sum of rates, units per minute (No limiter: send rate, clipped)', ticks);
	let s = `<rect class="f-drop" x="${x(COMPARISON.dropAt)}" y="${pad.t}" width="${x(COMPARISON.dropAt + COMPARISON.dropFor) - x(COMPARISON.dropAt)}" height="${H - pad.t - pad.b}"/>`;
	s += ax.s;
	const h0 = results[0].history;
	s += `<path class="st-limit" d="${path(h0.map((p) => [x(p.t), ax.y(p.limit)]))}"/>`;
	for (const r of results) {
		const pts = r.history.map((p) => [x(p.t), ax.y(p.rates.reduce((a, b) => a + b, 0))]);
		s += `<path class="st-series ${MODE_CLASS[r.mode]}" d="${path(pts)}"/>`;
	}
	if (chart) svg(chart, W, H, s, 'Comparison chart of all modes on one seed.');
	if (legend) {
		legend.innerHTML =
			results.map((r) => `<span><i class="k-line ${MODE_CLASS[r.mode]}"></i>${MODES[r.mode].label}</span>`).join('') +
			`<span><i class="k-dash"></i>Hidden quota</span><span><i class="k-drop"></i>Quota drop</span>`;
	}
	if (!table) return;
	const columns = [
		['Throughput', (r) => r.throughput, (v) => `${fmt(v)}/min`, 'high'],
		['Share of quota', (r) => r.share, (v) => `${Math.round(v * 100)}%`, 'high'],
		['429s', (r) => r.n429, fmt, 'low'],
		['Rejected', (r) => r.rejected, fmt, 'low'],
		['Gave up', (r) => r.gaveUp, fmt, 'low'],
		['p95 wait', (r) => r.p95, (v) => `${v.toFixed(1)} s`, 'low'],
		['Recovery', (r) => r.recovery ?? Infinity, (v) => (v === Infinity ? 'not in run' : `${v} s`), 'low'],
	];
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
