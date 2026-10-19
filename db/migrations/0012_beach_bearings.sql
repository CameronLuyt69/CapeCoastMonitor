-- Beach bearings (the direction you look to face the open sea), estimated from the OpenStreetMap coastline with
-- tools/beach_facing.py on 2026-10-20 and each result checked against the known geography. Rounded to 5 degrees,
-- and good to about 20 degrees at best: a beach is not a straight line and the model is coarse anyway.
-- Map data (c) OpenStreetMap contributors, ODbL.
--
-- Hout Bay is deliberately left without a bearing. The City's sampling point there is on the harbour wharf, so the
-- coastline around it describes harbour walls, not the bay. Set it by hand once you have chosen a value from a map.
ALTER TABLE locations DROP CONSTRAINT locations_facing_source_check;
ALTER TABLE locations ADD CONSTRAINT locations_facing_source_check
    CHECK (facing_source IN ('unset', 'coast-default', 'osm-coastline', 'manual'));

UPDATE locations AS l
SET facing_deg = v.deg, facing_source = 'osm-coastline'
FROM (VALUES
    ('clifton-4th', 325),
    ('camps-bay',   300),
    ('llandudno',   290),
    ('noordhoek',   270),
    ('muizenberg',  150),
    ('st-james',    130),
    ('fish-hoek',   100),
    ('seaforth',     35),
    ('strand',      230)
) AS v(slug, deg)
WHERE l.slug = v.slug;

UPDATE locations SET facing_deg = NULL, facing_source = 'unset' WHERE slug = 'hout-bay';
