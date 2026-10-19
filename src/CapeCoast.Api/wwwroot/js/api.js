const TIMEOUT_MS = 15000;

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

export const api = {
  locations: () => get('/api/locations'),
  status: () => get('/api/status'),
  run: (id) => get(`/api/runs/${id}`),
  waterQuality: (slug, limit = 10) => get(`/api/locations/${encodeURIComponent(slug)}/water-quality?${q({ limit })}`),
  history: (slug, metric, from, to, interval) =>
    get(`/api/locations/${encodeURIComponent(slug)}/history?${q({ metric, from: from.toISOString(), to: to.toISOString(), interval })}`),
};
