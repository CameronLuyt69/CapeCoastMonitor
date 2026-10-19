<!--
DRAFT. Written as a starting point from what actually happened while building this, so the facts are right but the voice is not
yours yet. Rewrite it in your own words, drop what doesn't ring true, and fill in the [bracketed] bits only you can say.
When it's ready, move it into the README (or your portfolio page) and delete this file.
-->

# What I learned

**Look at the data before writing the parser.** I assumed the City's water quality report would be a tidy table. It's a printed
dashboard whose text comes out scrambled, and the "summer dashboard" I'd planned to use turned out to carry ten results per beach
but only one date. Dumping word positions first showed the real table, with site codes, coordinates and weekly columns, hiding in
the second PDF. Parsing by position and refusing to write when fewer than 90% of rows look right was far more reliable than
trying to be clever with text. [Add: how you felt when the first parse came back at 100%, or what you'd probe first next time.]

**An idempotent write needs to know when nothing changed.** Upserting on location, source and hour stops duplicates, but adding
`WHERE old IS DISTINCT FROM new` is what makes the run audit honest: a re-run reports zero rows written instead of rewriting
the same values and claiming success.

**"I can't tell" is not the same as "it's fine".** My first alert logic resolved an incident whenever it didn't see a breach, which
means a stalled feed would quietly close every alert. Giving the evaluator a third answer (inconclusive: leave things as they are)
fixed that, and it also made gaps, short histories and missing baselines easy to reason about.

**Tests found real bugs, and so did looking at the output.** Tests caught a retry setting of zero crashing the Polly pipeline at
startup, and a boundary check that let a three-hour rule fire on one hour of data. But the query checker was happy with Grafana
dashboards that were quietly wrong: the datasource was never provisioned because the file sat in the wrong folder, and Grafana's
unit `m` means minutes, so wave heights read "2.2 min". I only saw either by actually rendering the thing. [Add: your own
version of this, if you have one.]

**TimescaleDB has sharp edges worth knowing.** Continuous aggregates can't be created inside a transaction, so the migrator runs
that script on its own. Real-time aggregation matters if you want today's chart to show today's data, and a refresh policy's window
has to stay inside the retention period or old buckets get rewritten.

**Read the terms, and decide what's public.** The City's data is copyrighted and its terms don't allow redistribution without
written permission. Fetching a file a day for personal use is one thing, serving the numbers from a public server is another, so the
static export leaves the City's results out unless you switch them on on purpose. [Add: whether you've asked the City, and what they said.]

**You don't always need the standard tool.** There's no message queue because the volume doesn't justify one, and no metrics
server because a small exporter into the Postgres I already run gives Grafana what it needs. Each came with a cost I wrote down
(at-least-once delivery, estimated percentiles) rather than pretending it was free.

**A framework wasn't necessary.** Plain modules, one vendored library, and an SVG chart I wrote myself kept the page small, let me
set a strict Content-Security-Policy, and meant the interesting logic (chart geometry, staleness rules) became pure functions that
are easy to test.

[Add: one or two things you'd do differently, and what you'd build next, for example more beaches, the City's other data, or
proper user accounts.]
