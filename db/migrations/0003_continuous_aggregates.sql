-- Has to run outside a transaction, timescale won't create a continuous aggregate inside one.
CREATE MATERIALIZED VIEW marine_daily
WITH (timescaledb.continuous) AS
SELECT location_id,
       time_bucket(INTERVAL '1 day', observed_at) AS day,
       min(wave_height_m) AS wave_min, max(wave_height_m) AS wave_max, avg(wave_height_m) AS wave_avg,
       min(swell_height_m) AS swell_min, max(swell_height_m) AS swell_max, avg(swell_height_m) AS swell_avg,
       min(sea_temp_c) AS sea_temp_min, max(sea_temp_c) AS sea_temp_max, avg(sea_temp_c) AS sea_temp_avg,
       count(*) AS samples
FROM marine_observations
GROUP BY location_id, day
WITH NO DATA;

CREATE MATERIALIZED VIEW wind_daily
WITH (timescaledb.continuous) AS
SELECT location_id,
       time_bucket(INTERVAL '1 day', observed_at) AS day,
       min(speed_kmh) AS speed_min, max(speed_kmh) AS speed_max, avg(speed_kmh) AS speed_avg,
       max(gust_kmh) AS gust_max,
       count(*) AS samples
FROM wind_observations
GROUP BY location_id, day
WITH NO DATA;

-- start_offset stays well inside the 90 day retention so dropped chunks never rewrite old buckets
SELECT add_continuous_aggregate_policy('marine_daily',
    start_offset => INTERVAL '30 days', end_offset => INTERVAL '1 hour', schedule_interval => INTERVAL '1 hour');
SELECT add_continuous_aggregate_policy('wind_daily',
    start_offset => INTERVAL '30 days', end_offset => INTERVAL '1 hour', schedule_interval => INTERVAL '1 hour');
