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
