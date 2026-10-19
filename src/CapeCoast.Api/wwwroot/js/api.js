// Talks to the API, or in static mode (a snapshot hosted as plain files) reads the exported JSON.

const TIMEOUT_MS = 15000;
const STATIC_DIR = window.CCM?.staticData ?? null;

export const isStatic = STATIC_DIR !== null;

let snapshotTime = null;
const beachFiles = new Map();

async function get(path) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    const response = await fetch(path, { signal: controller.signal, headers: { Accept: 'application/json' } });
    if (!response.ok) throw new Error(`${path} returned ${response.status}`);
    return await response.json();
  } finally {
    clearTimeout(timer);
  }
}

const q = (params) => new URLSearchParams(params).toString();

function beachFile(slug) {
  if (!beachFiles.has(slug)) {
    const request = get(`${STATIC_DIR}/beaches/${encodeURIComponent(slug)}.json`);
    request.catch(() => beachFiles.delete(slug));   // so a retry asks again
    beachFiles.set(slug, request);
  }
  return beachFiles.get(slug);
}

export const api = {
  isStatic,

  /** What "now" means for the chart: the moment of the snapshot when running from one. */
  now: () => snapshotTime ?? new Date(),

  async locations() {
    const data = await get(isStatic ? `${STATIC_DIR}/locations.geojson` : '/api/locations');
    if (isStatic) snapshotTime = new Date(data.serverTime);
    return data;
  },

  status: () => get(isStatic ? `${STATIC_DIR}/status.json` : '/api/status'),

  async run(id) {
    if (!isStatic) return get(`/api/runs/${id}`);
    for (const pending of beachFiles.values()) {
      const file = await pending.catch(() => null);
      if (file?.runs?.[id]) return file.runs[id];
    }
    throw new Error('that run is not in the snapshot');
  },

  async waterQuality(slug, limit = 10) {
    if (!isStatic) return get(`/api/locations/${encodeURIComponent(slug)}/water-quality?${q({ limit })}`);
    const file = await beachFile(slug);
    // a snapshot made without the City's results says so instead of looking like an empty list
    return file.waterQuality ?? { withheld: true, samples: [], thresholdCfu: null };
  },

  async history(slug, metric, from, to, interval) {
    if (!isStatic) {
      return get(`/api/locations/${encodeURIComponent(slug)}/history?${q({ metric, from: from.toISOString(), to: to.toISOString(), interval })}`);
    }
    const entry = (await beachFile(slug)).history?.[metric];
    if (!entry) throw new Error(`no ${metric} in the snapshot`);
    return interval === 'raw' ? entry.raw : entry.day;
  },
};
