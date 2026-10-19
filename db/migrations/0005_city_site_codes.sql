-- Site codes and coordinates below are the ones published in the City's Coastal Water Quality
-- Report (site table, page 1 and 2). Which City site stands in for each beach is my choice;
-- where the report lists several points on one beach I picked the most central swimming spot.
UPDATE locations AS l SET city_site_code = v.code, geom = ST_SetSRID(ST_MakePoint(v.lon, v.lat), 4326)
FROM (VALUES
  ('clifton-4th', 'CN09',  18.374417,    -33.940619),     -- Clifton Fourth Beach
  ('camps-bay',   'CN41',  18.37711225,  -33.95092422),   -- Camps Bay Central
  ('llandudno',   'XCN03', 18.34031,     -34.007816),     -- Llandudno
  ('hout-bay',    'HB13',  18.34918168,  -34.04741935),   -- Hout Bay Mariners Wharf
  ('noordhoek',   'CS40',  18.35242828,  -34.09751307),   -- Noordhoek Beach (Hoek)
  ('muizenberg',  'CS34',  18.470834,    -34.108983),     -- Muizenberg Surfers Corner
  ('st-james',    'CS04',  18.459533,    -34.118862),     -- St James Tidal Pool
  ('fish-hoek',   'CS41',  18.43275173,  -34.1394567),    -- Fish Hoek Beach
  ('seaforth',    'XCS13', 18.44663889,  -34.19394444),   -- Seaforth Beach
  ('strand',      'XCS26', 18.81753333,  -34.10616667)    -- Strand Lifesaving Club
) AS v(slug, code, lon, lat)
WHERE l.slug = v.slug;
