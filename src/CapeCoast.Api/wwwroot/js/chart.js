import { el, clear } from './dom.js';
import { buildChart, nearestPoint } from './chart-model.js';
import { formatDateTime, show } from './format.js';

const WIDTH = 560;
const HEIGHT = 230;

/**
 * Draws the trend chart into `container` and returns { select(point) }.
 * Layers, back to front: forecast shading, daily min-max bands, grid, hourly line, water quality
 * sample markers, "now" line, and a hover/keyboard cursor.
 */
export function renderChart(container, { raw, daily, samples, metric, from, to, now, onPoint, onSample }) {
  clear(container);

  const chart = buildChart({ raw, daily, from, to, width: WIDTH, height: HEIGHT, zeroBased: metric.zeroBased });
  if (chart.empty) {
    container.append(el('p', { class: 'muted empty' }, 'No data in this range yet.'));
    return { select() {} };
  }

  const svg = el('svg:svg', {
    viewBox: `0 0 ${WIDTH} ${HEIGHT}`, class: 'chart', role: 'img', tabindex: '0',
    'aria-label': `${metric.label} over the last 7 days and the next 3, hourly line with daily minimum to maximum range`,
  });

  const { margin: m, innerW, innerH } = chart;
  const nowX = chart.x(now);

  // forecast region
  svg.append(el('svg:rect', { class: 'chart-forecast', x: nowX, y: m.top, width: Math.max(m.left + innerW - nowX, 0), height: innerH }));

  // daily min-max bands, so spikes and lulls show even where the line is thin
  for (const b of chart.bands) {
    const rect = el('svg:rect', { class: 'chart-band', x: b.x, y: b.y, width: b.width, height: b.height, rx: 2 });
    rect.append(el('svg:title', {}, `${new Date(b.day).toISOString().slice(0, 10)}: ${show(b.min, metric.unit, 2)} to ${show(b.max, metric.unit, 2)}`));
    svg.append(rect);
  }

  for (const t of chart.yTicks) {
    svg.append(el('svg:line', { class: 'chart-grid', x1: m.left, x2: m.left + innerW, y1: t.y, y2: t.y }));
    svg.append(el('svg:text', { class: 'chart-label', x: m.left - 6, y: t.y + 3, 'text-anchor': 'end' }, String(+t.value.toFixed(2))));
  }
  svg.append(el('svg:text', { class: 'chart-label', x: 4, y: m.top + 4 }, metric.unit));

  for (const d of chart.dayTicks) {
    svg.append(el('svg:line', { class: 'chart-grid chart-grid-v', x1: d.x, x2: d.x, y1: m.top, y2: m.top + innerH }));
    const day = new Date(d.t);
    svg.append(el('svg:text', { class: 'chart-label', x: d.x + 3, y: HEIGHT - 8 }, `${day.getUTCDate()}/${day.getUTCMonth() + 1}`));
  }

  for (const path of chart.linePaths) svg.append(el('svg:path', { class: 'chart-line', d: path }));

  svg.append(el('svg:line', { class: 'chart-now', x1: nowX, x2: nowX, y1: m.top, y2: m.top + innerH }));
  svg.append(el('svg:text', { class: 'chart-label chart-now-label', x: nowX + 3, y: m.top + 10 }, 'now'));

  // water quality samples inside the range, as markers on the time axis
  for (const s of samples) {
    const t = new Date(s.sampledAt).getTime();
    if (t < new Date(from).getTime() || t > new Date(to).getTime()) continue;
    const x = chart.x(t);
    const y = m.top + innerH;
    const marker = el('svg:path', {
      class: `chart-sample chart-sample-${s.band}`, d: `M${x - 6},${y} L${x + 6},${y} L${x},${y - 11} Z`,
      tabindex: '0', role: 'button', 'aria-label': `Water quality sample from ${s.sampledAt.slice(0, 10)}, open details`,
      onclick: () => onSample(s), onkeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSample(s); } },
    });
    marker.append(el('svg:title', {}, `Water quality sample ${s.sampledAt.slice(0, 10)}`));
    svg.append(marker);
  }

  const cursor = el('svg:g', { class: 'chart-cursor', visibility: 'hidden' },
    el('svg:line', { class: 'chart-cursor-line', y1: m.top, y2: m.top + innerH }),
    el('svg:circle', { class: 'chart-cursor-dot', r: 4 }));
  svg.append(cursor);

  const readout = el('div', { class: 'chart-readout muted', 'aria-live': 'polite' }, 'Hover, tap or use the arrow keys, then press Enter for details.');

  let focus = null;
  const moveCursor = (point) => {
    if (!point) { cursor.setAttribute('visibility', 'hidden'); return; }
    focus = point;
    const x = chart.x(point.t);
    cursor.setAttribute('visibility', 'visible');
    cursor.firstChild.setAttribute('x1', x);
    cursor.firstChild.setAttribute('x2', x);
    cursor.lastChild.setAttribute('cx', x);
    cursor.lastChild.setAttribute('cy', chart.y(point.value));
    readout.textContent = `${formatDateTime(point.t)}: ${show(point.value, metric.unit, 2)}`;
  };

  const toChartX = (event) => {
    const rect = svg.getBoundingClientRect();
    return ((event.clientX - rect.left) / rect.width) * WIDTH;
  };

  const usable = raw.filter((p) => p.value != null);

  svg.addEventListener('pointermove', (e) => moveCursor(nearestPoint(chart, raw, toChartX(e), 40)));
  svg.addEventListener('click', (e) => {
    const point = nearestPoint(chart, raw, toChartX(e), 40);
    if (point) { moveCursor(point); onPoint(point); }
  });
  svg.addEventListener('keydown', (e) => {
    if (!usable.length) return;
    const index = focus ? usable.indexOf(focus) : usable.findIndex((p) => new Date(p.t) >= now);
    const step = e.shiftKey ? 24 : 1;
    if (e.key === 'ArrowRight') { e.preventDefault(); moveCursor(usable[Math.min(Math.max(index, 0) + step, usable.length - 1)]); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); moveCursor(usable[Math.max(Math.max(index, 0) - step, 0)]); }
    else if (e.key === 'Enter' && focus) { e.preventDefault(); onPoint(focus); }
  });

  container.append(svg, readout);
  return { select: moveCursor };
}
