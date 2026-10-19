#!/usr/bin/env bash
# Same as publish-snapshot.ps1. Pass --with-water-quality only with the City's written permission.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p site-data
MSYS_NO_PATHCONV=1 docker compose run --rm -v "$PWD/site-data:/export" worker --export-static /export "$@"
echo "Snapshot written to site-data/. Review it, then commit and push to publish with the pages workflow."
