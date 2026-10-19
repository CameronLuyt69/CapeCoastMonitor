-- A resolved message that couldn't be delivered is retried, so it needs its own marker.
ALTER TABLE alert_events ADD COLUMN resolution_notified_at timestamptz;

-- The default rule: the City's single-sample guideline. Results below 240 cfu/100 ml are acceptable for
-- recreational use per the City's page (SA Water Quality Guidelines for Coastal Marine Water, Vol 2).
-- Some sources quote 200, so this is an ordinary rule: change or add rules through /api/admin/alert-rules.
INSERT INTO alert_rules (name, metric, rule_type, operator, threshold, severity, cooldown_minutes)
VALUES ('Water quality above single-sample guideline', 'enterococci', 'static', '>', 240, 'warning', 1440);
