import { COMPARISON, MODES, race, Simulation, STORIES, storyPlan, withDefaults } from './engine.js';
import { axes, clock, esc, fmt, INSTANCE_CLASS, path, sumRates, svg } from './draw.js';
import { drawRace } from './race.js';
import { decodeState, encodeState, FIELDS, getOption, setOption, toAppSettings, toCSharp, validate } from './options.js';

const HISTORY = 300;
const RACE_SECONDS = 20;

export function mountAll(root = document) {
	for (const el of root.querySelectorAll('[data-rate-sim]:not([data-mounted])')) {
		el.dataset.mounted = 'true';
		mount(el);
	}
}

function mount(el) {
	const config = JSON.parse(el.dataset.config);
	const $ = (role) => el.querySelector(`[data-role="${role}"]`);
	const tools = Boolean(config.tools);
	const controls = {
		mode: $('mode'),
		instances: $('instances'),
		quota: $('quota'),
		demand: $('demand'),
		speed: $('speed'),
		story: $('story'),
		seed: $('seed'),
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
	let hits = [];
	let selected = null;
	let raceState = null;

	if (tools) {
		const saved = decodeState(location.hash);
		if (saved) {
			for (const key of ['mode', 'instances', 'quota', 'demand', 'seed', 'story']) {
				if (saved[key] != null && controls[key]) controls[key].value = String(saved[key]);
			}
			options = { ...options, ...saved.options };
		}
	}

	const story = () => controls.story?.value ?? config.story ?? 'free';
	const raceStory = () => (controls.story ? story() : 'neighbor');
	const seed = () => (controls.seed ? Math.max(1, Number(controls.seed.value) | 0) : COMPARISON.seed);
	const world = () => ({
		mode: controls.mode?.value ?? config.mode,
		instances: Number(controls.instances?.value ?? config.instances),
		quota: Number(controls.quota?.value ?? config.quota),
		demand: Number(controls.demand?.value ?? config.demand),
		options,
	});
	const state = () => ({ ...world(), seed: seed(), story: story() });

	function reset() {
		const runSeed = controls.seed ? seed() : (Date.now() & 0xffff) + 1;
		sim = new Simulation({ ...world(), seed: runSeed, story: story(), historyMax: HISTORY });
		selected = null;
		hideTip();
		render();
		changed();
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
		while (acc >= sim.world.dt && steps < 2400) {
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
		if (controls.instances && Number(controls.instances.value) !== sim.clients.length) controls.instances.value = String(sim.clients.length);
		drawPipeline($('pipeline'), sim, width);
		drawTiles(el, sim);
		hits = drawRateChart($('chart'), sim, width);
		drawLegend($('legend'), sim);
		const log = $('log');
		if (log) log.textContent = sim.marks.slice(-5).map((m) => `${clock(m.t)}  ${m.text}`).join('\n');
		const status = $('story-status');
		if (status) {
			const s = sim.story;
			status.innerHTML = s && s.events.length ? `<b>${esc(s.label)}.</b> ${esc(s.text)} <span class="sim-clock">${clock(sim.t)}${sim.t < s.duration ? ` / ${clock(s.duration)}` : ''}</span>` : '';
		}
		if (selected) showTip(selected);
	}

	/* Event marks: hover, tap or arrow keys show the rule that fired. */
	const chartEl = $('chart');
	const tip = $('tip');

	function hideTip() {
		if (tip) tip.hidden = true;
	}

	function showTip(mark) {
		const hit = hits.find((h) => h.mark === mark);
		if (!tip || !hit) {
			selected = null;
			hideTip();
			return;
		}
		const m = hit.mark;
		let extra = '';
		if (m.kind === 'cut') {
			const { cooldown, stale } = m.ignored;
			const parts = [];
			if (cooldown) parts.push(`${cooldown} came in the DecreaseCooldown`);
			if (stale) parts.push(`${stale} were for requests sent before this cut`);
			if (parts.length) extra = `<p>${cooldown + stale} more throttles did not cut again: ${parts.join(', ')}.</p>`;
		} else if (m.kind === 'pause' && m.count > 1) {
			extra = `<p>${m.count} throttled responses made the pause longer, to ${clock(m.until)}.</p>`;
		}
		const who = m.instance != null ? `<i class="k-line ${INSTANCE_CLASS[m.instance]}"></i>` : '';
		tip.innerHTML = `<p class="tip-head">${who}${clock(m.t)} ${esc(m.text)}</p><p>${esc(m.detail)}</p>${extra}`;
		tip.hidden = false;
		const rect = chartEl.getBoundingClientRect();
		const scale = rect.width / Math.max(1, Number(chartEl.getAttribute('width')));
		const x = hit.x * scale;
		const box = tip.offsetWidth;
		tip.style.left = `${Math.max(0, Math.min(rect.width - box, x - box / 2))}px`;
		tip.style.top = `${hit.y * scale + 12}px`;
	}

	function nearest(evt) {
		const rect = chartEl.getBoundingClientRect();
		const scale = Number(chartEl.getAttribute('width')) / Math.max(1, rect.width);
		const px = (evt.clientX - rect.left) * scale;
		const py = (evt.clientY - rect.top) * scale;
		let best = null;
		let dist = 16 * scale;
		for (const h of hits) {
			const d = Math.hypot(h.x - px, (h.y - py) * 0.5);
			if (d < dist) {
				dist = d;
				best = h.mark;
			}
		}
		return best;
	}

	let pinned = false;
	chartEl?.addEventListener('pointermove', (evt) => {
		if (pinned) return;
		const mark = nearest(evt);
		selected = mark;
		if (mark) showTip(mark);
		else hideTip();
	});
	chartEl?.addEventListener('pointerleave', () => {
		if (pinned) return;
		selected = null;
		hideTip();
	});
	chartEl?.addEventListener('pointerdown', (evt) => {
		const mark = nearest(evt);
		pinned = Boolean(mark);
		selected = mark;
		if (mark) showTip(mark);
		else hideTip();
	});
	chartEl?.addEventListener('keydown', (evt) => {
		if (!hits.length) return;
		if (evt.key === 'Escape') {
			selected = null;
			pinned = false;
			hideTip();
			return;
		}
		if (evt.key !== 'ArrowLeft' && evt.key !== 'ArrowRight') return;
		evt.preventDefault();
		const marks = hits.map((h) => h.mark);
		const i = marks.indexOf(selected);
		const next = i < 0 ? marks.length - 1 : Math.max(0, Math.min(marks.length - 1, i + (evt.key === 'ArrowRight' ? 1 : -1)));
		selected = marks[next];
		pinned = true;
		showTip(selected);
	});
	chartEl?.addEventListener('blur', () => {
		pinned = false;
		selected = null;
		hideTip();
	});

	function showValues() {
		if (controls.quotaValue) controls.quotaValue.textContent = fmt(Number(controls.quota.value));
		if (controls.demandValue) controls.demandValue.textContent = fmt(Number(controls.demand.value));
	}

	/* The race: all modes run on the same seed and story, then replay in step. */
	function runRace(animate) {
		const target = $('compare');
		if (!target) return;
		const base = world();
		const key = raceStory();
		const runSeed = seed();
		const modes = config.compareModes ?? Object.keys(MODES);
		const results = race({ instances: base.instances, quota: base.quota, demand: base.demand, options }, key, runSeed, modes);
		const plan = storyPlan(key, runSeed);
		if (raceState?.raf) cancelAnimationFrame(raceState.raf);
		raceState = { results, plan, cursor: animate ? 0 : plan.duration, raf: 0, last: 0 };
		const note = $('compare-note');
		if (note) {
			const s = STORIES[key];
			note.textContent = `${s.label}: ${s.text} ${base.instances} instance(s), quota ${fmt(base.quota)} units/min, demand ${fmt(base.demand)} per instance, ${clock(plan.duration)} min, seed ${runSeed}. Model numbers.`;
		}
		drawRace(target, results, plan, raceState.cursor, width);
		if (raceState.cursor < plan.duration) raceState.raf = requestAnimationFrame(raceFrame);
	}

	function raceFrame(ts) {
		const r = raceState;
		const dt = r.last ? Math.min(0.1, (ts - r.last) / 1000) : 0;
		r.last = ts;
		r.cursor = Math.min(r.plan.duration, r.cursor + (dt * r.plan.duration) / RACE_SECONDS);
		drawRace($('compare'), r.results, r.plan, r.cursor, width);
		r.raf = r.cursor < r.plan.duration ? requestAnimationFrame(raceFrame) : 0;
	}

	/* Tools: the options editor, the export and the link. */
	function changed() {
		if (!tools) return;
		drawOptions();
		drawExport();
		clearTimeout(changed.timer);
		changed.timer = setTimeout(() => history.replaceState(null, '', encodeState(state())), 200);
	}

	const optionsEl = $('options');
	function buildOptions() {
		if (!optionsEl) return;
		const groups = [
			['Rates, units per minute for the whole budget', (f) => f.key.includes('UnitsPerMinute')],
			['Timing and queue (time in seconds)', (f) => !f.key.includes('UnitsPerMinute') && !f.controller],
			['AIMD', (f) => f.controller === 'aimd'],
			['CUBIC', (f) => f.controller === 'cubic'],
		];
		optionsEl.innerHTML = groups
			.map(
				([title, test]) =>
					`<fieldset><legend>${title}</legend>${FIELDS.filter(test)
						.map(
							(f) =>
								`<label><span>${f.name}</span><input type="number" inputmode="decimal" data-opt="${f.key}" step="${f.step}" min="${f.min}"${f.max ? ` max="${f.max}"` : ''}></label>`,
						)
						.join('')}</fieldset>`,
			)
			.join('');
		optionsEl.addEventListener('change', (evt) => {
			const input = evt.target.closest('[data-opt]');
			if (!input) return;
			const field = FIELDS.find((f) => f.key === input.dataset.opt);
			let value = Number(input.value);
			if (input.value === '' || !Number.isFinite(value)) value = getOption(withDefaults({}), field.key);
			if (field.int) value = Math.round(value);
			applyOptions(setOption(options, field.key, value));
		});
	}

	function applyOptions(next) {
		const errors = validate(next);
		const box = $('options-error');
		if (box) box.textContent = errors.join(' ');
		if (errors.length) {
			drawOptions(next);
			return;
		}
		options = next;
		reset();
	}

	function drawOptions(shown = options) {
		if (!optionsEl) return;
		const o = withDefaults(shown);
		const d = withDefaults({});
		for (const input of optionsEl.querySelectorAll('[data-opt]')) {
			const key = input.dataset.opt;
			const v = getOption(o, key);
			if (document.activeElement !== input) input.value = String(v);
			input.closest('label').classList.toggle('changed', v !== getOption(d, key));
		}
	}

	let exportKind = 'json';
	function drawExport() {
		const code = $('export-code');
		if (!code) return;
		const w = world();
		const args = { mode: w.mode, instances: w.instances, options };
		code.textContent = exportKind === 'json' ? toAppSettings(args) : toCSharp(args);
		const note = $('export-note');
		if (note) {
			note.textContent =
				w.mode === 'aimd' || w.mode === 'cubic'
					? 'The rates and the options that differ from the defaults. Set InstanceCount for each deployment.'
					: `The live mode is ${MODES[w.mode].label}, which is not a controller. The export uses AIMD.`;
		}
		for (const b of el.querySelectorAll('[data-export]')) b.setAttribute('aria-pressed', String(b.dataset.export === exportKind));
	}

	async function copy(text, button) {
		const before = button.textContent;
		try {
			await navigator.clipboard.writeText(text);
			button.textContent = 'Copied';
		} catch {
			button.textContent = 'Copy failed';
		}
		setTimeout(() => (button.textContent = before), 1500);
	}

	function applyPreset(preset) {
		if (preset.options) options = { ...(config.options ?? {}), ...preset.options };
		for (const key of ['mode', 'instances', 'quota', 'demand', 'story']) {
			if (preset[key] != null && controls[key]) controls[key].value = String(preset[key]);
		}
		showValues();
		if (preset.reset || preset.mode || preset.options || preset.story) reset();
		else if (sim) {
			sim.setInstances(world().instances);
			sim.setQuota(world().quota);
			sim.setDemand(world().demand);
			changed();
		}
		if (preset.drop && sim) sim.quotaDrop();
		if ($('compare')) runRace(false);
		render();
		if (live) setPlaying(true);
	}

	controls.play?.addEventListener('click', () => setPlaying(!running));
	controls.reset?.addEventListener('click', reset);
	controls.drop?.addEventListener('click', () => {
		sim.quotaDrop();
		render();
	});
	controls.mode?.addEventListener('change', reset);
	controls.story?.addEventListener('change', reset);
	controls.seed?.addEventListener('change', reset);
	$('new-seed')?.addEventListener('click', () => {
		controls.seed.value = String(1 + Math.floor(Math.random() * 99999));
		reset();
	});
	controls.instances?.addEventListener('change', () => {
		sim?.setInstances(world().instances);
		render();
		changed();
	});
	controls.quota?.addEventListener('input', () => {
		showValues();
		sim?.setQuota(world().quota);
		render();
		changed();
	});
	controls.demand?.addEventListener('input', () => {
		showValues();
		sim?.setDemand(world().demand);
		render();
		changed();
	});
	if (!live && $('compare')) {
		for (const c of [controls.story, controls.instances, controls.quota, controls.demand]) c?.addEventListener('change', () => runRace(false));
	}
	$('compare-run')?.addEventListener('click', () => runRace(true));
	$('compare-skip')?.addEventListener('click', () => {
		if (raceState && raceState.cursor < raceState.plan.duration) {
			cancelAnimationFrame(raceState.raf);
			raceState.cursor = raceState.plan.duration;
			drawRace($('compare'), raceState.results, raceState.plan, raceState.cursor, width);
		} else {
			runRace(false);
		}
	});
	$('options-reset')?.addEventListener('click', () => applyOptions({ ...(config.options ?? {}) }));
	for (const b of el.querySelectorAll('[data-export]')) {
		b.addEventListener('click', () => {
			exportKind = b.dataset.export;
			drawExport();
		});
	}
	$('export-copy')?.addEventListener('click', (evt) => copy($('export-code').textContent, evt.currentTarget));
	$('copy-link')?.addEventListener('click', (evt) => {
		history.replaceState(null, '', encodeState(state()));
		copy(location.href, evt.currentTarget);
	});
	for (const button of el.querySelectorAll('[data-preset]')) {
		button.addEventListener('click', () => applyPreset(JSON.parse(button.dataset.preset)));
	}

	new IntersectionObserver((entries) => {
		visible = entries.some((e) => e.isIntersecting);
		if (visible && !raceState && $('compare') && config.autoCompare !== false) runRace(false);
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
				if (raceState) drawRace($('compare'), raceState.results, raceState.plan, raceState.cursor, width);
			}
		}, 80);
	}).observe(el);

	buildOptions();
	showValues();
	setPlaying(running);
	if (live) reset();
	else changed();
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
		const status = mode === 'none' ? 'no limit' : paused ? `paused ${Math.ceil(bucket.pausedUntil - now)} s` : `queue ${queue.length}`;
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
		if (hot) s += `<text class="t-small f-red" x="${ax + 4}" y="${cy - 6}">${now < sim.server.outageUntil ? '503' : '429'}</text>`;
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
	if (now < sim.server.outageUntil) {
		s += `<text class="t-small f-red" x="${serverX + 10}" y="${top + 108}">down ${Math.ceil(sim.server.outageUntil - now)} s</text>`;
	} else if (now < sim.server.dropUntil) {
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

/** Draws the live chart and returns the mark positions, for the tooltips. */
function drawRateChart(target, sim, width) {
	if (!target) return [];
	const W = Math.max(300, width);
	const H = 230;
	const pad = { l: 40, r: 8, t: 30, b: 34 };
	const h = sim.history;
	const quota = sim.world.quota;
	const ymax = Math.max(quota * 1.35, ...h.map((p) => Math.min(sumRates(p), quota * 1.8))) * 1.05;
	const span = HISTORY;
	const t0 = Math.max(0, (h.at(-1)?.t ?? 0) - span);
	const tEnd = t0 + span;
	const x = (t) => pad.l + ((t - t0) / span) * (W - pad.l - pad.r);
	const ticks = [];
	for (let t = Math.ceil(t0 / 60) * 60; t <= tEnd; t += 60) ticks.push([x(t), `${t / 60}m`]);
	const ax = axes(W, H, pad, ymax, 'units per minute over the last 5 sim minutes', ticks);
	let s = ax.s;
	const hits = [];
	if (h.length > 1) {
		const area = h.map((p) => [x(p.t), ax.y(p.used)]);
		s += `<path class="f-used" d="${path(area)}L${x(h.at(-1).t)} ${ax.y(0)}L${x(h[0].t)} ${ax.y(0)}Z"/>`;
		s += `<path class="st-limit" d="${path(h.map((p) => [x(p.t), ax.y(p.limit)]))}"/>`;
		if (h[0].rates.length > 1 || sim.clients.length > 1) {
			s += `<path class="st-sum" d="${path(h.map((p) => [x(p.t), ax.y(sumRates(p))]))}"/>`;
		}
		const lanes = Math.max(...h.map((p) => p.rates.length));
		for (let k = 0; k < lanes; k++) {
			const pts = h.filter((p) => p.rates[k] != null).map((p) => [x(p.t), ax.y(p.rates[k])]);
			if (pts.length > 1) s += `<path class="st-series ${INSTANCE_CLASS[k]}" d="${path(pts)}"/>`;
		}
		for (const p of h) if (p.n429) s += `<rect class="f-red" x="${x(p.t) - 1}" y="${ax.y(0) - 6}" width="2" height="6"/>`;
	}
	for (const m of sim.marks) {
		if (m.t < t0 || m.t > tEnd) continue;
		const mx = x(m.t);
		const cls = m.instance != null ? INSTANCE_CLASS[m.instance] : '';
		if (m.kind === 'story') {
			s += `<line class="st-event" x1="${mx}" x2="${mx}" y1="${pad.t - 10}" y2="${H - pad.b}"/>`;
			s += `<circle class="mk-story" cx="${mx}" cy="${pad.t - 14}" r="5"/>`;
			hits.push({ x: mx, y: pad.t - 14, mark: m });
		} else if (m.kind === 'cut') {
			const my = ax.y(m.rate);
			s += `<path class="mk ${cls}" d="M${mx - 5} ${my - 9}L${mx + 5} ${my - 9}L${mx} ${my - 1}Z"/>`;
			hits.push({ x: mx, y: my - 5, mark: m });
		} else if (m.kind === 'pause') {
			const py = pad.t - 4 - (m.instance ?? 0) * 5;
			const end = x(Math.min(m.until, tEnd));
			s += `<rect class="mk-pause" x="${mx}" y="${py - 3}" width="${Math.max(2, end - mx)}" height="3"/>`;
			hits.push({ x: mx, y: py, mark: m });
		} else if (m.kind === 'max') {
			const my = ax.y(m.rate);
			s += `<path class="mk-max ${cls}" d="M${mx - 5} ${my - 2}L${mx + 5} ${my - 2}L${mx} ${my - 10}Z"/>`;
			hits.push({ x: mx, y: my - 6, mark: m });
		}
	}
	svg(target, W, H, s, `Rate chart: ${MODES[sim.mode].label}, hidden quota ${fmt(quota)} units per minute. Use the arrow keys to read the events.`);
	return hits;
}

function drawLegend(target, sim) {
	if (!target) return;
	const label = sim.mode === 'none' ? 'send rate' : 'rate';
	let s = sim.clients.map((_, i) => `<span><i class="k-line ${INSTANCE_CLASS[i]}"></i>Instance ${i + 1} ${label}</span>`).join('');
	if (sim.clients.length > 1) s += `<span><i class="k-line k-sum"></i>Sum</span>`;
	s += `<span><i class="k-dash"></i>Hidden quota</span><span><i class="k-area"></i>Units used, last 60 s</span><span><i class="k-tick"></i>Throttle in that second</span>`;
	if (sim.mode === 'aimd' || sim.mode === 'cubic') {
		s += `<span><i class="k-cut"></i>Cut</span><span><i class="k-pause"></i>Pause</span><span><i class="k-max"></i>At maximum</span>`;
	}
	s += `<span><i class="k-event"></i>Event</span><span class="sim-hint">Point at a mark, or focus the chart and press the arrow keys, to see the rule.</span>`;
	target.innerHTML = s;
}
