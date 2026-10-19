# Alerting

Rules live in the database. After every ingestion run that wrote rows, the worker evaluates every enabled rule for every
beach it applies to, keeps one incident per rule and beach, and posts a message to an n8n webhook when an incident opens
or resolves. n8n decides where it goes (email, Telegram, Teams and so on).

## Rules

Manage them through `/api/admin/alert-rules` (Admin to change, Viewer to read).

| Field | Meaning |
|---|---|
| `metric` | `enterococci` or any name from `/api/metrics` (`wave_height`, `swell_height`, `swell_period`, `wind_speed`, `wind_gust`, `sea_temp`, ...) |
| `locationId` | One beach, or null for every beach |
| `ruleType` | `static` or `baseline` |
| `operator`, `threshold` | `>`, `>=`, `<`, `<=` and a number (for baseline rules, a percentage) |
| `durationMinutes` | Static rules only. The condition must hold for this long, 0 means "right now" |
| `severity` | `info`, `warning` or `critical`, passed through to the message |
| `cooldownMinutes` | Minimum time between messages for the same rule and beach |
| `enabled` | Off means no evaluation, and any open incident is closed |

**Static:** compares the newest value with the threshold. With a duration, every reading in the window must breach, and the window
has to be covered by data. Hourly data with a gap of more than two hours inside the window, or less history than the duration,
makes the rule *inconclusive* for now.

**Baseline:** compares the newest value with the average of the previous 30 days at that beach. `>` and `>=` mean "above the norm
by more than the threshold percent", `<` and `<=` mean "below the norm by more than that". At least 24 hourly samples (3 for water
quality) are needed, and a norm of zero is refused because a percentage of zero is undefined. Until there is enough history the
rule is inconclusive.

**Inconclusive is not the same as fine.** If a rule can't be judged (no fresh data, a gap, no baseline yet) nothing changes:
an open incident stays open and nothing is sent. Only a value that is checked and found within limits resolves an incident.

**Current means current.** Only readings at or before now count, so a big swell forecast for tomorrow does not fire today. Model data
older than 6 hours (ingestion has stalled) and water quality samples older than 14 days are treated as no data.

## Lifecycle

```
breach, no open incident      -> open incident, send "opened"        (unless cooldown or maintenance)
breach, incident open         -> update count and peak, send nothing
no breach, incident open      -> resolve, send "resolved"            (only if "opened" was delivered)
```

- **One open incident per rule and beach.** Enforced by a partial unique index, so even two workers evaluating at once can't
  open two.
- **Maintenance windows** (`/api/admin/maintenance-windows`, per beach or everywhere) suppress everything for the beach while
  active: nothing opens, updates, resolves or sends. If the breach is still there when the window ends, it opens then.
- **Cooldown:** a new incident is held back while the last message for that rule and beach is younger than `cooldownMinutes`,
  so a value hovering around the threshold doesn't message every hour. It opens on a later evaluation once the cooldown has passed.
- **Delivery is retried.** An incident is marked as notified only after the webhook accepted the message. An "opened" message that
  failed is sent on the next evaluation, and a failed "resolved" message is retried too (`resolution_notified_at`). An incident
  that was never announced is resolved silently.

Run the rules by hand at any time:

```bash
docker compose run --rm worker --evaluate-alerts
```

## Water quality threshold

The default rule (migration `0007`) is `enterococci > 240`, severity warning. The City states that a single result below
240 cfu/100 ml is acceptable for recreational use, quoting the South African Water Quality Guidelines for Coastal Marine Water,
Volume 2 (recreational use), and the same figure is printed on its Summer Dashboard. The 200 that some sources quote appears in
the City report's grading table as the 95th percentile limit for the "Good" category, which is a different measure from a single
sample. I checked these against the City's own pages and report, not against the 2012 guideline volume itself.

The value is an ordinary rule, so it can be changed or supplemented (for example a stricter 200 rule, or a critical rule for two
consecutive exceedances) without touching code. The API's map colouring uses `WaterQuality:SingleSampleThresholdCfu` (default 240).

## Webhook

`POST` to `Alerts__WebhookUrl` with `Content-Type: application/json`. If `Alerts__WebhookSecret` is set, the request also carries
`X-CCM-Signature: sha256=<hex HMAC-SHA256 of the raw body>`. Any 2xx counts as delivered. Other statuses and network errors are
retried (twice inside one attempt, then on the next evaluation).

```json
{
  "schemaVersion": 1,
  "event": "opened",
  "incidentId": 12,
  "severity": "warning",
  "rule": { "id": 1, "name": "Water quality above single-sample guideline", "type": "static",
            "metric": "enterococci", "operator": ">", "threshold": 240, "durationMinutes": 0 },
  "location": { "id": 6, "slug": "muizenberg", "name": "Muizenberg", "coast": "False Bay" },
  "value": 300,
  "unit": "cfu/100ml",
  "observedAt": "2026-10-15T00:00:00Z",
  "openedAt": "2026-10-19T10:50:12Z",
  "resolvedAt": null,
  "breachCount": 1,
  "peakValue": 300,
  "baseline": null,
  "summary": "Muizenberg: enterococci 300 cfu/100ml > 240 (warning, rule \"Water quality above single-sample guideline\")",
  "link": "http://localhost:8088/#beach=muizenberg",
  "disclaimer": "Portfolio demo, not a safety tool. Forecasts are modelled and water quality results are delayed."
}
```

- `event` is `opened` or `resolved`. A resolved message has `resolvedAt` set and a summary that starts with `Resolved:`.
- `baseline` is only present for baseline rules: `{ "mean": 1.2, "deviationPercent": 50 }`.
- `schemaVersion` changes only when a field is removed or renamed. New fields may appear at any time, so ignore what you don't know.

## n8n

`n8n/capecoast-alerts.workflow.json` is an exported workflow: webhook, a node that builds a subject and text, then email and
Telegram nodes. Import it, add your own credentials, then activate:

```bash
docker compose exec n8n n8n import:workflow --separate --input=/workflows
docker compose exec n8n n8n update:workflow --id=CapeCoastAlert1 --active=true
docker compose restart n8n
```

(On Windows with Git Bash, put `MSYS_NO_PATHCONV=1` in front so `/workflows` isn't rewritten into a Windows path.
You can also import the file from the n8n editor: Workflows, then Import from file.)

After importing, open the workflow in the editor at http://localhost:5678 and:

1. set the SMTP credential and the from/to addresses on **Send email**,
2. set the Telegram credential and your chat id on **Send Telegram message**,
3. delete whichever of the two you don't want.

The exported file has placeholder credential ids and addresses on purpose, nothing secret is stored in the repository.
