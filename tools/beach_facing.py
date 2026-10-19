"""Works out which way each beach faces from the OpenStreetMap coastline, using only the standard library.

OSM draws the coastline with land on the left and sea on the right of the direction of travel, so the bearing that
points out to sea is the local coastline direction turned 90 degrees clockwise. For each beach this fetches the
coastline ways within a few hundred metres, averages the segment directions (weighted by length and closeness), and
reports how consistent they are. A large spread means the point sits on a corner, a harbour wall or a headland, and the
number should not be trusted without looking at a map.

usage: python tools/beach_facing.py [radius_metres]          (reads the beaches from the running API)
       python tools/beach_facing.py 400 --api http://localhost:8088

Map data (c) OpenStreetMap contributors, ODbL. This makes one small request per beach to the public Overpass API, so it is
meant to be run by hand now and then, not on a schedule.
"""
import json
import math
import sys
import time
import urllib.parse
import urllib.request

OVERPASSES = ["https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter"]
RADII = (150, 300, 500)


def bearing(lat1, lon1, lat2, lon2):
    """Compass bearing from point 1 to point 2, 0 to 360, 0 north and 90 east."""
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dl = math.radians(lon2 - lon1)
    y = math.sin(dl) * math.cos(p2)
    x = math.cos(p1) * math.sin(p2) - math.sin(p1) * math.cos(p2) * math.cos(dl)
    return (math.degrees(math.atan2(y, x)) + 360) % 360


def distance_m(lat1, lon1, lat2, lon2):
    r = 6371000
    p1, p2 = math.radians(lat1), math.radians(lat2)
    a = math.sin((p2 - p1) / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(math.radians(lon2 - lon1) / 2) ** 2
    return 2 * r * math.asin(math.sqrt(a))


def seaward_bearing(lat, lon, ways, radius):
    """Returns (bearing, spread_degrees, segments_used) or None when no coastline is close enough."""
    sx = sy = total = 0.0
    used = 0
    for way in ways:
        pts = way["geometry"]
        for a, b in zip(pts, pts[1:]):
            mid_lat, mid_lon = (a["lat"] + b["lat"]) / 2, (a["lon"] + b["lon"]) / 2
            d = distance_m(lat, lon, mid_lat, mid_lon)
            if d > radius:
                continue
            length = distance_m(a["lat"], a["lon"], b["lat"], b["lon"])
            if length < 1:
                continue
            theta = math.radians(bearing(a["lat"], a["lon"], b["lat"], b["lon"]))
            weight = length / (1 + d / 150)       # long segments and ones close to the beach count for more
            sx += weight * math.sin(theta)
            sy += weight * math.cos(theta)
            total += weight
            used += 1
    if used == 0 or total == 0:
        return None

    resultant = math.hypot(sx, sy) / total        # 1 means every segment points the same way
    along = (math.degrees(math.atan2(sx, sy)) + 360) % 360
    # circular standard deviation, in degrees: small when the coast is straight, large around corners
    spread = math.degrees(math.sqrt(-2 * math.log(max(resultant, 1e-9))))
    return (along + 90) % 360, spread, used


def fetch_coastline(lat, lon, radius):
    query = f"[out:json][timeout:25];way(around:{radius + 150},{lat},{lon})[natural=coastline];out geom;"
    data = urllib.parse.urlencode({"data": query}).encode()
    last = None
    for attempt in range(4):
        for server in OVERPASSES:
            request = urllib.request.Request(server, data=data, headers={"User-Agent": "CapeCoastMonitor/0.1 (one-off bearing check)"})
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    return [e for e in json.load(response)["elements"] if e.get("type") == "way"]
            except Exception as ex:   # the public servers are often busy, wait and try the next one
                last = ex
                time.sleep(4)
        time.sleep(10 * (attempt + 1))
    raise last


def angle_gap(a, b):
    d = abs(a - b) % 360
    return min(d, 360 - d)


def compass(deg):
    names = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"]
    return names[round(deg / 22.5) % 16]


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    radius = int(args[0]) if args else 400
    api = "http://localhost:8088"
    if "--api" in sys.argv:
        api = sys.argv[sys.argv.index("--api") + 1]

    with urllib.request.urlopen(api + "/api/locations", timeout=30) as response:
        beaches = json.load(response)["features"]

    print(f"{'beach':<12} " + " ".join(f"{r:>4}m" for r in RADII) + "   best   spread  verdict")
    for feature in beaches:
        lon, lat = feature["geometry"]["coordinates"]
        slug = feature["id"]
        try:
            ways = fetch_coastline(lat, lon, max(RADII))
        except Exception as ex:   # a failed lookup for one beach shouldn't hide the others
            print(f"{slug:<12} lookup failed: {ex}")
            continue

        results = [seaward_bearing(lat, lon, ways, r) for r in RADII]
        if any(r is None for r in results):
            print(f"{slug:<12} no coastline close enough")
            continue

        bearings = [r[0] for r in results]
        best, spread, _ = results[1]                      # the middle radius is the one used
        stable = max(angle_gap(a, b) for a in bearings for b in bearings) <= 20 and spread < 25
        verdict = "stable" if stable else "CHECK ON A MAP: it changes with how much coast is looked at"
        print(f"{slug:<12} " + " ".join(f"{b:5.0f}" for b in bearings) + f"  {best:5.0f} {compass(best):<3} {spread:5.0f}  {verdict}")
        time.sleep(3)   # be gentle with the public server


if __name__ == "__main__":
    main()
