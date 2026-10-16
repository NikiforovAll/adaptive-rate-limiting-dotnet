/*
 * A model of Nall.RateLimiting.Adaptive and of an HTTP API with a hidden quota.
 * No DOM code: the view, the headless runner (docs/scripts/simulate.mjs) and the parity
 * check all use this module. Time is in seconds, rates are in units per minute.
 *
 * Each rule below mirrors a C# member. Keep them in step when the library changes.
 *
 *   Engine                              C# member
 *   ----------------------------------  -------------------------------------------------------
 *   DEFAULT_OPTIONS                     AdaptiveRateLimiterOptions, AimdOptions, CubicOptions
 *                                       (property initializers)
 *   AdaptiveTokenBucket.capacityFor     AdaptiveTokenBucket.CapacityFor
 *   AdaptiveTokenBucket.canTake         AdaptiveTokenBucket.CanTake: a cost above the capacity
 *                                       is granted when the bucket is full, and goes below zero
 *   AdaptiveTokenBucket.refill          AdaptiveTokenBucket.Refill: no refill before the pause ends
 *   AdaptiveTokenBucket.setRate         AdaptiveTokenBucket.SetRate
 *   AdaptiveTokenBucket.pause           AdaptiveTokenBucket.Pause: an end time, tokens to <= 0
 *   AdaptiveTokenBucket.resetWaited     AdaptiveTokenBucket.ResetWaited
 *   AdaptiveTokenBucket.tryTake         AdaptiveTokenBucket.TryTake / TryAcquireNow (FIFO: no
 *                                       grant while a request waits)
 *   AdaptiveTokenBucket.acquire         AdaptiveTokenBucket.AcquireAsyncCore (queue limit, sets
 *                                       the waited flag)
 *   AdaptiveTokenBucket.scheduleDrain   AdaptiveTokenBucket.ScheduleDrain (timer delay, 1 ms min)
 *   AdaptiveTokenBucket.drain           AdaptiveTokenBucket.Drain
 *   AdaptiveTokenBucket.cancel          AdaptiveTokenBucket.Cancel
 *   AdaptiveRateLimiter.constructor     AdaptiveRateLimiter ctor: rate = Initial / InstanceCount
 *   AdaptiveRateLimiter.wait            AdaptiveRateLimiter.WaitAsync / AcquireAsync
 *                                       (reasons queue_full and timeout)
 *   AdaptiveRateLimiter.expire          AdaptiveRateLimiter.AcquireAsync MaxWait timeout
 *   AdaptiveRateLimiter.onResponse      AdaptiveRateLimiter.OnResponseAsync and
 *                                       HttpThrottleClassifier.IsThrottled (429, or 503 with
 *                                       Retry-After; other 5xx do not change the rate)
 *   AdaptiveRateLimiter.onThrottled     AdaptiveRateLimiter.OnThrottled: DecreaseCooldown, the
 *                                       sentAt >= last decrease rule, Retry-After pause up to
 *                                       MaxPause, increase clock restarts after the pause
 *   AdaptiveRateLimiter.onSuccess       AdaptiveRateLimiter.OnSuccess
 *   AdaptiveRateLimiter.setRate         AdaptiveRateLimiter.SetRate (clamp to Min/Max share)
 *   AdaptiveRateLimiter.state           AdaptiveRateLimiter.State -> RateControllerState
 *   AdaptiveRateLimiter.setInstanceCount  AdaptiveRateLimiter.SetInstanceCount / Rescale
 *   aimdController.onThrottled          AimdController.OnThrottled
 *   aimdController.onSuccess            AimdController.OnSuccess (IncreaseInterval, Waited,
 *                                       IncreaseStep / InstanceCount)
 *   cubicController.onThrottled         CubicController.OnThrottled
 *   cubicController.onSuccess           CubicController.OnSuccess (Waited, MaxRate,
 *                                       2 x MeasuredRate)
 *   cubicIncrease                       CubicController.OnSuccess cubic curve: K and
 *                                       C x (t - K)^3 + W, with t = SinceLastDecrease
 *   AdaptiveRateLimiter.recordSent      AdaptiveRateLimiter.RecordSent
 *
 * Not modeled: Enabled = false, UpdateOptions, metrics, custom classifiers and estimators.
 * The server, the client retry handler and the "no limiter" and "fixed rate" modes are part
 * of the model, not of the library.
 */

export const DEFAULT_OPTIONS = Object.freeze({
	instanceCount: 1,
	initialUnitsPerMinute: 1500,
	minUnitsPerMinute: 200,
	maxUnitsPerMinute: 5625,
	decreaseCooldown: 5,
	burst: 10,
	queueLimit: 500,
	maxWait: 100,
	maxPause: 30,
	controller: 'aimd',
	aimd: Object.freeze({ decreaseFactor: 0.7, increaseStep: 120, increaseInterval: 10 }),
	cubic: Object.freeze({ beta: 0.7, scaleConstant: 0.4 }),
});

export function withDefaults(options = {}) {
	return {
		...DEFAULT_OPTIONS,
		...options,
		aimd: { ...DEFAULT_OPTIONS.aimd, ...(options.aimd ?? {}) },
		cubic: { ...DEFAULT_OPTIONS.cubic, ...(options.cubic ?? {}) },
	};
}

export class AdaptiveTokenBucket {
	constructor(tokensPerSecond, burst, queueLimit, now) {
		this.burst = burst;
		this.queueLimit = queueLimit;
		this.tokensPerSecond = tokensPerSecond;
		this.capacity = this.capacityFor(tokensPerSecond);
		this.tokens = this.capacity;
		this.lastRefill = now;
		this.pausedUntil = -Infinity;
		this.queue = [];
		this.waited = false;
		this.dueAt = null;
	}

	capacityFor(tokensPerSecond) {
		return Math.max(1, tokensPerSecond * this.burst);
	}

	canTake(cost) {
		return this.tokens >= Math.min(cost, this.capacity);
	}

	refill(now) {
		const from = Math.max(this.lastRefill, this.pausedUntil);
		if (now > from) {
			this.tokens = Math.min(this.capacity, this.tokens + (now - from) * this.tokensPerSecond);
		}
		this.lastRefill = now;
	}

	setRate(tokensPerSecond, now) {
		this.refill(now);
		this.tokensPerSecond = tokensPerSecond;
		this.capacity = this.capacityFor(tokensPerSecond);
		this.tokens = Math.min(this.tokens, this.capacity);
		this.scheduleDrain(now);
	}

	pause(duration, now) {
		this.refill(now);
		this.pausedUntil = Math.max(this.pausedUntil, now + duration);
		this.tokens = Math.min(this.tokens, 0);
		this.scheduleDrain(now);
	}

	isPaused(now) {
		return now < this.pausedUntil;
	}

	resetWaited() {
		const waited = this.waited;
		this.waited = this.queue.length > 0;
		return waited;
	}

	tryTake(cost, now) {
		this.refill(now);
		if (this.queue.length > 0 || !this.canTake(cost)) {
			return false;
		}
		this.tokens -= cost;
		return true;
	}

	/** Returns 'acquired', 'queued' or 'failed' (queue full). */
	acquire(waiter, now) {
		if (this.tryTake(waiter.cost, now)) {
			return 'acquired';
		}
		if (this.queue.length >= this.queueLimit) {
			return 'failed';
		}
		this.queue.push(waiter);
		this.waited = true;
		if (this.queue.length === 1) {
			this.scheduleDrain(now);
		}
		return 'queued';
	}

	scheduleDrain(now) {
		if (this.queue.length === 0) {
			this.dueAt = null;
			return;
		}
		const paused = Math.max(0, this.pausedUntil - now);
		const missing = Math.min(this.queue[0].cost, this.capacity) - this.tokens;
		this.dueAt = now + Math.max(paused + missing / this.tokensPerSecond, 0.001);
	}

	drain(now) {
		this.refill(now);
		const granted = [];
		while (this.queue.length > 0 && this.canTake(this.queue[0].cost)) {
			const waiter = this.queue.shift();
			this.tokens -= waiter.cost;
			granted.push(waiter);
		}
		this.scheduleDrain(now);
		return granted;
	}

	cancel(waiter, now) {
		const index = this.queue.indexOf(waiter);
		if (index < 0) {
			return;
		}
		this.queue.splice(index, 1);
		if (index === 0) {
			this.refill(now);
			this.scheduleDrain(now);
		}
	}

	/** Fires the drain timer each time it is due up to `until`, as a TimeProvider timer does. */
	runTimers(until, onGrant) {
		while (this.dueAt !== null && this.dueAt <= until) {
			const at = Math.max(this.dueAt, this.lastRefill);
			for (const waiter of this.drain(at)) {
				onGrant(waiter, at);
			}
		}
	}
}

export function cubicIncrease(state, beta, scaleConstant) {
	const scale = scaleConstant / Math.max(1, state.instanceCount);
	const maxPerSecond = state.rateBeforeLastDecrease / 60;
	const k = Math.cbrt((maxPerSecond * (1 - beta)) / scale);
	const t = state.sinceLastDecrease;
	return (scale * Math.pow(t - k, 3) + maxPerSecond) * 60;
}

const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

export function aimdController(options = DEFAULT_OPTIONS.aimd) {
	const { decreaseFactor, increaseStep, increaseInterval } = { ...DEFAULT_OPTIONS.aimd, ...options };
	return {
		name: 'aimd',
		onThrottled: (s) => Math.max(s.rate * decreaseFactor, s.minRate),
		onSuccess(s) {
			if (s.sinceLastIncrease < increaseInterval) {
				return null;
			}
			if (s.waited && s.rate < s.maxRate) {
				return Math.min(s.rate + increaseStep / Math.max(1, s.instanceCount), s.maxRate);
			}
			return s.rate;
		},
	};
}

export function cubicController(options = DEFAULT_OPTIONS.cubic) {
	const { beta, scaleConstant } = { ...DEFAULT_OPTIONS.cubic, ...options };
	return {
		name: 'cubic',
		onThrottled: (s) => clamp(s.rateBeforeLastDecrease * beta, s.minRate, Math.max(s.minRate, s.rate)),
		onSuccess(s) {
			if (!s.waited) {
				return null;
			}
			if (s.rate >= s.maxRate) {
				return s.maxRate;
			}
			const next = Math.min(cubicIncrease(s, beta, scaleConstant), 2 * s.measuredRate);
			return clamp(Math.max(s.rate, next), s.minRate, s.maxRate);
		},
	};
}

export function createController(options) {
	return options.controller === 'cubic' ? cubicController(options.cubic) : aimdController(options.aimd);
}

export function isThrottled(status, retryAfter) {
	return status === 429 || (status === 503 && retryAfter != null);
}

export class AdaptiveRateLimiter {
	constructor(options, now, controller) {
		this.options = withDefaults(options);
		this.instanceCount = this.options.instanceCount;
		this.controller = controller ?? createController(this.options);
		this.rate = this.options.initialUnitsPerMinute / this.instanceCount;
		this.rateBeforeLastDecrease = this.rate;
		this.lastIncrease = now;
		this.lastDecrease = now;
		this.growthStart = now;
		this.decreased = false;
		this.measuredRate = 0;
		this.sentUnits = 0;
		this.start = now;
		this.sentBucket = 0;
		this.bucket = new AdaptiveTokenBucket(
			bucketRate(this.rate),
			this.options.burst > 0 ? this.options.burst : 1,
			Math.max(0, this.options.queueLimit),
			now,
		);
	}

	get minRate() {
		return this.options.minUnitsPerMinute / this.instanceCount;
	}

	get maxRate() {
		return this.options.maxUnitsPerMinute / this.instanceCount;
	}

	/** Returns 'acquired', 'queued' or a rejection reason ('queue_full'). */
	wait(waiter, now) {
		if (waiter.cost === 0) {
			return 'acquired';
		}
		const result = this.bucket.acquire(waiter, now);
		return result === 'failed' ? 'queue_full' : result;
	}

	/** Removes the waiters whose MaxWait has passed and returns them (reason 'timeout'). */
	expire(now) {
		const expired = this.bucket.queue.filter((w) => now - w.waitStart >= this.options.maxWait);
		for (const waiter of expired) {
			this.bucket.cancel(waiter, now);
		}
		return expired;
	}

	onResponse(now, status, retryAfter, sentAt, cost = 1) {
		if (cost <= 0) {
			return;
		}
		this.recordSent(cost, now);
		if (isThrottled(status, retryAfter)) {
			this.onThrottled(now, retryAfter, sentAt);
		} else if (status < 500) {
			this.onSuccess(now);
		}
	}

	onThrottled(now, retryAfter = null, sentAt = null) {
		this.lastIncrease = now;
		this.bucket.resetWaited();

		if (
			!this.decreased ||
			(now - this.lastDecrease >= this.options.decreaseCooldown && (sentAt ?? now) >= this.lastDecrease)
		) {
			this.rateBeforeLastDecrease = this.measuredRate > 0 ? Math.min(this.rate, this.measuredRate) : this.rate;
			const state = this.state(now);
			this.decreased = true;
			this.lastDecrease = now;
			this.growthStart = now;
			this.setRate(Math.min(this.controller.onThrottled(state), this.rate), now);
		}

		if (retryAfter != null && retryAfter > 0 && this.options.maxPause > 0) {
			const pause = Math.min(retryAfter, this.options.maxPause);
			this.bucket.pause(pause, now);
			this.lastIncrease = now + pause;
			this.growthStart = Math.max(this.growthStart, now + pause);
		}
	}

	/** The botocore measured send rate: an average over half-second buckets, smoothing 0.8. */
	recordSent(units, now) {
		const bucket = Math.floor((now - this.start) * 2) / 2;
		this.sentUnits += units;
		if (bucket > this.sentBucket) {
			const current = (this.sentUnits / (bucket - this.sentBucket)) * 60;
			this.measuredRate = current * 0.8 + this.measuredRate * 0.2;
			this.sentUnits = 0;
			this.sentBucket = bucket;
		}
	}

	onSuccess(now) {
		if (now < this.lastIncrease) {
			return;
		}
		const next = this.controller.onSuccess(this.state(now));
		if (next == null) {
			return;
		}
		this.lastIncrease = now;
		this.bucket.resetWaited();
		this.setRate(next, now);
	}

	setInstanceCount(instanceCount, now) {
		const previous = this.instanceCount;
		if (instanceCount === previous) {
			return;
		}
		this.instanceCount = instanceCount;
		this.rateBeforeLastDecrease = (this.rateBeforeLastDecrease * previous) / instanceCount;
		this.measuredRate = (this.measuredRate * previous) / instanceCount;
		this.setRate((this.rate * previous) / instanceCount, now);
	}

	setRate(unitsPerMinute, now) {
		const value = Number.isNaN(unitsPerMinute) ? this.rate : unitsPerMinute;
		const rate = clamp(value, this.minRate, Math.max(this.minRate, this.maxRate));
		if (rate === this.rate) {
			return;
		}
		this.rate = rate;
		this.bucket.setRate(bucketRate(rate), now);
	}

	state(now) {
		return {
			rate: this.rate,
			minRate: this.minRate,
			maxRate: this.maxRate,
			rateBeforeLastDecrease: this.rateBeforeLastDecrease,
			sinceLastDecrease: Math.max(0, now - this.growthStart),
			measuredRate: this.measuredRate,
			sinceLastIncrease: now - this.lastIncrease,
			waited: this.bucket.waited,
			instanceCount: this.instanceCount,
		};
	}
}

function bucketRate(unitsPerMinute) {
	return Math.max(unitsPerMinute / 60, Number.MIN_VALUE);
}

/* ---------------------------------------------------------------------------------------
 * The world around the limiter: a server with a hidden quota, a retry handler and demand.
 * --------------------------------------------------------------------------------------- */

export const MODES = Object.freeze({
	none: { label: 'No limiter', short: 'None' },
	fixed: { label: 'Fixed rate', short: 'Fixed' },
	aimd: { label: 'AIMD', short: 'AIMD' },
	cubic: { label: 'CUBIC', short: 'CUBIC' },
});

/** Request costs in units. The mean is 2 units. */
export const COST_MIX = Object.freeze([1, 1, 1, 1, 1, 1, 2, 2, 2, 5]);
const MEAN_COST = COST_MIX.reduce((a, b) => a + b, 0) / COST_MIX.length;

export function seeded(seed) {
	let s = seed | 0;
	return () => {
		s = (s + 0x6d2b79f5) | 0;
		let x = Math.imul(s ^ (s >>> 15), 1 | s);
		x = (x + Math.imul(x ^ (x >>> 7), 61 | x)) ^ x;
		return ((x ^ (x >>> 14)) >>> 0) / 4294967296;
	};
}

/**
 * A sliding 60 s window of the units the server accepted, in 1 s slots. A request that would
 * take the window above the quota gets 429 and a Retry-After: the seconds until enough units
 * leave the window. A throttled request takes no units.
 */
export class QuotaServer {
	constructor(quota) {
		this.quota = quota;
		this.slots = new Float64Array(60);
		this.second = 0;
		this.used = 0;
		this.dropUntil = -Infinity;
		this.dropShare = 0.5;
		this.outageUntil = -Infinity;
	}

	limit(now) {
		return now < this.dropUntil ? this.quota * (1 - this.dropShare) : this.quota;
	}

	advance(now) {
		const second = Math.floor(now);
		while (this.second < second) {
			this.second++;
			const slot = this.second % 60;
			this.used -= this.slots[slot];
			this.slots[slot] = 0;
		}
		if (this.used < 1e-9) {
			this.used = Math.max(0, this.used);
		}
	}

	handle(cost, now) {
		this.advance(now);
		if (now < this.outageUntil) {
			return { status: 503, retryAfter: Math.max(1, Math.ceil(this.outageUntil - now)) };
		}
		const limit = this.limit(now);
		if (this.used + cost <= limit) {
			this.slots[this.second % 60] += cost;
			this.used += cost;
			return { status: 200, retryAfter: null };
		}
		let freed = 0;
		for (let s = this.second - 59; s <= this.second; s++) {
			freed += s >= 0 ? this.slots[((s % 60) + 60) % 60] : 0;
			if (this.used - freed + cost <= limit) {
				return { status: 429, retryAfter: Math.max(1, Math.ceil(s + 60 - now)) };
			}
		}
		return { status: 429, retryAfter: 60 };
	}
}

export const DEFAULT_WORLD = Object.freeze({
	mode: 'aimd',
	instances: 2,
	quota: 3000,
	demand: 2000,
	seed: 7,
	dt: 0.1,
	latency: 0.2,
	maxRetries: 3,
	options: {},
	story: null,
});

export const MAX_INSTANCES = 4;

/**
 * Scripted scenarios. Each event runs once, at the end of the step that reaches `at`.
 * `factor` values are relative to the quota or the demand that the reader chose.
 * `window` is the disturbance: the recovery column counts from its end.
 */
export const STORIES = Object.freeze({
	free: {
		label: 'Free run',
		text: 'No events. Change the settings while it runs.',
		duration: 900,
		events: [],
	},
	neighbor: {
		label: 'A noisy neighbor',
		text: 'After 5 minutes, another client takes half of the quota for 60 seconds.',
		duration: 900,
		window: [300, 360],
		events: [{ at: 300, type: 'drop', seconds: 60, share: 0.5 }],
	},
	cut: {
		label: 'The server cuts the quota',
		text: 'After 4 minutes, the server lowers the quota to 60% for 3 minutes. Then the full quota comes back.',
		duration: 720,
		window: [240, 420],
		events: [
			{ at: 240, type: 'quota', factor: 0.6 },
			{ at: 420, type: 'quota', factor: 1 },
		],
	},
	scale: {
		label: 'Scale out',
		text: 'One instance at the start. A second joins after 2 minutes, and two more after 4 minutes. Each limiter takes its share.',
		duration: 600,
		world: { instances: 1 },
		events: [
			{ at: 120, type: 'instances', count: 2 },
			{ at: 240, type: 'instances', count: 4 },
		],
	},
	burst: {
		label: 'Top of the hour',
		text: 'After 3 minutes, a scheduled job adds two times the demand for 90 seconds.',
		duration: 600,
		window: [180, 270],
		events: [
			{ at: 180, type: 'demand', factor: 3 },
			{ at: 270, type: 'demand', factor: 1 },
		],
	},
	outage: {
		label: 'A long Retry-After',
		text: 'After 3 minutes, the API is down for 90 seconds. It answers 503 with a Retry-After to the end of the outage. The limiter pauses for MaxPause at most, then tries again.',
		duration: 600,
		window: [180, 270],
		events: [{ at: 180, type: 'outage', seconds: 90 }],
	},
});

const MARK_LIMIT = 400;

export class Simulation {
	constructor(world = {}) {
		const story = STORIES[world.story] ?? null;
		this.world = { ...DEFAULT_WORLD, ...world, ...(story?.world ?? {}) };
		this.story = story;
		this.script = story ? story.events.slice().sort((a, b) => a.at - b.at) : [];
		this.factors = { quota: 1, demand: 1 };
		this.options = withDefaults(this.world.options);
		this.t = 0;
		this.server = new QuotaServer(this.world.quota);
		this.orderRng = seeded(this.world.seed * 7919 + 1);
		this.clients = [];
		this.inflight = [];
		this.history = [];
		this.events = [];
		this.marks = [];
		this.totals = { accepted: 0, n429: 0, rejected: 0, queueFull: 0, timeout: 0, gaveUp: 0, sent: 0 };
		this.waits = [];
		this.recentWaits = [];
		this.second = { n429: 0, accepted: 0 };
		this.setInstances(this.world.instances);
	}

	get mode() {
		return this.world.mode;
	}

	log(text) {
		this.events.push({ t: this.t, text });
		if (this.events.length > 4) this.events.shift();
	}

	/** Records an event for the chart. `detail` says which rule fired. */
	mark(kind, text, detail = '', instance = null, extra = {}) {
		const mark = { t: this.t, kind, text, detail, instance, ...extra };
		this.marks.push(mark);
		if (this.marks.length > MARK_LIMIT) this.marks.shift();
		return mark;
	}

	applyEvent(e) {
		if (e.type === 'drop') {
			this.quotaDrop(e.seconds, e.share);
		} else if (e.type === 'quota') {
			this.factors.quota = e.factor;
			this.server.quota = this.world.quota * e.factor;
			const text = e.factor === 1 ? 'the server gives back the full quota' : `the server cuts the quota to ${Math.round(e.factor * 100)}%`;
			this.log(text);
			this.mark('story', text, `The hidden quota is now ${Math.round(this.server.quota).toLocaleString('en-US')} units per minute. The limiters do not see it: they learn it from 429s.`);
		} else if (e.type === 'demand') {
			this.factors.demand = e.factor;
			const text = e.factor === 1 ? 'the burst ends' : `a burst: demand x${e.factor}`;
			this.log(text);
			this.mark('story', text, e.factor === 1 ? 'The demand goes back to the normal value.' : `Each instance now wants ${e.factor} times its normal demand. Requests wait in the queue, and the queue limit and MaxWait start to matter.`);
		} else if (e.type === 'instances') {
			const before = this.clients.length;
			this.setInstances(e.count);
			const text = `scale out: ${before} -> ${e.count} instances`;
			this.log(text);
			this.mark('story', text, `SetInstanceCount(${e.count}): each limiter scales its rate, its minimum and its maximum to 1/${e.count} of the budget. New instances start at InitialUnitsPerMinute / ${e.count}.`);
		} else if (e.type === 'outage') {
			this.server.outageUntil = this.t + e.seconds;
			const text = `the API is down for ${e.seconds} s`;
			this.log(text);
			this.mark('story', text, `Each request gets 503 with a Retry-After to the end of the outage. A 503 with Retry-After is a throttle: the limiter cuts and pauses, but for MaxPause (${this.options.maxPause} s) at most.`);
		}
	}

	runScript() {
		while (this.script.length && this.script[0].at <= this.t + 1e-9) this.applyEvent(this.script.shift());
	}

	/** Reads the limiter after a response and marks the rule that fired. Read-only. */
	explain(flight, before, now) {
		const { client } = flight;
		const l = client.limiter;
		const o = l.options;
		const i = client.index;
		const fmtRate = (v) => Math.round(v).toLocaleString('en-US');
		if (isThrottled(flight.status, flight.retryAfter)) {
			const what = flight.status === 429 ? '429' : '503 with Retry-After';
			if (l.decreased && l.lastDecrease === now && (!before.decreased || before.cut !== now)) {
				const rule =
					l.controller.name === 'cubic'
						? `CUBIC: W = ${fmtRate(l.rateBeforeLastDecrease)}, the lower of the rate and the measured send rate. New rate = W x Beta (${o.cubic.beta}).`
						: `AIMD: new rate = rate x DecreaseFactor (${o.aimd.decreaseFactor}).`;
				const floor = l.rate <= l.minRate + 1e-9 ? ` The rate stops at the MinUnitsPerMinute share (${fmtRate(l.minRate)}).` : '';
				client.cutMark = this.mark(
					'cut',
					`instance ${i + 1}: ${what}, rate ${fmtRate(before.rate)} -> ${fmtRate(l.rate)}`,
					`${rule}${floor} For the next DecreaseCooldown (${o.decreaseCooldown} s), more throttles do not cut again.`,
					i,
					{ ignored: { cooldown: 0, stale: 0 }, rate: l.rate },
				);
			} else if (client.cutMark) {
				const reason = now - before.cut < o.decreaseCooldown ? 'cooldown' : 'stale';
				client.cutMark.ignored[reason]++;
			}
			if (l.bucket.pausedUntil > before.paused + 1e-9) {
				const ra = flight.retryAfter;
				const until = l.bucket.pausedUntil;
				const open = client.pauseMark && client.pauseMark.until >= now - 1e-9;
				if (open) {
					client.pauseMark.until = until;
					client.pauseMark.count++;
				} else {
					const capped = ra > o.maxPause;
					client.pauseMark = this.mark(
						'pause',
						`instance ${i + 1}: pause ${Math.round(Math.min(ra, o.maxPause))} s`,
						`Retry-After ${ra} s. The bucket gives no units until the pause ends${capped ? `, capped by MaxPause (${o.maxPause} s)` : ''}. The increase clock starts again after the pause.`,
						i,
						{ until, count: 1, rate: l.rate },
					);
				}
			}
		} else if (before.rate < l.maxRate - 1e-9 && l.rate >= l.maxRate - 1e-9) {
			this.mark(
				'max',
				`instance ${i + 1}: rate at the maximum`,
				`The rate reached the MaxUnitsPerMinute share (${fmtRate(l.maxRate)}). It cannot go higher. If the API allows more, raise MaxUnitsPerMinute.`,
				i,
				{ rate: l.rate },
			);
		}
	}

	newClient(index, count) {
		const rng = seeded(this.world.seed * 104729 + index * 31 + 3);
		const client = {
			index,
			rng,
			nextArrival: 0,
			retries: [],
			flashUntil: -Infinity,
			sentLog: [],
			limiter: null,
			bucket: null,
		};
		client.nextArrival = this.t + this.interArrival(client);
		const options = { ...this.options, instanceCount: count };
		if (this.mode === 'aimd' || this.mode === 'cubic') {
			client.limiter = new AdaptiveRateLimiter({ ...options, controller: this.mode }, this.t);
		} else if (this.mode === 'fixed') {
			client.bucket = new AdaptiveTokenBucket(
				options.initialUnitsPerMinute / count / 60,
				options.burst,
				options.queueLimit,
				this.t,
			);
		}
		return client;
	}

	interArrival(client) {
		const perSecond = (this.world.demand * this.factors.demand) / 60 / MEAN_COST;
		if (perSecond <= 0) return Infinity;
		return -Math.log(1 - client.rng()) / perSecond;
	}

	setInstances(count) {
		const n = Math.max(1, Math.min(MAX_INSTANCES, count | 0));
		this.world.instances = n;
		for (const c of this.clients) {
			if (c.limiter) c.limiter.setInstanceCount(n, this.t);
			if (c.bucket) c.bucket.setRate(this.options.initialUnitsPerMinute / n / 60, this.t);
		}
		while (this.clients.length < n) this.clients.push(this.newClient(this.clients.length, n));
		this.clients.length = n;
		this.inflight = this.inflight.filter((f) => f.client.index < n);
	}

	setQuota(quota) {
		this.world.quota = quota;
		this.server.quota = quota * this.factors.quota;
	}

	setDemand(demand) {
		this.world.demand = demand;
	}

	quotaDrop(seconds = 60, share = 0.5) {
		this.server.dropUntil = this.t + seconds;
		this.server.dropShare = share;
		const text = `noisy neighbor takes ${Math.round(share * 100)}% of the quota for ${seconds} s`;
		this.log(text);
		this.mark('story', text, 'Another client uses the same quota. The API now allows less, but it does not say so: the limiters learn it from 429s.');
	}

	rateOf(client) {
		if (client.limiter) return client.limiter.rate;
		if (client.bucket) return client.bucket.tokensPerSecond * 60;
		const since = this.t - 10;
		let units = 0;
		for (const s of client.sentLog) if (s.at > since) units += s.cost;
		return units * 6;
	}

	queueOf(client) {
		return client.limiter ? client.limiter.bucket.queue : client.bucket ? client.bucket.queue : [];
	}

	bucketOf(client) {
		return client.limiter ? client.limiter.bucket : client.bucket;
	}

	send(client, request, at) {
		const result = this.server.handle(request.cost, at);
		this.totals.sent++;
		client.sentLog.push({ at, cost: request.cost });
		if (client.sentLog.length > 4000) client.sentLog.splice(0, 2000);
		this.inflight.push({ client, request, sentAt: at, at: at + this.world.latency, ...result });
		if (result.status === 200) {
			this.totals.accepted += request.cost;
			this.second.accepted += request.cost;
		}
	}

	granted(client, request, at) {
		const wait = at - request.waitStart;
		this.waits.push(wait);
		this.recentWaits.push(wait);
		if (this.recentWaits.length > 400) this.recentWaits.shift();
		this.send(client, request, at);
	}

	reject(reason) {
		this.totals.rejected++;
		if (reason === 'queue_full' || reason === 'failed') this.totals.queueFull++;
		else this.totals.timeout++;
	}

	ready(client, request, now) {
		request.waitStart = now;
		if (this.mode === 'none') {
			this.granted(client, request, now);
			return;
		}
		const result = client.limiter ? client.limiter.wait(request, now) : client.bucket.acquire(request, now);
		if (result === 'acquired') this.granted(client, request, now);
		else if (result !== 'queued') this.reject(result);
	}

	respond(flight, now) {
		const { client, request } = flight;
		if (client.limiter) {
			client.limiter.onResponse(now, flight.status, flight.retryAfter, flight.sentAt, request.cost);
		}
		if (!isThrottled(flight.status, flight.retryAfter)) return;
		this.totals.n429++;
		this.second.n429++;
		client.flashUntil = now + 1;
		if (request.attempt < this.world.maxRetries) {
			client.retries.push({ cost: request.cost, attempt: request.attempt + 1, at: now + flight.retryAfter });
		} else {
			this.totals.gaveUp++;
		}
	}

	step() {
		const dt = this.world.dt;
		const now = Math.round((this.t + dt) * 1e6) / 1e6;
		const order = this.clients.map((c) => [c, this.orderRng()]).sort((a, b) => a[1] - b[1]).map((p) => p[0]);

		for (const client of order) {
			const bucket = this.bucketOf(client);
			if (bucket) bucket.runTimers(now, (w, at) => this.granted(client, w, at));
		}

		this.t = now;
		this.server.advance(now);

		const due = this.inflight.filter((f) => f.at <= now);
		if (due.length) {
			this.inflight = this.inflight.filter((f) => f.at > now);
			for (const flight of due) {
				const l = flight.client.limiter;
				const before = l && { rate: l.rate, cut: l.lastDecrease, decreased: l.decreased, paused: l.bucket.pausedUntil };
				this.respond(flight, now);
				if (!l) continue;
				if (l.rate < before.rate) {
					this.log(`instance ${flight.client.index + 1}: ${flight.status}, rate ${Math.round(before.rate)} -> ${Math.round(l.rate)}`);
				}
				this.explain(flight, before, now);
			}
		}

		for (const client of order) {
			const ready = client.retries.filter((r) => r.at <= now);
			if (ready.length) {
				client.retries = client.retries.filter((r) => r.at > now);
				for (const r of ready) this.ready(client, { cost: r.cost, attempt: r.attempt }, now);
			}
			while (client.nextArrival <= now) {
				const cost = COST_MIX[Math.floor(client.rng() * COST_MIX.length)];
				client.nextArrival += this.interArrival(client);
				this.ready(client, { cost, attempt: 0 }, now);
			}
			if (client.limiter) {
				for (const _ of client.limiter.expire(now)) this.reject('timeout');
			} else if (client.bucket) {
				const expired = client.bucket.queue.filter((w) => now - w.waitStart >= this.options.maxWait);
				for (const w of expired) {
					client.bucket.cancel(w, now);
					this.reject('timeout');
				}
			}
		}

		if (Math.abs(now - Math.round(now)) < 1e-6) this.record(Math.round(now));
		if (this.script.length) this.runScript();
	}

	record(second) {
		this.history.push({
			t: second,
			rates: this.clients.map((c) => this.rateOf(c)),
			used: this.server.used,
			limit: this.server.limit(this.t),
			n429: this.second.n429,
			accepted: this.second.accepted,
			queue: this.clients.reduce((a, c) => a + this.queueOf(c).length, 0),
			total: { accepted: this.totals.accepted, n429: this.totals.n429, lost: this.totals.rejected + this.totals.gaveUp },
		});
		this.second = { n429: 0, accepted: 0 };
		if (this.world.historyMax && this.history.length > this.world.historyMax) this.history.shift();
	}

	runUntil(seconds, onSecond) {
		while (this.t < seconds - 1e-9) {
			const before = this.history.length;
			this.step();
			if (onSecond && this.history.length > before) onSecond(this);
		}
	}

	snapshot() {
		const last = this.history.at(-1);
		return {
			t: this.t,
			used: this.server.used,
			limit: this.server.limit(this.t),
			sumRate: this.clients.reduce((a, c) => a + this.rateOf(c), 0),
			n429: this.totals.n429,
			queue: this.clients.reduce((a, c) => a + this.queueOf(c).length, 0),
			p95: percentile(this.recentWaits, 0.95),
			rejected: this.totals.rejected,
			gaveUp: this.totals.gaveUp,
			last,
		};
	}
}

export function percentile(values, p) {
	if (!values.length) return 0;
	const sorted = values.slice().sort((a, b) => a - b);
	return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))];
}

export const COMPARISON = Object.freeze({ duration: 900, dropAt: 300, dropFor: 60, dropShare: 0.5, seed: 42 });

/** Runs one mode for `duration` seconds with a quota drop, and returns the summary. */
export function runScenario(world, plan = COMPARISON) {
	const sim = new Simulation({ ...world, seed: plan.seed });
	sim.runUntil(plan.dropAt);
	sim.quotaDrop(plan.dropFor, plan.dropShare);
	sim.runUntil(plan.duration);
	return summarize(sim, plan);
}

export function summarize(sim, plan = COMPARISON) {
	const h = sim.history;
	const rolling = h.map((_, i) => {
		let sum = 0;
		for (let k = Math.max(0, i - 9); k <= i; k++) sum += h[k].accepted;
		return sum * (60 / Math.min(10, i + 1));
	});
	const pre = h.map((p, i) => [p, rolling[i]]).filter(([p]) => p.t > plan.dropAt - 60 && p.t <= plan.dropAt);
	const preMean = pre.reduce((a, [, r]) => a + r, 0) / Math.max(1, pre.length);
	const end = plan.dropAt + plan.dropFor;
	const back = h.findIndex((p, i) => p.t >= end && rolling[i] >= 0.9 * preMean);
	const avgLimit = h.reduce((a, p) => a + p.limit, 0) / h.length;
	const throughput = sim.totals.accepted / (plan.duration / 60);
	const rates = h.map((p) => p.rates.reduce((a, b) => a + b, 0));
	return {
		mode: sim.mode,
		throughput,
		share: throughput / avgLimit,
		n429: sim.totals.n429,
		rejected: sim.totals.rejected,
		gaveUp: sim.totals.gaveUp,
		p95: percentile(sim.waits, 0.95),
		recovery: back < 0 ? null : h[back].t - end,
		peakRate: Math.max(...rates),
		history: h,
		rolling,
	};
}

export function compare(world, modes = Object.keys(MODES), plan = COMPARISON) {
	return modes.map((mode) => runScenario({ ...world, mode }, plan));
}

export function storyPlan(key, seed = COMPARISON.seed) {
	const story = STORIES[key] ?? STORIES.free;
	const [from, to] = story.window ?? [story.duration, story.duration];
	return { duration: story.duration, dropAt: from, dropFor: to - from, seed, story: key, hasWindow: Boolean(story.window) };
}

/** Runs one mode through a story. With the 'neighbor' story and seed 42 it gives the numbers of runScenario. */
export function runStory(world, key, seed = COMPARISON.seed) {
	const plan = storyPlan(key, seed);
	const sim = new Simulation({ ...world, seed: plan.seed, story: STORIES[key] ? key : 'free' });
	sim.runUntil(plan.duration);
	const result = summarize(sim, plan);
	if (!plan.hasWindow) result.recovery = null;
	result.marks = sim.marks.filter((m) => m.kind === 'story');
	return result;
}

export function race(world, key, seed, modes = Object.keys(MODES)) {
	return modes.map((mode) => runStory({ ...world, mode }, key, seed));
}
