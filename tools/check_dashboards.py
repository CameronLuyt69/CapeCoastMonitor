"""Runs every query of every provisioned Grafana dashboard through Grafana itself and reports errors.

usage: python tools/check_dashboards.py [grafana_url] [user] [password]
Defaults: http://localhost:3000, admin, and GRAFANA_ADMIN_PASSWORD from the environment.
Template variables are filled in with their first value ("All" for multi-value ones).
"""
import base64
import json
import os
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:3000"
user = sys.argv[2] if len(sys.argv) > 2 else "admin"
password = sys.argv[3] if len(sys.argv) > 3 else os.environ.get("GRAFANA_ADMIN_PASSWORD", "admin")
auth = "Basic " + base64.b64encode(f"{user}:{password}".encode()).decode()


def call(path, body=None):
    req = urllib.request.Request(base + path, data=json.dumps(body).encode() if body else None,
                                 headers={"Authorization": auth, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def run(sql, ds, fmt="table", time_range=("now-3d", "now")):
    body = {"from": time_range[0], "to": time_range[1], "queries": [
        {"refId": "A", "datasource": ds, "rawSql": sql, "format": fmt, "rawQuery": True, "editorMode": "code"}]}
    try:
        res = call("/api/ds/query", body)["results"]["A"]
    except urllib.error.HTTPError as e:
        return None, e.read().decode()[:400]
    if res.get("error"):
        return None, res["error"]
    rows = sum(len(f["values"][0]) if f.get("values") else 0 for f in res.get("frames", [])[:1] for f in [f.get("data", {})])
    return rows, None


found = call("/api/search?type=dash-db&tag=capecoast")
print(f"{len(found)} dashboards provisioned: {', '.join(d['title'] for d in found)}")
problems = 0

for item in found:
    dash = call(f"/api/dashboards/uid/{item['uid']}")["dashboard"]
    ds = None
    variables = {}
    for v in dash.get("templating", {}).get("list", []):
        rows, err = run(v["query"], v["datasource"], "table")
        if err:
            print(f"  [{dash['title']}] variable {v['name']}: ERROR {err}")
            problems += 1
            continue
        # first value of the variable query
        res = call("/api/ds/query", {"from": "now-1h", "to": "now", "queries": [
            {"refId": "A", "datasource": v["datasource"], "rawSql": v["query"], "format": "table", "rawQuery": True}]})
        values = res["results"]["A"]["frames"][0]["data"]["values"]
        first = values[1][0] if len(values) > 1 else values[0][0]
        variables[v["name"]] = v.get("allValue") if v.get("includeAll") and v.get("allValue") else str(first)

    for panel in dash["panels"]:
        for t in panel["targets"]:
            sql = t["rawSql"]
            for name, value in variables.items():
                sql = sql.replace(f"${name}", value)
            window = (dash.get("time", {}).get("from", "now-3d"), dash.get("time", {}).get("to", "now"))
            rows, err = run(sql, t["datasource"], t["format"], window)
            status = "ERROR " + err if err else f"ok ({rows} rows)"
            if err:
                problems += 1
            print(f"  [{dash['title']}] {panel['title']} / {t['refId']}: {status}")

print("problems:", problems)
sys.exit(1 if problems else 0)
