# Surf estimate

A rough guess at how good the surf might be, worked out from the **forecast** swell and wind and the direction the beach faces.

> **Estimate from the forecast, not a surf report.** It comes from model values for the nearest grid cell, it uses a coarse
> beach direction, and it is nobody's official rating. It is a derived number that can be wrong. Don't plan around it.

It is not scraped from or compared with any surf forecasting site, and nothing is stored: it is calculated when the data is read
(`SurfScore` in `CapeCoast.Core`) from the latest hourly values, and appears as `surf` in `GET /api/locations`.

## Inputs

| Input | Where it comes from |
|---|---|
| Swell height (m), swell period (s), swell direction (°, where it comes **from**) | Open-Meteo marine model, current hour |
| Wind speed (km/h), wind direction (°, where it comes **from**) | Open-Meteo forecast model, current hour |
| Beach bearing (°) | `locations.facing_deg`: the compass bearing you look along to face the open sea |

If **any** input is missing the score is `null`, the label is `null` and the response lists what is missing (`missing`).
Nothing is guessed or filled in.

## Formula (0 to 100)

1. **Exposure.** `exposure = max(0, cos(angle between the swell's direction and the beach bearing))`. A swell coming straight
   from the direction the beach faces has exposure 1. One from the side, or from behind, has 0.
2. **Swell size, up to 40 points.** The effective height is `swell height × exposure`, turned into points by straight lines
   between these points, flat beyond the ends (so a huge swell scores 0, it is too big):

   | Effective height (m) | 0 | 0.3 | 0.6 | 1.0 | 1.5 | 2.5 | 3.5 | 5 | 7 |
   |---|---|---|---|---|---|---|---|---|---|
   | Points | 0 | 5 | 15 | 28 | 38 | 40 | 30 | 12 | 0 |

3. **Rideable.** `rideable = min(1, size points / 15)`. Period and wind only count as far as there is something to ride, so a
   flat sea with perfect wind, or a tiny swell with a long period, doesn't score well.
4. **Swell period, up to 30 points.** From the period by straight lines, then `× min(1, 2 × exposure) × rideable`:

   | Period (s) | 6 | 8 | 10 | 12 | 14 | 18 |
   |---|---|---|---|---|---|---|
   | Points | 0 | 10 | 20 | 26 | 30 | 30 |

5. **Wind, up to 30 points.** Offshore wind blows from the land towards the sea, so it comes from the direction opposite the
   beach bearing. `alignment = cos(angle between the wind's direction and bearing + 180°)`: 1 is offshore, 0 is along the
   beach, -1 is onshore. Then `quality` is 1 for wind up to 8 km/h, otherwise
   `clamp(0.5 + 0.5 × alignment × min(speed, 30)/30 − 0.5 × max(0, speed − 30)/30, 0, 1)`. Points are `30 × quality × rideable`.
6. **Total** is the three parts added and rounded, 0 to 100.

| Score | Label |
|---|---|
| 0 to 24 | Poor |
| 25 to 49 | Fair |
| 50 to 74 | Good |
| 75 to 100 | Great |

### Worked example

A beach facing west (270°). Swell 2.0 m, 10 s, from 315°. Wind 15 km/h from the east (90°).

- Swell is 45° off the beach, so exposure is cos 45° = 0.707 and the effective height is 1.414 m. Between (1.0 m, 28) and (1.5 m, 38)
  that is 28 + (0.414 / 0.5) × 10 = **36.3** size points.
- 10 s is 20 points. Exposure is above 0.5 so it isn't reduced, and size points are above 15, so rideable is 1: **20**.
- The wind comes from exactly offshore, alignment 1. Quality is 0.5 + 0.5 × 15/30 = 0.75, so **22.5** points.
- 36.3 + 20 + 22.5 = 78.8, which rounds to **79, Great**.

These and many more cases are in `tests/CapeCoast.Tests/SurfScoreTests.cs`, each worked out by hand.

## Which way each beach faces

The model needs a bearing per beach, and the seed data only has a **crude default by coast**: 270° (west) for the Atlantic
seaboard and 180° (south) for False Bay. Real beaches differ, for example the Simon's Town side of False Bay faces east. The
API shows where each value came from (`facingSource`: `coast-default`, `manual` or `unset`) and the panel says so.

Set a real bearing (read it from a map, then check it) with the admin API, signed in as Admin:

```bash
curl -b cookies.txt -X PUT http://localhost:8088/api/admin/locations/6 \
  -H "Content-Type: application/json" \
  -d '{"name":"Muizenberg","coast":"False Bay","lon":18.470834,"lat":-34.108983,"citySiteCode":"CS34","facingDeg":200}'
```

Leaving `facingDeg` out of an update keeps the current bearing. A new beach without one has no bearing and therefore no score.

## What it ignores

Tide, wave quality at a particular break, local reef or sandbank shape, wind that varies across the bay, crowds, water quality and
safety. It judges only swell size, period, direction and wind against one bearing. Treat it as a way to compare beaches and hours
against each other, not as a verdict.
