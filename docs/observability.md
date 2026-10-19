# Observability

## Logs

Serilog writes structured logs to the console and to Seq (http://localhost:5341). Seq has no login in this setup,
so compose publishes it on localhost only; set an admin password in Seq before exposing it anywhere.

Every log line from a request or a job carries a `CorrelationId`:

- **API requests:** the caller's `X-Correlation-Id` if it is a short plain identifier (letters, digits, `.`, `_`, `-`, up to 64),
  otherwise the request's trace id. It is returned in the `X-Correlation-Id` response header, tagged on the span as
  `correlation.id` and added to every log line, including the one-line request summary.
- **Scheduled jobs:** each job runs in its own span and the correlation id is that trace id, with a `Job` property.
  The same id is written to `ingestion_runs.correlation_id`, so a row in the pipeline dashboard can be looked up in Seq:
  `CorrelationId = '<id>'` shows every log line of that run, and the spans are under the same trace.

## Traces

OpenTelemetry spans for API requests (the event stream and static files are left out), outbound HTTP calls, each job
(`job marine-ingest` and so on), each ingestion run (`ingestion.run`, tagged with source, run id and rows written) and each
alert evaluation (`alerts.evaluate`). They are sent to Seq's OTLP endpoint (`Telemetry__OtlpEndpoint`), where they sit
alongside the logs. Leave the setting empty to keep traces local.

## Metrics

| Name | Type | Tags | Meaning |
|---|---|---|---|
| `ccm.ingestion.run.duration` | histogram (s) | source, outcome | Duration of an ingestion run |
| `ccm.ingestion.runs` | counter | source, outcome | Runs by outcome, so **failures per source** is `outcome = failed` |
| `ccm.ingestion.rows.fetched` | counter | source | Rows received |
| `ccm.ingestion.rows.written` | counter | source | Rows inserted or changed |
| `ccm.job.duration` | histogram (s) | job, outcome | Wall time of each scheduled job |
| `ccm.alerts.events` | counter | event, severity | Incidents opened and resolved |
| `ccm.alerts.notify.failures` | counter | | Messages the webhook did not accept |
| `http.server.request.duration` | histogram (s) | route | API latency (ASP.NET Core's standard metric) |

**Where they go.** The stack has no metrics server, so a small exporter (`PostgresMetricExporter`) writes them into the
`metric_points` hypertable every 30 seconds, kept for 30 days, and Grafana reads them from the same Postgres as everything else.
It uses delta temporality, so each row describes one interval. Counters are one row per tag set. Each histogram becomes
`.count`, `.sum`, `.max`, `.p50`, `.p95` and `.p99` rows.

The percentiles are **estimates**: they are interpolated from the histogram buckets for that 30 second interval, and an
estimate from the default bucket boundaries is coarse at the very low end (below 5 ms). Latency is summarised per route
(methods and status codes merged) and as `scope = all`, which leaves out `/api/stream` because a stream stays open for as
long as a browser does.

If you would rather have Prometheus, the meters are ordinary OpenTelemetry meters, so adding an exporter in
`TelemetryExtensions` is all it takes. `Telemetry__Enabled=false` turns metrics and traces off.

## What the status page covers

`/api/status`, the freshness table on the pipeline dashboard and the stale-source warning in the logs cover four sources: marine
(`open-meteo-marine`), wind (`open-meteo-forecast`), weather (`open-meteo-weather`: air temperature, rain, UV, visibility, cloud and
humidity) and the City report (`cct-coastal-water-quality`). Each has its own run history, so a failure of one doesn't hide the others.

## Grafana

http://localhost:3000, user `admin`, password `GRAFANA_ADMIN_PASSWORD`. The Postgres datasource and the three dashboards
are provisioned from `docker/grafana` and cannot be edited in the UI (change the JSON and they reload within 30 seconds).

| Dashboard | Shows |
|---|---|
| Pipeline health | Freshness per source, runs per hour by outcome, rows per run, run and job duration, API latency percentiles, failed and flagged runs with their correlation ids, open alert incidents |
| Beach trends | One beach: waves with daily min and max, swell, wind and gusts, directions, sea temperature, the sea level proxy, a data quality flag count, and alert incidents as annotations |
| Water quality history | Enterococci per sample against the alert threshold, age of the newest result, the latest result per beach with links to the source document and ingestion run, samples per week |

`python tools/check_dashboards.py` runs every query of every dashboard through Grafana and reports any that fail, which is
handy after editing a dashboard or the schema (`GRAFANA_ADMIN_PASSWORD=... python tools/check_dashboards.py`).
