// Geometry for the trend chart. Pure functions: take data and a size, return numbers and path strings.

export const METRICS = [
  { key: 'wave_height', label: 'Wave height', unit: 'm', zeroBased: true },
  { key: 'swell_height', label: 'Swell height', unit: 'm', zeroBased: true },
  { key: 'swell_period', label: 'Swell period', unit: 's', zeroBased: true },
  { key: 'wind_speed', label: 'Wind speed', unit: 'km/h', zeroBased: true },
  { key: 'wind_gust', label: 'Wind gust', unit: 'km/h', zeroBased: true },
  { key: 'sea_temp', label: 'Sea temperature', unit: '°C', zeroBased: false },
];

/** Round-number tick positions covering [min, max]. */
export function niceTicks(min, max, target = 4) {
  if (!(max > min)) return [min];
  const rough = (max - min) / target;
  const pow = 10 ** Math.floor(Math.log10(rough));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * pow).find((s) => s >= rough) ?? 10 * pow;
  const ticks = [];
  for (let v = Math.ceil(min / step) * step; v <= max + step * 1e-9; v += step) ticks.push(Math.round(v / step) * step);
  return ticks;
}

/**
 * @param raw   [{t, value, ingestionRunId}] hourly values
 * @param daily [{t, min, max, avg}] one entry per UTC day
 */
export function buildChart({ raw, daily, from, to, width, height, margin, zeroBased }) {
  const m = { top: 10, right: 12, bottom: 24, left: 40, ...margin };
  const innerW = width - m.left - m.right;
  const innerH = height - m.top - m.bottom;
  const t0 = new Date(from).getTime();
  const t1 = new Date(to).getTime();

  const values = [
    ...raw.map((p) => p.value),
    ...daily.flatMap((d) => [d.min, d.max]),
  ].filter((v) => v != null && Number.isFinite(v));

  if (values.length === 0 || !(t1 > t0)) return { empty: true, width, height };

  let lo = Math.min(...values);
  let hi = Math.max(...values);
  if (zeroBased && lo > 0) lo = 0;
  if (hi === lo) hi = lo + 1;
  const pad = (hi - lo) * 0.06;
  hi += pad;
  if (!zeroBased || lo < 0) lo -= pad;

  const x = (t) => m.left + ((new Date(t).getTime() - t0) / (t1 - t0)) * innerW;
  const y = (v) => m.top + (1 - (v - lo) / (hi - lo)) * innerH;

  // a line has to break where a value is missing, otherwise it draws across the gap
  const segments = [];
  let current = [];
  for (const p of raw) {
    if (p.value == null) {
      if (current.length) segments.push(current);
      current = [];
    } else {
      current.push(`${x(p.t).toFixed(1)},${y(p.value).toFixed(1)}`);
    }
  }
  if (current.length) segments.push(current);
  const linePaths = segments.map((s) => (s.length === 1 ? `M${s[0]}l0.01,0` : `M${s.join('L')}`));

  // each day's min-max range is a band over that UTC day, clipped to the chart
  const bands = daily
    .filter((d) => d.min != null && d.max != null)
    .map((d) => {
      const start = new Date(d.t).getTime();
      const left = Math.max(x(start), m.left);
      const right = Math.min(x(start + 86400000), m.left + innerW);
      return { x: left, width: Math.max(right - left, 0), y: y(d.max), height: Math.max(y(d.min) - y(d.max), 1.5), day: d.t, min: d.min, max: d.max, avg: d.avg };
    })
    .filter((b) => b.width > 0);

  const yTicks = niceTicks(lo, hi).map((v) => ({ value: v, y: y(v) }));

  const dayTicks = [];
  for (let t = Math.ceil(t0 / 86400000) * 86400000; t <= t1; t += 86400000) dayTicks.push({ t, x: x(t) });

  return {
    empty: false, width, height, margin: m, innerW, innerH, lo, hi, x, y, linePaths, bands, yTicks, dayTicks,
    nowX: null,
  };
}

/** The raw point nearest in time to x, or null if nothing is within maxPixels. */
export function nearestPoint(chart, raw, px, maxPixels = 24) {
  let best = null;
  let bestDist = Infinity;
  for (const p of raw) {
    if (p.value == null) continue;
    const d = Math.abs(chart.x(p.t) - px);
    if (d < bestDist) { best = p; bestDist = d; }
  }
  return bestDist <= maxPixels ? best : null;
}

/** The sample closest in time to t, within maxDays, or null. */
export function nearestSample(samples, t, maxDays = 7) {
  const target = new Date(t).getTime();
  let best = null;
  let bestDist = Infinity;
  for (const s of samples) {
    const d = Math.abs(new Date(s.sampledAt).getTime() - target);
    if (d < bestDist) { best = s; bestDist = d; }
  }
  return best && bestDist <= maxDays * 86400000 ? best : null;
}
