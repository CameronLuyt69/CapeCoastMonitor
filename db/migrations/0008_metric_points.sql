-- App metrics written by the OpenTelemetry exporter in the API and worker, charted from Grafana.
-- Counters are one row per tag set per interval. Histograms are .count, .sum, .max, .p50, .p95 and .p99.
CREATE TABLE metric_points (
    time       timestamptz      NOT NULL,
    service    text             NOT NULL,
    name       text             NOT NULL,
    attributes jsonb            NOT NULL DEFAULT '{}',
    value      double precision NOT NULL
);
SELECT create_hypertable('metric_points', 'time', chunk_time_interval => INTERVAL '1 day');
CREATE INDEX ix_metric_points_name_time ON metric_points (name, time DESC);
SELECT add_retention_policy('metric_points', INTERVAL '30 days');
