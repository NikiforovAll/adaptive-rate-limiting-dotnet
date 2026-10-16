import { DEFAULT_OPTIONS, MAX_INSTANCES, MODES, STORIES, withDefaults } from './engine.js';

/** The options a reader can edit, with their C# names. `time` options are TimeSpan in C#. */
export const FIELDS = Object.freeze([
	{ key: 'initialUnitsPerMinute', name: 'InitialUnitsPerMinute', step: 50, min: 1 },
	{ key: 'minUnitsPerMinute', name: 'MinUnitsPerMinute', step: 50, min: 1 },
	{ key: 'maxUnitsPerMinute', name: 'MaxUnitsPerMinute', step: 50, min: 1 },
	{ key: 'decreaseCooldown', name: 'DecreaseCooldown', time: true, step: 1, min: 0 },
	{ key: 'burst', name: 'Burst', time: true, step: 1, min: 0.1 },
	{ key: 'queueLimit', name: 'QueueLimit', int: true, step: 10, min: 0 },
	{ key: 'maxWait', name: 'MaxWait', time: true, step: 5, min: 0.1 },
	{ key: 'maxPause', name: 'MaxPause', time: true, step: 5, min: 0 },
	{ key: 'aimd.decreaseFactor', name: 'Aimd.DecreaseFactor', step: 0.05, min: 0.05, max: 0.95, controller: 'aimd' },
	{ key: 'aimd.increaseStep', name: 'Aimd.IncreaseStep', step: 10, min: 0, controller: 'aimd' },
	{ key: 'aimd.increaseInterval', name: 'Aimd.IncreaseInterval', time: true, step: 1, min: 0, controller: 'aimd' },
	{ key: 'cubic.beta', name: 'Cubic.Beta', step: 0.05, min: 0.05, max: 0.95, controller: 'cubic' },
	{ key: 'cubic.scaleConstant', name: 'Cubic.ScaleConstant', step: 0.1, min: 0.01, controller: 'cubic' },
]);

export function getOption(options, key) {
	const [a, b] = key.split('.');
	return b ? options[a]?.[b] : options[a];
}

/** Returns a copy of `options` with `key` set. Sets nothing when `value` equals the default. */
export function setOption(options, key, value) {
	const [a, b] = key.split('.');
	const next = { ...options };
	const isDefault = value === getOption(DEFAULT_OPTIONS, key);
	if (b) {
		const inner = { ...(next[a] ?? {}) };
		if (isDefault) delete inner[b];
		else inner[b] = value;
		if (Object.keys(inner).length) next[a] = inner;
		else delete next[a];
	} else if (isDefault) {
		delete next[a];
	} else {
		next[a] = value;
	}
	return next;
}

/** The checks of AdaptiveRateLimiterOptions that the editor can break. Returns messages. */
export function validate(options) {
	const o = withDefaults(options);
	const errors = [];
	if (!(o.minUnitsPerMinute > 0)) errors.push('MinUnitsPerMinute must be more than 0.');
	if (!(o.maxUnitsPerMinute >= o.minUnitsPerMinute)) errors.push('MaxUnitsPerMinute must be MinUnitsPerMinute or more.');
	if (!(o.initialUnitsPerMinute >= o.minUnitsPerMinute && o.initialUnitsPerMinute <= o.maxUnitsPerMinute)) {
		errors.push('InitialUnitsPerMinute must be between MinUnitsPerMinute and MaxUnitsPerMinute.');
	}
	if (!(o.burst > 0) || !(o.maxWait > 0)) errors.push('Burst and MaxWait must be more than 0.');
	if (!(o.decreaseCooldown >= 0) || !(o.maxPause >= 0) || !(o.queueLimit >= 0)) {
		errors.push('DecreaseCooldown, MaxPause and QueueLimit must be 0 or more.');
	}
	for (const [name, v] of [['Aimd.DecreaseFactor', o.aimd.decreaseFactor], ['Cubic.Beta', o.cubic.beta]]) {
		if (!(v > 0 && v < 1)) errors.push(`${name} must be more than 0 and less than 1.`);
	}
	if (!(o.aimd.increaseStep >= 0) || !(o.aimd.increaseInterval >= 0)) errors.push('Aimd.IncreaseStep and Aimd.IncreaseInterval must be 0 or more.');
	if (!(o.cubic.scaleConstant > 0)) errors.push('Cubic.ScaleConstant must be more than 0.');
	return errors;
}

const ALWAYS = ['initialUnitsPerMinute', 'minUnitsPerMinute', 'maxUnitsPerMinute'];

function exported(options) {
	const o = withDefaults(options);
	return FIELDS.filter((f) => ALWAYS.includes(f.key) || getOption(o, f.key) !== getOption(DEFAULT_OPTIONS, f.key)).map((f) => ({
		...f,
		value: getOption(o, f.key),
	}));
}

function timeSpan(seconds) {
	const h = Math.floor(seconds / 3600);
	const m = Math.floor((seconds % 3600) / 60);
	const s = seconds % 60;
	const whole = Math.floor(s);
	const frac = s - whole > 1e-9 ? `.${(s - whole).toFixed(3).slice(2).replace(/0+$/, '')}` : '';
	const two = (v) => String(v).padStart(2, '0');
	return `${two(h)}:${two(m)}:${two(whole)}${frac}`;
}

const controllerName = (mode) => (mode === 'cubic' ? 'Cubic' : 'Aimd');

export function toAppSettings({ mode, instances, options, name = 'api' }) {
	const limiter = { InstanceCount: instances };
	for (const f of exported(options)) {
		const value = f.time ? timeSpan(f.value) : f.value;
		const [a, b] = f.name.split('.');
		if (b) (limiter[a] ??= {})[b] = value;
		else limiter[a] = value;
	}
	const ordered = {};
	for (const [k, v] of Object.entries(limiter)) {
		if (k === 'Aimd' || k === 'Cubic') continue;
		ordered[k] = v;
	}
	ordered.Controller = controllerName(mode);
	if (limiter.Aimd) ordered.Aimd = limiter.Aimd;
	if (limiter.Cubic) ordered.Cubic = limiter.Cubic;
	return JSON.stringify({ AdaptiveRateLimiting: { Limiters: { [name]: ordered } } }, null, 2);
}

export function toCSharp({ mode, instances, options, name = 'api' }) {
	const lines = [`o.InstanceCount = ${instances};`];
	for (const f of exported(options)) {
		lines.push(`o.${f.name} = ${f.time ? `TimeSpan.FromSeconds(${f.value})` : f.value};`);
	}
	const at = lines.findIndex((l) => l.startsWith('o.Aimd.') || l.startsWith('o.Cubic.'));
	const controller = `o.Controller = RateControllerType.${controllerName(mode)};`;
	if (at < 0) lines.push(controller);
	else lines.splice(at, 0, controller);
	return `builder.Services.AddAdaptiveRateLimiter("${name}", o =>\n{\n${lines.map((l) => `    ${l}`).join('\n')}\n});`;
}

/* The URL hash: #sim?mode=aimd&n=2&q=3000&d=2000&seed=42&story=neighbor&o.maxUnitsPerMinute=2700 */
const HASH = '#sim?';

export function encodeState(state) {
	const p = new URLSearchParams({
		mode: state.mode,
		n: String(state.instances),
		q: String(state.quota),
		d: String(state.demand),
		seed: String(state.seed),
		story: state.story,
	});
	for (const f of FIELDS) {
		const v = getOption(state.options, f.key);
		if (v != null) p.set(`o.${f.key}`, String(v));
	}
	return HASH + p.toString();
}

export function decodeState(hash) {
	if (!hash?.startsWith(HASH)) return null;
	const p = new URLSearchParams(hash.slice(HASH.length));
	const num = (k, lo, hi) => {
		const v = Number(p.get(k));
		return p.has(k) && Number.isFinite(v) ? Math.min(hi, Math.max(lo, v)) : undefined;
	};
	let options = {};
	for (const f of FIELDS) {
		const v = num(`o.${f.key}`, 0, 1e7);
		if (v !== undefined) options = setOption(options, f.key, f.int ? Math.round(v) : v);
	}
	const mode = p.get('mode');
	const story = p.get('story');
	return {
		mode: MODES[mode] ? mode : undefined,
		instances: num('n', 1, MAX_INSTANCES),
		quota: num('q', 600, 9000),
		demand: num('d', 200, 5000),
		seed: num('seed', 1, 0x7fffffff),
		story: STORIES[story] ? story : undefined,
		options,
	};
}
