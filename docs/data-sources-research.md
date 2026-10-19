# Data sources I looked into and did not build

For tides, real observations and E. coli I looked for a legitimate source before writing any code, and in each case could not
verify one that I could defend using. This page records what I found, so the decision can be revisited. Everything here comes from
web searches done on 19 October 2026 and **has not been confirmed with the owners of the data**. Where something is a lead and
not a finding, it says so.

The rule I applied: an official source whose terms allow the use, or stop. Better an honest gap than a number I can't stand behind.

## Official tide times and heights

**Not built.** The sea level in the app (`sea_level_height_msl`) stays labelled as an approximate model proxy, not a tide.

What I found:

- The South African Navy Hydrographic Office (SANHO) runs the national tide gauge network and is responsible for processing,
  archiving and distributing sea level data. Its products include tidal information.
- The PSMSL page for Simon's Bay says SANHO owns the copyright on the Simon's Bay sea level data and that SANHO must be contacted for
  permission to use it.
- The University of Hawaii Sea Level Center (UHSLC) lists Simon's Town as station 221. The fast-delivery daily listing showed a
  last data request in December 2018, so I could not confirm a current feed, and the data is SANHO's.
- I found no free tide prediction API and no published SANHO licence for reuse.

A possible route, **not verified**: open harmonic constants such as the TICON-4 dataset (tidal constants estimated from tide gauge
records), from which a tide could be predicted locally. Before using it someone would have to confirm that Simon's Town or
Cape Town is in the dataset (I could not check), what licence it carries (sources I found disagreed on the attribution), and
how well a prediction matches the official tables. That last check needs a handful of official tide table values, which I don't have.

To proceed: ask SANHO for permission or its data terms, or supply official table values and confirm the TICON licence.

## Real measurements instead of model values

**Not built.** Everything marine in the app is modelled and says so.

- CSIR's WAVENET site runs a Datawell wave buoy off Cape Point. Its Cape Point page said the buoy was offline when I looked, and it
  carries a copyright notice and a note that the figures are estimates for display only. I found no API, no licence and no
  redistribution terms.
- I found no open wave or weather station feed from the South African Weather Service or SAEON in the results.
- Tide gauge data is SANHO's (see above).

To proceed: contact CSIR (the Cape Point page names a contact) about reuse terms and ask whether the buoy is expected to come back.

## E. coli

**Not built.** The City's report only gives enterococci.

- The City publishes enterococci and argues it is the preferred indicator. Coverage notes that E. coli was left out of the published
  results, which drew criticism.
- The only E. coli figures I found were from a citizen-led study (Project Blue) whose results the City disputes, so they are not an
  official source.
- A South African guideline of 500 organisms per 100 ml for E. coli in recreational coastal water was mentioned, but without a
  data source to compare against it is no use.

To proceed: look for official E. coli results (the City's open data portal, Blue Flag or SABS reports) and check their terms.
If the City's open data portal carries a coastal dataset, its licence may differ from the website's terms. I could not confirm that.

## Crowd data

**Not built, and not planned.** There is no legitimate, privacy-respecting source of beach crowd numbers, and collecting any would
conflict with this project's no-tracking rule.
