-- Has to run outside a transaction, like the other continuous aggregates.
CREATE MATERIALIZED VIEW weather_daily
WITH (timescaledb.continuous) AS
SELECT location_id,
       time_bucket(INTERVAL '1 day', observed_at) AS day,
       min(air_temp_c) AS air_temp_min, max(air_temp_c) AS air_temp_max, avg(air_temp_c) AS air_temp_avg,
       min(precipitation_mm) AS precip_min, max(precipitation_mm) AS precip_max, avg(precipitation_mm) AS precip_avg,
       sum(precipitation_mm) AS precip_sum,
       min(uv_index) AS uv_min, max(uv_index) AS uv_max, avg(uv_index) AS uv_avg,
       min(visibility_m) AS vis_min, max(visibility_m) AS vis_max, avg(visibility_m) AS vis_avg,
       min(cloud_cover_pct) AS cloud_min, max(cloud_cover_pct) AS cloud_max, avg(cloud_cover_pct) AS cloud_avg,
       min(humidity_pct) AS humidity_min, max(humidity_pct) AS humidity_max, avg(humidity_pct) AS humidity_avg,
       count(*) AS samples
FROM weather_observations
GROUP BY location_id, day
WITH NO DATA;

SELECT add_continuous_aggregate_policy('weather_daily',
    start_offset => INTERVAL '30 days', end_offset => INTERVAL '1 hour', schedule_interval => INTERVAL '1 hour');

-- real-time aggregation, so today's chart includes hours that haven't been materialised yet
ALTER MATERIALIZED VIEW weather_daily SET (timescaledb.materialized_only = false);
