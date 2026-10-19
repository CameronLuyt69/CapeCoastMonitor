ALTER TABLE marine_observations SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'location_id, source',
    timescaledb.compress_orderby = 'observed_at DESC');
ALTER TABLE wind_observations SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'location_id, source',
    timescaledb.compress_orderby = 'observed_at DESC');

SELECT add_compression_policy('marine_observations', INTERVAL '7 days');
SELECT add_compression_policy('wind_observations', INTERVAL '7 days');
SELECT add_retention_policy('marine_observations', INTERVAL '90 days');
SELECT add_retention_policy('wind_observations', INTERVAL '90 days');
