// Formatting and classification helpers. Pure functions, no DOM, so they can be tested with node.

const DAY = 86400000;
export const STALE_AFTER_DAYS = 14;

function startOfUtcDay(date) {
  return Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate());
}

/** Whole calendar days from a to b, in UTC. */
export function daysBetween(a, b) {
  return Math.round((startOfUtcDay(b) - startOfUtcDay(a)) / DAY);
}

/** "today", "yesterday" or "4 days ago", for results that are only known by their date. */
export function daysAgoText(iso, now = new Date()) {
  const d = daysBetween(new Date(iso), now);
  if (d <= 0) return 'today';
  if (d === 1) return 'yesterday';
  return `${d} days ago`;
}

/** "12 min ago", "3 h ago" or "2 days ago", for timestamps with a time of day. */
export function timeAgoText(iso, now = new Date()) {
  const minutes = Math.round((now - new Date(iso)) / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.round(hours / 24)} days ago`;
}

export function formatDate(iso) {
  return new Date(iso).toLocaleDateString('en-ZA', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
}

export function formatDateTime(iso) {
  return new Date(iso).toLocaleString('en-ZA', {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', timeZone: 'UTC',
  }) + ' UTC';
}

/** ">2419", "<10", a plain count, or the City's markers spelled out. */
export function formatResult(cfu, qualifier) {
  if (cfu == null) {
    if (qualifier === 'ND') return 'not detected';
    if (qualifier === 'TFD') return 'too few data';
    return 'no result';
  }
  return `${qualifier === '>' || qualifier === '<' ? qualifier : ''}${cfu}`;
}

const POINTS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];

export function compass(deg) {
  if (deg == null || Number.isNaN(deg)) return '';
  return POINTS[Math.round((((deg % 360) + 360) % 360) / 22.5) % 16];
}

export function round(value, digits = 1) {
  if (value == null || Number.isNaN(value)) return null;
  const f = 10 ** digits;
  return Math.round(value * f) / f;
}

export function show(value, unit, digits = 1) {
  const r = round(value, digits);
  return r == null ? 'no data' : `${r} ${unit}`;
}

export const STATUS = {
  ok: { label: 'Within guideline', glyph: '✓' },
  amber: { label: 'Above guideline', glyph: '!' },
  red: { label: 'Red alert', glyph: '✕' },
  unknown: { label: 'No usable result', glyph: '?' },
  none: { label: 'No City sampling site', glyph: '–' },
  withheld: { label: 'Water quality not in this snapshot', glyph: '·' },
};

/**
 * Which colour a beach gets. The City's own status is used when it gave one, otherwise the
 * API's band against the configured threshold. A result older than STALE_AFTER_DAYS is flagged
 * stale, so an old "green" is never mistaken for a current one.
 */
export function waterQualityStatus(wq, now = new Date(), withheld = false) {
  if (withheld) return { key: 'withheld', stale: false, ...STATUS.withheld };
  if (!wq) return { key: 'none', stale: false, ...STATUS.none };

  let key;
  const rating = (wq.rating || '').toLowerCase();
  if (rating.startsWith('green')) key = 'ok';
  else if (rating.startsWith('amber')) key = 'amber';
  else if (rating.startsWith('red')) key = 'red';
  else if (wq.band === 'ok') key = 'ok';
  else if (wq.band === 'exceeds') key = 'amber';
  else key = 'unknown';

  const stale = daysBetween(new Date(wq.sampledAt), now) > STALE_AFTER_DAYS;
  return { key, stale, ...STATUS[key] };
}
