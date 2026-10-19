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
