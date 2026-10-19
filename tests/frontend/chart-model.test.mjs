import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildChart, nearestPoint, nearestSample, niceTicks } from '../../src/CapeCoast.Api/wwwroot/js/chart-model.js';

const from = '2026-10-12T00:00:00Z';
const to = '2026-10-22T00:00:00Z';
const base = { from, to, width: 560, height: 230, zeroBased: true };

const raw = [
  { t: '2026-10-18T00:00:00Z', value: 1.0, ingestionRunId: 1 },
  { t: '2026-10-18T01:00:00Z', value: 2.0, ingestionRunId: 1 },
  { t: '2026-10-18T02:00:00Z', value: null, ingestionRunId: 1 },
  { t: '2026-10-18T03:00:00Z', value: 3.0, ingestionRunId: 1 },
];
const daily = [{ t: '2026-10-18T00:00:00Z', min: 0.8, max: 3.4, avg: 2 }];

test('niceTicks gives round numbers that cover the range', () => {
  assert.deepEqual(niceTicks(0, 10, 5), [0, 2, 4, 6, 8, 10]);
  assert.deepEqual(niceTicks(0, 2.4, 4), [0, 1, 2]);
  assert.deepEqual(niceTicks(0, 2, 4), [0, 0.5, 1, 1.5, 2]);
  assert.deepEqual(niceTicks(5, 5), [5]);
});

test('an empty series is reported as empty rather than drawn', () => {
  assert.equal(buildChart({ ...base, raw: [], daily: [] }).empty, true);
  assert.equal(buildChart({ ...base, raw: [{ t: from, value: null }], daily: [] }).empty, true);
});

test('the line breaks at a missing value instead of bridging the gap', () => {
  const chart = buildChart({ ...base, raw, daily });

  assert.equal(chart.linePaths.length, 2);
  assert.match(chart.linePaths[1], /^M[\d.]+,[\d.]+l0\.01,0$/);   // the lone point after the gap still shows
});

test('zero based metrics start at zero, temperature does not have to', () => {
  const heights = buildChart({ ...base, raw, daily });
  assert.ok(heights.lo <= 0);

  const temps = buildChart({ ...base, zeroBased: false, raw: [{ t: from, value: 15 }, { t: to, value: 17 }], daily: [] });
  assert.ok(temps.lo > 10);
});

test('the daily band spans the whole day and covers its min and max', () => {
  const chart = buildChart({ ...base, raw, daily });

  const [band] = chart.bands;
  assert.ok(band.height > 0);
  assert.ok(Math.abs(band.width - chart.innerW / 10) < 0.5, 'one day of ten is a tenth of the width');
  assert.ok(chart.y(3.4) <= band.y + 0.01 && chart.y(0.8) >= band.y + band.height - 0.01);
});

test('the scale maps values inside the plot area, higher values higher up', () => {
  const chart = buildChart({ ...base, raw, daily });

  assert.ok(chart.y(3) < chart.y(1));
  assert.ok(chart.y(0) <= chart.margin.top + chart.innerH + 0.01);
  assert.ok(chart.x('2026-10-17T00:00:00Z') > chart.x('2026-10-16T00:00:00Z'));
});

test('bands outside the chart range are dropped', () => {
  const chart = buildChart({ ...base, raw, daily: [...daily, { t: '2026-09-01T00:00:00Z', min: 1, max: 2, avg: 1.5 }] });
  assert.equal(chart.bands.length, 1);
});

test('nearestPoint finds the closest hour and ignores gaps and distant clicks', () => {
  const chart = buildChart({ ...base, raw, daily });

  assert.equal(nearestPoint(chart, raw, chart.x('2026-10-18T01:05:00Z')).value, 2.0);
  // the null hour is never picked, even when the click lands right on it
  assert.notEqual(nearestPoint(chart, raw, chart.x('2026-10-18T02:00:00Z'))?.value, null);
  assert.equal(nearestPoint(chart, raw, chart.x('2026-10-13T00:00:00Z'), 24), null);
});

test('nearestSample links a point to the closest sample within the window', () => {
  const samples = [
    { sampledAt: '2026-09-01T00:00:00Z', id: 1 },
    { sampledAt: '2026-09-08T00:00:00Z', id: 2 },
  ];

  assert.equal(nearestSample(samples, '2026-09-10T00:00:00Z').id, 2);
  assert.equal(nearestSample(samples, '2026-09-04T12:00:00Z', 7).id, 1);
  assert.equal(nearestSample(samples, '2026-10-19T00:00:00Z', 7), null);
  assert.equal(nearestSample([], '2026-09-10T00:00:00Z'), null);
});
