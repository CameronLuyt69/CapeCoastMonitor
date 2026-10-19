import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  compass, daysAgoText, daysBetween, formatResult, round, show, timeAgoText, uvCategory, visibilityText, waterQualityStatus,
} from '../../src/CapeCoast.Api/wwwroot/js/format.js';

const now = new Date('2026-10-19T12:00:00Z');

test('daysBetween counts calendar days, not 24 hour blocks', () => {
  assert.equal(daysBetween(new Date('2026-10-18T23:59:00Z'), new Date('2026-10-19T00:01:00Z')), 1);
  assert.equal(daysBetween(new Date('2026-10-19T00:00:00Z'), new Date('2026-10-19T23:59:00Z')), 0);
});

test('daysAgoText', () => {
  assert.equal(daysAgoText('2026-10-19T00:00:00Z', now), 'today');
  assert.equal(daysAgoText('2026-10-18T00:00:00Z', now), 'yesterday');
  assert.equal(daysAgoText('2026-10-15T00:00:00Z', now), '4 days ago');
});

test('timeAgoText', () => {
  assert.equal(timeAgoText('2026-10-19T11:59:40Z', now), 'just now');
  assert.equal(timeAgoText('2026-10-19T11:48:00Z', now), '12 min ago');
  assert.equal(timeAgoText('2026-10-19T09:00:00Z', now), '3 h ago');
  assert.equal(timeAgoText('2026-10-14T12:00:00Z', now), '5 days ago');
});

test('formatResult keeps qualifiers and spells out the City markers', () => {
  assert.equal(formatResult(12, null), '12');
  assert.equal(formatResult(2419, '>'), '>2419');
  assert.equal(formatResult(10, '<'), '<10');
  assert.equal(formatResult(null, 'ND'), 'not detected');
  assert.equal(formatResult(null, 'TFD'), 'too few data');
  assert.equal(formatResult(null, null), 'no result');
});

test('compass points', () => {
  assert.equal(compass(0), 'N');
  assert.equal(compass(225), 'SW');
  assert.equal(compass(359), 'N');
  assert.equal(compass(-90), 'W');
  assert.equal(compass(null), '');
});

test('round and show do not invent values for missing data', () => {
  assert.equal(round(1.2345, 2), 1.23);
  assert.equal(round(null), null);
  assert.equal(show(null, 'm'), 'no data');
  assert.equal(show(1.64, 'm', 2), '1.64 m');
});

test('City status wins over the band, and old results are marked stale', () => {
  const recent = { sampledAt: '2026-10-15T00:00:00Z' };

  assert.deepEqual(pick(waterQualityStatus({ ...recent, rating: 'Green', band: 'exceeds' }, now)), { key: 'ok', stale: false });
  assert.deepEqual(pick(waterQualityStatus({ ...recent, rating: 'Amber', band: 'ok' }, now)), { key: 'amber', stale: false });
  assert.deepEqual(pick(waterQualityStatus({ ...recent, rating: 'Red alert' }, now)), { key: 'red', stale: false });
  assert.deepEqual(pick(waterQualityStatus({ ...recent, rating: null, band: 'exceeds' }, now)), { key: 'amber', stale: false });
  assert.deepEqual(pick(waterQualityStatus({ ...recent, rating: null, band: 'unknown' }, now)), { key: 'unknown', stale: false });
  assert.deepEqual(pick(waterQualityStatus({ sampledAt: '2026-09-10T00:00:00Z', rating: 'Green' }, now)), { key: 'ok', stale: true });
  assert.deepEqual(pick(waterQualityStatus(null, now)), { key: 'none', stale: false });
});

test('the staleness line is 14 days', () => {
  assert.equal(waterQualityStatus({ sampledAt: '2026-10-05T00:00:00Z', rating: 'Green' }, now).stale, false);   // 14 days
  assert.equal(waterQualityStatus({ sampledAt: '2026-10-04T00:00:00Z', rating: 'Green' }, now).stale, true);    // 15 days
});

function pick({ key, stale }) {
  return { key, stale };
}

test('a snapshot without water quality shows its own status, never "no sampling site"', () => {
  const status = waterQualityStatus({ sampledAt: '2026-10-15T00:00:00Z', rating: 'Green' }, now, true);

  assert.equal(status.key, 'withheld');
  assert.equal(status.stale, false);
  assert.match(status.label, /not in this snapshot/);
  assert.equal(waterQualityStatus(null, now, true).key, 'withheld');
  assert.equal(waterQualityStatus(null, now, false).key, 'none');
});

test('UV index bands follow the WHO scale', () => {
  assert.equal(uvCategory(0), 'low');
  assert.equal(uvCategory(2.9), 'low');
  assert.equal(uvCategory(3), 'moderate');
  assert.equal(uvCategory(5.9), 'moderate');
  assert.equal(uvCategory(6), 'high');
  assert.equal(uvCategory(7.15), 'high');
  assert.equal(uvCategory(8), 'very high');
  assert.equal(uvCategory(10.9), 'very high');
  assert.equal(uvCategory(11), 'extreme');
  assert.equal(uvCategory(null), '');
});

test('visibility is shown in metres below a kilometre and kilometres above', () => {
  assert.equal(visibilityText(850), '850 m');
  assert.equal(visibilityText(999.4), '999 m');
  assert.equal(visibilityText(1000), '1 km');
  assert.equal(visibilityText(41640), '41.6 km');
  assert.equal(visibilityText(null), 'no data');
});
