# Rebuilds src/FreshExile.Core/Data/MapMods.tsv from RePoE (https://repoe-fork.github.io): the mods a map can roll, for
# the map regex window (Core/MapRegex.cs). The same mod at every tier is one row, its numbers spread over all tiers.
# Run once a league, next to update-mod-tiers.ps1: pwsh tools/update-map-mods.ps1
#
# Format (tab separated): <p|s|ps prefix/suffix>  <r|u|ru: regular maps / Tier 17 only / both>  <line> | <line> ...
# The lines a mod adds to the map's totals ("13% increased Atlas Drops", "47% more Currency found in Area") are left out:
# the game shows them in the property block, not under the mod, and the regex never sees them there.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src/FreshExile.Core/Data/MapMods.tsv'
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'freshexile-repoe'
New-Item -ItemType Directory -Force $tmp | Out-Null

$f = Join-Path $tmp 'mods.min.json'
if (-not (Test-Path $f) -or (Get-Item $f).LastWriteTime -lt (Get-Date).AddDays(-1)) {
    Invoke-WebRequest 'https://repoe-fork.github.io/mods.min.json' -OutFile $f -TimeoutSec 300
}
$mods = Get-Content $f -Raw | ConvertFrom-Json -AsHashtable

$regular = 'default', 'low_tier_map', 'mid_tier_map', 'top_tier_map', 'has_uber_map_prefix ', 'has_uber_map_suffix'
$bonus = 'Atlas Drops|Rarity of Items found|Pack size|more Maps found|more Currency found|Scarabs found|Divination Cards found|Quantity of Items found|Threads found'
$numRx = '\((-?[\d.]+)-(-?[\d.]+)\)|-?\d+(?:\.\d+)?'

$families = [ordered]@{}   # shape -> @{ kinds; tiers; lines = list of per-line number lists }
foreach ($id in $mods.Keys | Sort-Object) {
    $m = $mods[$id]
    if ($m.domain -ne 'area' -or $m.generation_type -notin 'prefix', 'suffix') { continue }
    $tags = @($m.spawn_weights | Where-Object { $_.weight -gt 0 } | ForEach-Object { $_.tag })
    $isRegular = @($tags | Where-Object { $_ -in $regular }).Count -gt 0
    $isUber = $tags -contains 'uber_tier_map'
    if (-not $isRegular -and -not $isUber) { continue }

    # RePoE keeps the game's link markup: "[AtlasDropScaling|Atlas Drops]" shows as "Atlas Drops"
    $text = $m.text -replace '\[(?:[^\]|]*\|)?([^\]]*)\]', '$1'
    $lines = @($text -split "`n" | Where-Object { $_ -and $_ -notmatch $bonus } | Sort-Object { $_ -replace $numRx, '#' })
    if ($lines.Count -eq 0) { continue }
    $shape = ($lines | ForEach-Object { $_ -replace $numRx, '#' }) -join ' | '

    if (-not $families.Contains($shape)) {
        $families[$shape] = @{ Kinds = [Collections.Generic.HashSet[string]]::new(); Regular = $false; Uber = $false; Lo = @{}; Hi = @{} }
    }
    $fam = $families[$shape]
    [void]$fam.Kinds.Add($m.generation_type.Substring(0, 1))
    if ($isRegular) { $fam.Regular = $true }
    if ($isUber) { $fam.Uber = $true }
    # every number's lowest and highest over the tiers, by its place in the lines
    $i = 0
    foreach ($n in [regex]::Matches(($lines -join ' | '), $numRx)) {
        $lo = [double]($n.Groups[1].Success ? $n.Groups[1].Value : $n.Value)
        $hi = [double]($n.Groups[2].Success ? $n.Groups[2].Value : $n.Value)
        if (-not $fam.Lo.ContainsKey($i) -or $lo -lt $fam.Lo[$i]) { $fam.Lo[$i] = $lo }
        if (-not $fam.Hi.ContainsKey($i) -or $hi -gt $fam.Hi[$i]) { $fam.Hi[$i] = $hi }
        $i++
    }
}

$rows = foreach ($shape in $families.Keys) {
    $fam = $families[$shape]
    $i = 0
    $shown = [regex]::Replace($shape, '#', {
        param($x)
        $lo = $fam.Lo[$script:i]; $hi = $fam.Hi[$script:i]; $script:i++
        if ($lo -eq $hi) { "$lo" } elseif ($lo -lt $hi) { "($lo-$hi)" } else { "($hi-$lo)" }
    })
    $kind = (@('p', 's') | Where-Object { $fam.Kinds.Contains($_) }) -join ''
    $tier = ($fam.Regular ? 'r' : '') + ($fam.Uber ? 'u' : '')
    "$kind`t$tier`t$shown"
}
# by the words, not the numbers: "(14-40)% increased Monster Damage" sits among the "Monster…" ones
$rows = @($rows | Sort-Object { (($_ -split "`t")[2] -replace '[-+\d().%]', '').Trim() })
Set-Content -Path $out -Value $rows -Encoding utf8NoBOM
Write-Host "$($rows.Count) map mods -> $out"
