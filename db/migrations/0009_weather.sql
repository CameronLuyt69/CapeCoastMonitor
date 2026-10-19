-- Air temperature, rain, UV, visibility, cloud and humidity from the Open-Meteo forecast API (model values).
CREATE TABLE weather_observations (
    location_id        integer NOT NULL REFERENCES locations(id),
    source             text NOT NULL DEFAULT 'open-meteo-weather',
    observed_at        timestamptz NOT NULL,
    forecast_issued_at timestamptz NOT NULL,
    air_temp_c         real,
    precipitation_mm   real,
    uv_index           real,
    visibility_m       real,
    cloud_cover_pct    real,
    humidity_pct       real,
    quality_flags      text[] NOT NULL DEFAULT '{}',
    ingestion_run_id   bigint REFERENCES ingestion_runs(id),
    PRIMARY KEY (location_id, source, observed_at)
);
SELECT create_hypertable('weather_observations', 'observed_at', chunk_time_interval => INTERVAL '7 days');

ALTER TABLE weather_observations SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'location_id, source',
    timescaledb.compress_orderby = 'observed_at DESC');
SELECT add_compression_policy('weather_observations', INTERVAL '7 days');
SELECT add_retention_policy('weather_observations', INTERVAL '90 days');
