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
