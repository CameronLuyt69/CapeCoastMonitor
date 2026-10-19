-- Include rows that haven't been materialised yet, so a daily chart never lags behind the hourly data.
-- The refresh policy still keeps the materialised part current in the background.
ALTER MATERIALIZED VIEW marine_daily SET (timescaledb.materialized_only = false);
ALTER MATERIALIZED VIEW wind_daily SET (timescaledb.materialized_only = false);
