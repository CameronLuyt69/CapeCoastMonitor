<#
 Writes a fresh static snapshot into site-data/ using the running stack's database.
 It does not commit or push anything, so you can look at what changed first:

   git diff --stat site-data
   git add site-data; git commit -m "Update snapshot"; git push

 Add -WithWaterQuality only if you have the City of Cape Town's written permission to republish
 its results (see the terms of use). Without it the snapshot contains no City data at all.
#>
param([switch]$WithWaterQuality)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
New-Item -ItemType Directory -Force site-data | Out-Null

$extra = @()
if ($WithWaterQuality) { $extra += '--with-water-quality' }

docker compose run --rm -v "${PWD}/site-data:/export" worker --export-static /export @extra
if ($LASTEXITCODE -ne 0) { throw "export failed" }

Write-Host "Snapshot written to site-data/. Review it, then commit and push to publish with the pages workflow."
