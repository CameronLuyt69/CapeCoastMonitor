-- Which way each beach faces, as a compass bearing towards the open sea. The surf estimate needs it to tell an
-- onshore wind from an offshore one and a swell that reaches the beach from one that doesn't.
--
-- The values seeded here are only a coarse default by coast: the Atlantic seaboard faces roughly west, and False Bay
-- opens roughly south. Individual beaches differ (the Simon's Town side of False Bay faces east, for example), so
-- treat these as placeholders and set real bearings through PUT /api/admin/locations/{id}.
ALTER TABLE locations
    ADD COLUMN facing_deg    real CHECK (facing_deg >= 0 AND facing_deg < 360),
    ADD COLUMN facing_source text NOT NULL DEFAULT 'unset' CHECK (facing_source IN ('unset', 'coast-default', 'manual'));

UPDATE locations
SET facing_deg = CASE coast WHEN 'Atlantic' THEN 270 WHEN 'False Bay' THEN 180 END,
    facing_source = 'coast-default';
