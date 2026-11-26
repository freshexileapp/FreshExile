# Rebuilds src/FreshExile.Core/Data/Mercenaries.tsv from RePoE (https://github.com/repoe-fork/repoe-fork.github.io):
# the mercenaries' builds, their skills and the supports each skill can carry, for reading the in-game mercenary window
# (Core/MercRecognizer.cs). Run once a league, next to update-mod-tiers.ps1: pwsh tools/update-merc-data.ps1
#
# Format (tab separated):
#   B  <build as the warrant names it>  <skill> | <skill> ...        every skill the build can roll
#   K  <skill>  <support id> | <support id> ...                       the supports that skill can carry
#   S  <support id>  <name>  <tier>  <icon>                           icon: the path on web.poecdn.com/image/Art/…png

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src/FreshExile.Core/Data/Mercenaries.tsv'
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'freshexile-repoe'
New-Item -ItemType Directory -Force $tmp | Out-Null

function Get-Table([string]$name) {
    $f = Join-Path $tmp "$name.json"
    if (-not (Test-Path $f) -or (Get-Item $f).LastWriteTime -lt (Get-Date).AddDays(-1)) {
        Invoke-WebRequest "https://raw.githubusercontent.com/repoe-fork/repoe-fork.github.io/HEAD/data/$name.json" -OutFile $f -TimeoutSec 300
    }
    Get-Content $f -Raw | ConvertFrom-Json -AsHashtable
}

$builds = Get-Table 'mercenary_builds'
$skills = Get-Table 'mercenary_skills'
$supports = Get-Table 'mercenary_supports'

$rows = [Collections.Generic.List[string]]::new()
foreach ($id in $builds.Keys | Sort-Object) {
    $b = $builds[$id]
    $pool = @($b.primary_skills) + @($b.secondary_skills.pool) + @($b.utility_skills.pool) |
        Where-Object { $_ -and $skills.ContainsKey($_) } | ForEach-Object { $skills[$_].name } | Select-Object -Unique
    $rows.Add("B`t$($b.name)`t$($pool -join ' | ')")
}
$seen = @{}
foreach ($id in $skills.Keys | Sort-Object) {
    $s = $skills[$id]
    if (-not $s.name -or $seen.ContainsKey($s.name) -or @($s.possible_supports).Count -eq 0) { continue }
    $seen[$s.name] = $true
    $rows.Add("K`t$($s.name)`t$((@($s.possible_supports) | Where-Object { $supports.ContainsKey($_) }) -join ' | ')")
}
foreach ($id in $supports.Keys | Sort-Object) {
    $p = $supports[$id]
    $icon = $p.icon -replace '^Art/', '' -replace '\.dds$', ''
    $rows.Add("S`t$id`t$($p.name)`t$($p.tier)`t$icon")
}
Set-Content -Path $out -Value $rows -Encoding utf8NoBOM
Write-Host "$($builds.Count) builds, $($seen.Count) skills, $($supports.Count) supports -> $out"
