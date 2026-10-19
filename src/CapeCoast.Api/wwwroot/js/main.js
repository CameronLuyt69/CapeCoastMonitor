import { api, isStatic } from './api.js';
import { clear, el, link } from './dom.js';
import { daysAgoText, formatDate, formatDateTime, timeAgoText } from './format.js';
import { startLive } from './live.js';
import { createMap } from './map.js';
import { createPanel } from './panel.js';
import { initTheme } from './theme.js';

const SOURCE_NAMES = {
  'open-meteo-marine': 'Waves and sea temperature',
  'open-meteo-forecast': 'Wind',
  'cct-coastal-water-quality': 'Water quality report',
};

const liveBadge = document.getElementById('live');
const statusBar = document.getElementById('statusbar');
const panelRoot = document.getElementById('panel');
let features = [];

initTheme(document.getElementById('theme'));

const map = createMap(document.getElementById('map'), selectBeach);
const panel = createPanel(panelRoot, {
  onClose: () => {
    map.select(null);
    history.replaceState(null, '', location.pathname);
    map.resize();
  },
});

function selectBeach(slug) {
  const feature = features.find((f) => f.id === slug);
  if (!feature) return;
  map.select(slug);
  panel.open(feature);
  history.replaceState(null, '', `#beach=${encodeURIComponent(slug)}`);
  map.resize();
  // on narrow screens the panel sits below the map, so bring it into view
  if (window.matchMedia('(max-width: 860px)').matches) panelRoot.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

function setLive(state, detail = '') {
  const text = { live: 'Live updates', polling: 'Checking every minute', offline: 'Offline', snapshot: `Snapshot${detail}` }[state];
  liveBadge.textContent = text;
  liveBadge.dataset.state = state;
}

async function refresh() {
  try {
    const [locations, status] = await Promise.all([api.locations(), api.status().catch(() => null)]);
    // an export made without the City's results says so, and the panel and map need to know
    features = locations.features.map((f) => ({ ...f, withheld: locations.waterQualityIncluded === false }));
    map.update(features);
    if (panel.selected) {
      const current = features.find((f) => f.id === panel.selected);
      if (current) panel.update(current);
    }
    renderStatus(status, features);
    if (isStatic) setLive('snapshot', ` from ${formatDateTime(locations.serverTime)}`);
    else if (liveBadge.dataset.state === 'offline') setLive('polling');
  } catch {
    setLive('offline');
  }
}

function renderStatus(status, features) {
  clear(statusBar);
  if (!status) return;

  const items = [];
  for (const s of status.sources) {
    if (!s.enabled) continue;
    const label = SOURCE_NAMES[s.source] ?? s.source;
    const when = s.lastSuccessAt ? timeAgoText(s.lastSuccessAt) : 'never';
    items.push(el('li', { class: s.stale ? 'is-stale' : '' },
      el('strong', {}, `${label}: `), `checked ${when}`, s.stale ? ' (stale)' : '',
      s.source === 'cct-coastal-water-quality' ? newestSample(features) : null));
  }
  statusBar.append(el('h2', { class: 'visually-hidden' }, 'Data freshness'), el('ul', {}, items));
}

function newestSample(features) {
  const dates = features.map((f) => f.properties.waterQuality?.sampledAt).filter(Boolean).sort();
  if (!dates.length) return '';
  const newest = dates[dates.length - 1];
  return `; newest sample ${formatDate(newest)} (${daysAgoText(newest)})`;
}

await refresh();
if (isStatic) {
  // nothing changes in a snapshot, so no stream and no polling, and there is no API to point at
  document.querySelectorAll('[data-api-only]').forEach((node) => node.remove());
} else {
  startLive({ onUpdate: refresh, onState: setLive });
  setLive('polling');
}

// deep link: #beach=muizenberg
const wanted = new URLSearchParams(location.hash.slice(1)).get('beach');
if (wanted) selectBeach(wanted);
