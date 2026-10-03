$ErrorActionPreference='Stop'
# Preserve all seven original identity cases through the actual batched path.
& (Join-Path $PSScriptRoot 'Test-DialoguePeopleSlices.ps1') -LegacyLookupOnly
