import { STATUS, waterQualityStatus } from './format.js';

const CAPE_PENINSULA = [-34.06, 18.5];

// Marker colours come from CSS variables, shape and glyph carry the same meaning for colour-blind users.
function icon(status, selected) {
  const cls = ['pin', `pin-${status.key}`, status.stale ? 'pin-stale' : '', selected ? 'pin-selected' : ''].join(' ').trim();
  return L.divIcon({
    className: 'pin-wrap',
    html: `<span class="${cls}"><span class="pin-glyph">${status.glyph}</span></span>`,
    iconSize: [34, 34],
    iconAnchor: [17, 17],
  });
}

export function createMap(container, onSelect) {
  const map = L.map(container, { zoomControl: true, minZoom: 8, maxZoom: 17 }).setView(CAPE_PENINSULA, 10);

  L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
  }).addTo(map);

  addLegend(map);

  // beaches on the Atlantic seaboard sit close together, so shrink the pins when zoomed out
  const sizePins = () => map.getContainer().classList.toggle('zoom-low', map.getZoom() < 12);
  map.on('zoomend', sizePins);
  sizePins();

  const markers = new Map();
  let selectedSlug = null;
  let fitted = false;

  function statusOf(feature, now) {
    return waterQualityStatus(feature.properties.waterQuality, now);
  }

  return {
    update(features, now = new Date()) {
      const seen = new Set();
      for (const feature of features) {
        const { slug, name } = feature.properties;
        const [lon, lat] = feature.geometry.coordinates;
        const status = statusOf(feature, now);
        const label = `${name}: ${status.label}${status.stale ? ', result is old' : ''}`;
        seen.add(slug);

        let marker = markers.get(slug);
        if (!marker) {
          marker = L.marker([lat, lon], { icon: icon(status, slug === selectedSlug), title: label, alt: label, keyboard: true })
            .addTo(map)
            .on('click', () => onSelect(slug));
          markers.set(slug, marker);
        } else {
          marker.setIcon(icon(status, slug === selectedSlug));
          marker.options.title = label;
          marker.getElement()?.setAttribute('title', label);
        }
        marker._feature = feature;
      }
      for (const [slug, marker] of markers) {
        if (!seen.has(slug)) { marker.remove(); markers.delete(slug); }
      }
      if (!fitted && markers.size) {
        map.fitBounds(L.latLngBounds([...markers.values()].map((m) => m.getLatLng())).pad(0.15));
        fitted = true;
      }
    },

    select(slug, now = new Date()) {
      selectedSlug = slug;
      for (const [s, marker] of markers) {
        marker.setIcon(icon(statusOf(marker._feature, now), s === slug));
      }
      const marker = markers.get(slug);
      if (marker) map.panTo(marker.getLatLng(), { animate: true });
    },

    resize: () => map.invalidateSize(),
  };
}

function addLegend(map) {
  const control = L.control({ position: 'bottomleft' });
  control.onAdd = () => {
    // a <details> so it can fold away, closed by default on a narrow map where it would cover the beaches
    const box = L.DomUtil.create('details', 'legend');
    box.open = map.getSize().x >= 600;
    L.DomEvent.disableClickPropagation(box);

    const title = L.DomUtil.create('summary', 'legend-title', box);
    title.textContent = 'Legend: water quality (City of Cape Town)';

    for (const key of ['ok', 'amber', 'red', 'unknown', 'none']) {
      const row = L.DomUtil.create('div', 'legend-row', box);
      const pin = L.DomUtil.create('span', `pin pin-${key} pin-small`, row);
      pin.innerHTML = `<span class="pin-glyph">${STATUS[key].glyph}</span>`;
      const text = L.DomUtil.create('span', '', row);
      text.textContent = STATUS[key].label;
    }

    const stale = L.DomUtil.create('div', 'legend-row legend-note', box);
    const ring = L.DomUtil.create('span', 'pin pin-ok pin-stale pin-small', stale);
    ring.innerHTML = '<span class="pin-glyph">✓</span>';
    const note = L.DomUtil.create('span', '', stale);
    note.textContent = 'Dashed ring: result older than 14 days';
    return box;
  };
  control.addTo(map);
}
