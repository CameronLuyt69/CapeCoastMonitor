-- Coordinates are my approximate beach positions. city_site_code stays null until the
-- City's Coastal Water Quality Report has been parsed in phase 3, nothing is guessed here.
INSERT INTO locations (name, slug, coast, geom) VALUES
 ('Clifton 4th',   'clifton-4th', 'Atlantic',  ST_SetSRID(ST_MakePoint(18.3775, -33.9395), 4326)),
 ('Camps Bay',     'camps-bay',   'Atlantic',  ST_SetSRID(ST_MakePoint(18.3780, -33.9515), 4326)),
 ('Llandudno',     'llandudno',   'Atlantic',  ST_SetSRID(ST_MakePoint(18.3400, -34.0085), 4326)),
 ('Hout Bay',      'hout-bay',    'Atlantic',  ST_SetSRID(ST_MakePoint(18.3470, -34.0480), 4326)),
 ('Noordhoek',     'noordhoek',   'Atlantic',  ST_SetSRID(ST_MakePoint(18.3730, -34.1085), 4326)),
 ('Muizenberg',    'muizenberg',  'False Bay', ST_SetSRID(ST_MakePoint(18.4710, -34.1090), 4326)),
 ('St James',      'st-james',    'False Bay', ST_SetSRID(ST_MakePoint(18.4630, -34.1230), 4326)),
 ('Fish Hoek',     'fish-hoek',   'False Bay', ST_SetSRID(ST_MakePoint(18.4330, -34.1370), 4326)),
 ('Seaforth',      'seaforth',    'False Bay', ST_SetSRID(ST_MakePoint(18.4380, -34.1920), 4326)),
 ('Strand',        'strand',      'False Bay', ST_SetSRID(ST_MakePoint(18.8230, -34.1130), 4326));
