// Runs the simulator engine without a browser and prints the comparison of each scenario.
// The docs quote these numbers as model numbers. Usage: npm run simulate [-- --json]
import { compare, COMPARISON, MODES } from '../src/components/simulator/engine.js';

const scenarios = [
	{ name: 'Default: quota 3,000, 2 x 2,000', world: { quota: 3000, instances: 2, demand: 2000 } },
	{ name: 'Three instances: quota 3,000, 3 x 1,500', world: { quota: 3000, instances: 3, demand: 1500 } },
	{ name: 'Demand below quota: quota 3,000, 2 x 1,000', world: { quota: 3000, instances: 2, demand: 1000 } },
	{ name: 'Large quota: quota 6,000, 2 x 4,000', world: { quota: 6000, instances: 2, demand: 4000 } },
	{
		name: 'Max below quota: quota 3,000, 2 x 2,000, MaxUnitsPerMinute 2,700',
		world: { quota: 3000, instances: 2, demand: 2000, options: { maxUnitsPerMinute: 2700 } },
	},
];

const json = process.argv.includes('--json');
const out = [];
for (const s of scenarios) {
	const results = compare(s.world);
	out.push({
		scenario: s.name,
		results: results.map(({ history, rolling, ...r }) => r),
	});
}

if (json) {
	console.log(JSON.stringify(out, null, 2));
} else {
	console.log(
		`Plan: ${COMPARISON.duration / 60} min, seed ${COMPARISON.seed}, quota drop of ${COMPARISON.dropShare * 100}% at ${COMPARISON.dropAt} s for ${COMPARISON.dropFor} s`,
	);
	for (const s of out) {
		console.log(`\n${s.scenario}`);
		console.log('mode        throughput  share  429s  rejected  gave-up  p95-wait  recovery  peak-rate');
		for (const r of s.results) {
			console.log(
				[
					MODES[r.mode].label.padEnd(10),
					r.throughput.toFixed(0).padStart(10),
					`${(r.share * 100).toFixed(0)}%`.padStart(6),
					String(r.n429).padStart(5),
					String(r.rejected).padStart(9),
					String(r.gaveUp).padStart(8),
					`${r.p95.toFixed(1)} s`.padStart(9),
					(r.recovery == null ? 'none' : `${r.recovery} s`).padStart(9),
					r.peakRate.toFixed(0).padStart(10),
				].join('  '),
			);
		}
	}
}
