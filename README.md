# Cape Coast Monitor

Wave, wind and coastal water quality for Cape Town beaches on one map, with history and alerts.

Work in progress. Sections to come: architecture, setup, design decisions, limitations, attribution.

## Running it

```
cp .env.example .env     # then edit the passwords
docker compose up --build
```

| Service | URL |
|---|---|
| API | http://localhost:8088 |
| Grafana | http://localhost:3000 |
| Seq | http://localhost:5341 |
| n8n | http://localhost:5678 |

## Data notes

- Open-Meteo returns values for the nearest model grid cell, not the exact beach. The marine grid is coarse
  (the Camps Bay request, for example, snapped to a point about 6 km away), so treat per-beach marine values as regional.
- `docs/probes/` holds raw sample responses used to confirm field names before the adapters were written.
- Ingestion keeps rows with missing or out-of-range values, stores them as NULL with entries in `quality_flags`,
  and marks the run `flagged`. Nothing is interpolated.

## Water quality data

Source: City of Cape Town, [Coastal Water Quality Report](https://www.capetown.gov.za/Work%20and%20business/Commercial-health-and-safety/Environmental-health/coastal-water-quality) (PDF).
The City owns this data. The site's terms allow personal, non-commercial use with credit and don't mention automated
access, so the worker fetches one file a day, identifies itself as a non-commercial project, and skips re-parsing when the file hash is unchanged.
The PDFs are kept on the worker's volume only and are not in this repository; the API links to the City's own URL.

- **Why this PDF and not the Summer Dashboard:** the dashboard gives ten results per site but only one date. The report has
  site codes, coordinates, the latest dated result and status, and previous samples by week.
- **No structured feed:** the City's ArcGIS server (`citymaps.capetown.gov.za`) only exposes inland water quality, so the PDF is the only route found.
- **Parsing:** PdfPig word positions, not text extraction (the PDF is a printed dashboard and text order is scrambled).
  A parse is only written if at least 30 site rows were found and 90% of rows were clean. Otherwise the run is marked `flagged`,
  the document is still stored, and nothing is written. `tools/PdfProbe` dumps word positions or runs the parser on a file.
- **Qualifiers:** `>2419` and `<10` keep their number with the qualifier in its own column. `ND` and `TFD` store no number, only the qualifier.
- **Dates:** the most recent result has its real sample date. Older results are only labelled with the Monday of their sampling week,
  and are stored under that date. The week column covering the latest sample is that same sample, so it is not stored twice.
- **Delay:** results arrive about four days after sampling, and the report itself can be much older than that. It is not live data.
- **Which sites:** ten City sites stand in for the ten beaches (see `db/migrations/0005_city_site_codes.sql`). That mapping is my choice.
- **Guideline value:** the City states that a single result below 240 cfu/100 ml is acceptable for recreational use
  (South African Water Quality Guidelines for Coastal Marine Water, Vol 2). Alert thresholds stay configurable.

## API

Swagger UI is at `/swagger`. Everything under `/api` is public and read-only except `/api/admin`.

| Endpoint | What it returns |
|---|---|
| `GET /api/locations` | GeoJSON FeatureCollection, latest marine, wind and water quality per beach, each with its own timestamps |
| `GET /api/locations/{slug}/history?metric=&from=&to=&interval=` | Time series. `interval` is `raw`, `day` or `week` (default picks one from the range) |
| `GET /api/locations/{slug}/water-quality?limit=` | Recent samples, each with its source document and ingestion run |
| `GET /api/runs/{id}` | One ingestion run, to trace a point back to the job that wrote it |
| `GET /api/metrics` | Metric names `history` accepts |
| `GET /api/health`, `GET /api/status` | Database check, per-source freshness and recent runs |
| `GET /api/stream` | Server-Sent Events, an `update` event when a run writes new rows |
| `POST /api/auth/login` | Sign in (cookie). Roles: Viewer reads admin data, Admin changes it |

Design notes:

- **Aggregates:** `interval=day|week` reads the daily continuous aggregates (min, max, avg) where the metric has one,
  and aggregates raw rows otherwise. Real-time aggregation is on, so recent hours show up before the refresh policy runs.
  Raw data is capped at 31 days per request and aggregated data at 400.
- **Current values:** "latest" means the newest row at or before now, so forecast hours still in the future are never shown as current.
- **SSE:** the worker calls `pg_notify` when a run writes rows and the API listens, so the two processes need no queue.
- **Auth:** cookie sessions (HttpOnly, SameSite=Strict). A role with no password in the environment cannot be signed in to.
  Login is rate limited, as is every public route, and each client may hold a few streams open.
- **Water quality band:** `ok`, `exceeds` or `unknown` against `WaterQuality:SingleSampleThresholdCfu` (default 240), not a hard-coded value.
- **Tide:** the marine model's sea level is returned as `seaLevelMslApproxM` and is not official tide data.

## Frontend

Plain JavaScript (ES modules, no build step) served by the API from `wwwroot`. Leaflet 1.9.4 is vendored in `wwwroot/vendor/leaflet`
(BSD-2, licence included) so the page loads no third-party scripts; the only external request is OpenStreetMap tiles, and a
Content-Security-Policy enforces that.

- **Map:** markers are coloured by the City's water quality status, with a distinct glyph and shape per status so colour isn't the
  only signal. A dashed ring marks a result older than 14 days, so an old green is never mistaken for a current one.
- **Panel:** latest water quality with its age and source link, model conditions, a trend chart and the recent samples.
- **Chart:** an SVG chart (no chart library) with the hourly line, each day's min to max as a band, the forecast region shaded and
  water quality samples marked on the axis. Click, tap or use the arrow keys and Enter on a point to see the ingestion run that wrote
  it and the nearest water quality sample with its source document.
- **Live updates:** Server-Sent Events trigger a refetch; if the stream is refused or drops it falls back to polling every minute
  and retries the stream every two minutes.
- **Deep links:** `#beach=muizenberg` opens a beach.
- **Theme:** light and dark, following the system setting until the visitor picks one (stored in `localStorage`, optional).
- **Tests:** the formatting and chart geometry are pure functions covered by `node --test "tests/frontend/*.test.mjs"` (Node 22+).
