export const fmt = (v) => Math.round(v).toLocaleString('en-US');
export const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
export const clock = (t) => `${Math.floor(t / 60)}:${String(Math.floor(t % 60)).padStart(2, '0')}`;

export const INSTANCE_CLASS = ['s-i1', 's-i2', 's-i3', 's-i4'];
export const MODE_CLASS = { none: 's-none', fixed: 's-fixed', aimd: 's-aimd', cubic: 's-cubic' };

export function svg(target, viewWidth, height, body, label) {
	target.setAttribute('viewBox', `0 0 ${viewWidth} ${height}`);
	target.setAttribute('width', String(viewWidth));
	target.setAttribute('height', String(height));
	if (label) target.setAttribute('aria-label', label);
	target.innerHTML = body;
}

export function niceStep(max, count = 5) {
	const raw = max / Math.max(1, count - 1);
	const mag = Math.pow(10, Math.floor(Math.log10(raw || 1)));
	return [1, 2, 2.5, 5, 10].map((m) => m * mag).find((v) => max / v <= count) ?? mag * 10;
}

export const short = (v) => (v >= 1000 ? `${+(v / 1000).toFixed(2)}k` : `${+v.toFixed(2)}`);

export function axes(W, H, pad, ymax, xLabel, ticks) {
	const y = (v) => pad.t + (H - pad.t - pad.b) * (1 - Math.min(v, ymax) / ymax);
	const step = niceStep(ymax);
	let s = '';
	for (let v = 0; v <= ymax + 1e-9; v += step) {
		s += `<line class="grid" x1="${pad.l}" x2="${W - pad.r}" y1="${y(v)}" y2="${y(v)}"/>`;
		s += `<text class="t-axis" x="${pad.l - 6}" y="${y(v) + 4}" text-anchor="end">${short(v)}</text>`;
	}
	for (const [x, label] of ticks) s += `<text class="t-axis" x="${x}" y="${H - pad.b + 14}" text-anchor="middle">${label}</text>`;
	s += `<text class="t-axis" x="${pad.l}" y="${H - 2}">${esc(xLabel)}</text>`;
	return { s, y };
}

export function path(points) {
	let d = '';
	for (let i = 0; i < points.length; i++) d += `${i ? 'L' : 'M'}${points[i][0].toFixed(1)} ${points[i][1].toFixed(1)}`;
	return d;
}

export const sumRates = (p) => p.rates.reduce((a, b) => a + b, 0);
