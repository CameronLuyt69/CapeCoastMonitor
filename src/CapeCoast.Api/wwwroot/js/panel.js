import { api } from './api.js';
import { renderChart } from './chart.js';
import { METRICS, nearestSample } from './chart-model.js';
import { clear, el, link } from './dom.js';
import {
  compass, daysAgoText, daysBetween, formatDate, formatDateTime, formatResult, show,
  STALE_AFTER_DAYS, timeAgoText, waterQualityStatus,
} from './format.js';

const RANGE_BACK_DAYS = 7;
const RANGE_FORWARD_DAYS = 3;

export function createPanel(root, { onClose }) {
  let current = null;       // { feature, metric, samples, token }
  let token = 0;            // guards against a slow response from a beach the user has since left

  function open(feature) {
    current = { feature, metric: current?.feature?.id === feature.id ? current.metric : METRICS[0], samples: [] };
    root.hidden = false;
    render();
    load();
  }

  function close() {
    current = null;
    root.hidden = true;
    onClose();
  }

  function update(feature) {
    if (!current || current.feature.id !== feature.id) return;
    current.feature = feature;
    render();
    load();
  }

  // ---- rendering

  function render() {
    const { feature } = current;
    const p = feature.properties;
    clear(root);
    root.append(
      el('div', { class: 'panel-head' },
        el('div', {},
          el('h2', { id: 'panel-title', tabindex: '-1' }, p.name),
          el('span', { class: 'badge' }, `${p.coast} coast`)),
        el('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close beach details', onclick: close }, '✕')),
      waterQualityCard(p.waterQuality),
      conditionsCard(p),
      el('section', { class: 'card', 'aria-labelledby': 'trend-h' },
        el('h3', { id: 'trend-h' }, 'Trend'),
        el('label', { class: 'field' }, el('span', {}, 'Metric'),
          metricSelect()),
        el('div', { id: 'chart-host', class: 'chart-host' }, el('p', { class: 'muted' }, 'Loading chart…')),
        el('p', { class: 'muted small' },
          'Line: hourly model value. Shaded bars: each day’s minimum to maximum. ',
          'Triangles: water quality samples. Shaded right side: forecast.'),
        el('div', { id: 'point-details', class: 'details', 'aria-live': 'polite' })),
      el('section', { class: 'card', 'aria-labelledby': 'wq-h' },
        el('h3', { id: 'wq-h' }, 'Recent water quality results'),
        el('div', { id: 'wq-list' }, el('p', { class: 'muted' }, 'Loading…'))),
    );
    root.querySelector('#panel-title').focus({ preventScroll: true });
  }

  function metricSelect() {
    const select = el('select', { id: 'metric', onchange: (e) => {
      current.metric = METRICS.find((m) => m.key === e.target.value);
      loadChart();
    } }, METRICS.map((m) => el('option', { value: m.key, selected: m.key === current.metric.key }, `${m.label} (${m.unit})`)));
    return select;
  }

  function waterQualityCard(wq) {
    const now = new Date();
    const status = waterQualityStatus(wq, now);
    const card = el('section', { class: 'card', 'aria-labelledby': 'wqc-h' }, el('h3', { id: 'wqc-h' }, 'Water quality'));

    if (!wq) {
      card.append(el('p', {}, 'The City does not publish a sampling site for this beach in the report we use.'));
      return card;
    }

    const age = daysBetween(new Date(wq.sampledAt), now);
    card.append(
      el('p', { class: 'status-line' },
        el('span', { class: `pin pin-${status.key} pin-small ${status.stale ? 'pin-stale' : ''}`, 'aria-hidden': 'true' },
          el('span', { class: 'pin-glyph' }, status.glyph)),
        el('strong', {}, status.label)),
      el('p', { class: 'big' }, `${formatResult(wq.enterococciCfu100ml, wq.qualifier)}`,
        wq.enterococciCfu100ml != null ? el('span', { class: 'unit' }, ' enterococci per 100 ml') : null),
      el('p', {}, `Sampled ${daysAgoText(wq.sampledAt, now)} (${formatDate(wq.sampledAt)})`,
        wq.rating ? ` · City status: ${wq.rating}` : ''),
    );
    if (status.stale) {
      card.append(el('p', { class: 'warn' }, `This result is ${age} days old (more than ${STALE_AFTER_DAYS}). Treat it as history, not current conditions.`));
    }
    card.append(el('p', { class: 'muted small' },
      'Results reach the report about four days after sampling, so they are never live. ',
      wq.documentUrl ? ['Source: ', link(wq.documentUrl, 'City of Cape Town Coastal Water Quality Report (PDF)')] : ''));
    return card;
  }

  function conditionsCard(p) {
    const now = new Date();
    const card = el('section', { class: 'card', 'aria-labelledby': 'cond-h' }, el('h3', { id: 'cond-h' }, 'Conditions now (model)'));
    const grid = el('dl', { class: 'stats' });
    const row = (label, value, note) => grid.append(el('div', {}, el('dt', {}, label), el('dd', {}, value, note ? el('small', {}, ` ${note}`) : null)));

    const marine = p.marine;
    const wind = p.wind;
    if (marine) {
      row('Waves', show(marine.waveHeightM, 'm', 2));
      row('Swell', show(marine.swellHeightM, 'm', 2), `${show(marine.swellPeriodS, 's')} from ${compass(marine.swellDirectionDeg)} ${marine.swellDirectionDeg ?? ''}°`.trim());
      row('Sea temperature', show(marine.seaTempC, '°C'));
      row('Sea level (approx.)', show(marine.seaLevelMslApproxM, 'm', 2), 'model proxy, not a tide');
    }
    if (wind) {
      row('Wind', show(wind.speedKmh, 'km/h', 0), `from ${compass(wind.directionDeg)}`);
      row('Gusts', show(wind.gustKmh, 'km/h', 0));
    }
    if (!marine && !wind) {
      card.append(el('p', { class: 'muted' }, 'No model data has been ingested for this beach yet.'));
      return card;
    }
    card.append(grid);
    const at = marine?.observedAt ?? wind?.observedAt;
    const issued = marine?.forecastIssuedAt ?? wind?.forecastIssuedAt;
    card.append(el('p', { class: 'muted small' },
      `Modelled values for ${formatDateTime(at)}, fetched ${timeAgoText(issued, now)}. These are forecasts for the nearest grid cell, not measurements at the beach.`));
    return card;
  }

  // ---- data loading

  async function load() {
    const mine = ++token;
    const { feature } = current;
    const slug = feature.properties.slug;

    const wqPromise = api.waterQuality(slug, 10).catch(() => null);
    wqPromise.then((wq) => {
      if (mine !== token) return;
      current.samples = wq?.samples ?? [];
      renderSamples(wq);
      loadChart(mine);
    });
  }

  function renderSamples(wq) {
    const host = root.querySelector('#wq-list');
    if (!host) return;
    clear(host);
    if (!wq) {
      host.append(el('p', { class: 'warn' }, 'Could not load water quality results. ', el('button', { type: 'button', class: 'linkish', onclick: load }, 'Try again')));
      return;
    }
    if (!wq.samples.length) {
      host.append(el('p', { class: 'muted' }, 'No samples stored for this beach.'));
      return;
    }

    const now = new Date();
    const list = el('ol', { class: 'samples' });
    for (const s of wq.samples) {
      const st = waterQualityStatus({ sampledAt: s.sampledAt, rating: s.rating, band: s.band }, now);
      list.append(el('li', {},
        el('span', { class: `pin pin-${st.key} pin-small`, 'aria-hidden': 'true' }, el('span', { class: 'pin-glyph' }, st.glyph)),
        el('span', { class: 'sample-main' },
          el('strong', {}, formatResult(s.enterococciCfu100ml, s.qualifier)),
          el('span', { class: 'muted' }, ` · ${formatDate(s.sampledAt)} (${daysAgoText(s.sampledAt, now)})`)),
        el('span', { class: 'sample-links' },
          s.document?.url ? link(s.document.url, 'City report') : null,
          s.ingestionRunId ? link(`/api/runs/${s.ingestionRunId}`, `run #${s.ingestionRunId}`) : null)));
    }
    host.append(list,
      el('p', { class: 'muted small' }, `Single-sample guideline: below ${wq.thresholdCfu} cfu/100 ml is acceptable for recreational use, per the City. ` +
        'The City report holds up to ten results per site (often fewer), and older ones are only dated to the week they were reported.'));
  }

  async function loadChart(mine = token) {
    const host = root.querySelector('#chart-host');
    if (!host) return;
    clear(host).append(el('p', { class: 'muted' }, 'Loading chart…'));

    const slug = current.feature.properties.slug;
    const metric = current.metric;
    const now = new Date();
    const from = new Date(now.getTime() - RANGE_BACK_DAYS * 86400000);
    const to = new Date(now.getTime() + RANGE_FORWARD_DAYS * 86400000);

    try {
      const [raw, daily] = await Promise.all([
        api.history(slug, metric.key, from, to, 'raw'),
        api.history(slug, metric.key, from, to, 'day'),
      ]);
      if (mine !== token || metric !== current.metric) return;

      renderChart(host, {
        raw: raw.points, daily: daily.points, samples: current.samples, metric, from, to, now,
        onPoint: (point) => showPointDetails(point, metric),
        onSample: (sample) => showSampleDetails(sample),
      });
    } catch {
      if (mine !== token) return;
      clear(host).append(el('p', { class: 'warn' }, 'Could not load the chart. ',
        el('button', { type: 'button', class: 'linkish', onclick: () => loadChart() }, 'Try again')));
    }
  }

  // ---- contextual navigation: chart point -> run, nearest sample -> source document

  async function showPointDetails(point, metric) {
    const host = root.querySelector('#point-details');
    clear(host).append(
      el('h4', {}, `${metric.label}: ${show(point.value, metric.unit, 2)}`),
      el('p', { class: 'muted' }, formatDateTime(point.t)));

    if (point.flags?.length) {
      host.append(el('p', { class: 'warn' }, `Quality flags on this row: ${point.flags.join(', ')}`));
    }

    const runLine = el('p', {}, 'Loading the ingestion run…');
    host.append(runLine);
    if (point.ingestionRunId) {
      try {
        const run = await api.run(point.ingestionRunId);
        runLine.replaceChildren(
          'Written by ', link(`/api/runs/${run.id}`, `ingestion run #${run.id}`),
          ` (${run.source}, ${run.status}, finished ${run.finishedAt ? timeAgoText(run.finishedAt) : 'still running'}, ${run.rowsWritten} rows changed).`);
      } catch {
        runLine.textContent = `Written by ingestion run #${point.ingestionRunId} (details unavailable).`;
      }
    } else {
      runLine.textContent = 'No ingestion run is recorded for this row.';
    }

    const near = nearestSample(current.samples, point.t, 7);
    host.append(el('h4', {}, 'Nearest water quality sample'));
    if (near) host.append(sampleSummary(near, point.t));
    else host.append(el('p', { class: 'muted' }, 'No sample within 7 days of this point.'));
  }

  function showSampleDetails(sample) {
    const host = root.querySelector('#point-details');
    clear(host).append(el('h4', {}, 'Water quality sample'), sampleSummary(sample, null));
  }

  function sampleSummary(s, relativeTo) {
    const apart = relativeTo ? Math.abs(daysBetween(new Date(s.sampledAt), new Date(relativeTo))) : null;
    return el('p', {},
      el('strong', {}, `${formatResult(s.enterococciCfu100ml, s.qualifier)}`),
      s.enterococciCfu100ml != null ? ' cfu/100 ml' : '',
      ` on ${formatDate(s.sampledAt)}`,
      s.rating ? ` · ${s.rating}` : '',
      apart != null ? ` · ${apart} day${apart === 1 ? '' : 's'} from this point` : '',
      ' · ',
      s.document?.url ? link(s.document.url, 'source document') : 'no document',
      s.ingestionRunId ? [' · ', link(`/api/runs/${s.ingestionRunId}`, `ingestion run #${s.ingestionRunId}`)] : '');
  }

  return { open, close, update, get selected() { return current?.feature.id ?? null; } };
}
