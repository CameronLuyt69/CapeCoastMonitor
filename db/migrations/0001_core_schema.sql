CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE locations (
    id              serial PRIMARY KEY,
    name            text NOT NULL,
    slug            text NOT NULL UNIQUE,
    coast           text NOT NULL CHECK (coast IN ('Atlantic', 'False Bay')),
    city_site_code  text UNIQUE,
    geom            geometry(Point, 4326) NOT NULL,
    active          boolean NOT NULL DEFAULT true
);
CREATE INDEX ix_locations_geom ON locations USING gist (geom);

CREATE TABLE source_documents (
    id            bigserial PRIMARY KEY,
    source        text NOT NULL,
    url           text NOT NULL,
    sha256        text NOT NULL UNIQUE,
    stored_path   text,
    title         text,
    fetched_at    timestamptz NOT NULL DEFAULT now(),
    published_at  timestamptz
);

CREATE TABLE ingestion_runs (
    id             bigserial PRIMARY KEY,
    source         text NOT NULL,
    started_at     timestamptz NOT NULL DEFAULT now(),
    finished_at    timestamptz,
    status         text NOT NULL DEFAULT 'running'
                   CHECK (status IN ('running', 'succeeded', 'failed', 'flagged')),
    rows_fetched   integer NOT NULL DEFAULT 0,
    rows_written   integer NOT NULL DEFAULT 0,
    error_message  text,
    correlation_id text
);
CREATE INDEX ix_ingestion_runs_source_started ON ingestion_runs (source, started_at DESC);

CREATE TABLE marine_observations (
    location_id         integer NOT NULL REFERENCES locations(id),
    source              text NOT NULL DEFAULT 'open-meteo-marine',
    observed_at         timestamptz NOT NULL,
    forecast_issued_at  timestamptz NOT NULL,
    wave_height_m       real,
    swell_height_m      real,
    swell_period_s      real,
    swell_direction_deg real,
    wind_wave_height_m  real,
    sea_temp_c          real,
    sea_level_m         real,
    quality_flags       text[] NOT NULL DEFAULT '{}',
    ingestion_run_id    bigint REFERENCES ingestion_runs(id),
    PRIMARY KEY (location_id, source, observed_at)
);
SELECT create_hypertable('marine_observations', 'observed_at', chunk_time_interval => INTERVAL '7 days');

CREATE TABLE wind_observations (
    location_id        integer NOT NULL REFERENCES locations(id),
    source             text NOT NULL DEFAULT 'open-meteo-forecast',
    observed_at        timestamptz NOT NULL,
    forecast_issued_at timestamptz NOT NULL,
    speed_kmh          real,
    gust_kmh           real,
    direction_deg      real,
    quality_flags      text[] NOT NULL DEFAULT '{}',
    ingestion_run_id   bigint REFERENCES ingestion_runs(id),
    PRIMARY KEY (location_id, source, observed_at)
);
SELECT create_hypertable('wind_observations', 'observed_at', chunk_time_interval => INTERVAL '7 days');

CREATE TABLE water_quality_samples (
    id                    bigserial PRIMARY KEY,
    location_id           integer NOT NULL REFERENCES locations(id),
    sampled_at            timestamptz NOT NULL,
    published_at          timestamptz,
    enterococci_cfu_100ml integer,
    qualifier             text,
    rating                text,
    source_document_id    bigint REFERENCES source_documents(id),
    ingestion_run_id      bigint REFERENCES ingestion_runs(id),
    UNIQUE (location_id, sampled_at)
);
CREATE INDEX ix_wq_location_sampled ON water_quality_samples (location_id, sampled_at DESC);

CREATE TABLE alert_rules (
    id               serial PRIMARY KEY,
    name             text NOT NULL,
    metric           text NOT NULL,
    location_id      integer REFERENCES locations(id),   -- null means all locations
    rule_type        text NOT NULL DEFAULT 'static' CHECK (rule_type IN ('static', 'baseline')),
    operator         text NOT NULL CHECK (operator IN ('>', '>=', '<', '<=')),
    threshold        double precision NOT NULL,          -- baseline rules: percent deviation
    duration_minutes integer NOT NULL DEFAULT 0,
    severity         text NOT NULL DEFAULT 'warning' CHECK (severity IN ('info', 'warning', 'critical')),
    cooldown_minutes integer NOT NULL DEFAULT 60,
    enabled          boolean NOT NULL DEFAULT true
);

CREATE TABLE alert_events (
    id               bigserial PRIMARY KEY,
    rule_id          integer NOT NULL REFERENCES alert_rules(id),
    location_id      integer NOT NULL REFERENCES locations(id),
    opened_at        timestamptz NOT NULL DEFAULT now(),
    last_seen_at     timestamptz NOT NULL DEFAULT now(),
    resolved_at      timestamptz,
    last_notified_at timestamptz,
    peak_value       double precision,
    breach_count     integer NOT NULL DEFAULT 1
);
-- only one open incident per rule + location
CREATE UNIQUE INDEX ux_alert_events_open ON alert_events (rule_id, location_id) WHERE resolved_at IS NULL;

CREATE TABLE maintenance_windows (
    id          serial PRIMARY KEY,
    location_id integer REFERENCES locations(id),        -- null means everywhere
    starts_at   timestamptz NOT NULL,
    ends_at     timestamptz NOT NULL CHECK (ends_at > starts_at),
    reason      text
);
