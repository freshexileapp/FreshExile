# Rebuilds src/FreshExile.Core/Data/InfluenceMods.tsv from RePoE (https://repoe-fork.github.io): which mod lines belong to
# an influence, so the gear card can paint them in its colour - the eldritch implicits of the Searing Exarch and the Eater
# of Worlds, and the explicits only the Shaper, the Elder and the four conquerors give.
# Run once a league, next to update-mod-tiers.ps1: pwsh tools/update-influence-mods.ps1
#
# Format (tab separated): <kind i|e>  <influence>,<influence>...  <line with every number as #>
# An explicit line a plain (uninfluenced) mod can also give is left out: nothing tells the two apart.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src/FreshExile.Core/Data/InfluenceMods.tsv'
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'freshexile-repoe'
New-Item -ItemType Directory -Force $tmp | Out-Null

$f = Join-Path $tmp 'mods.min.json'
if (-not (Test-Path $f) -or (Get-Item $f).LastWriteTime -lt (Get-Date).AddDays(-1)) {
    Invoke-WebRequest 'https://repoe-fork.github.io/mods.min.json' -OutFile $f -TimeoutSec 300
}
$mods = Get-Content $f -Raw | ConvertFrom-Json -AsHashtable

# the same rule as Core/InfluenceMods.cs: "(3-5)%" and "4%" both become "#%"
function Shape([string]$line) {
    # RePoE keeps the game's link markup: "[DamageFaster|Faster]" shows as "Faster", "[Poison]" as "Poison"
    $line = $line -replace '\[(?:[^\]|]*\|)?([^\]]*)\]', '$1'
    ($line -replace '\(-?[\d.]+--?[\d.]+\)', '#' -replace '\d+(\.\d+)?', '#').Trim()
}

$conqueror = @{ shaper = 'Shaper'; elder = 'Elder'; crusader = 'Crusader'; basilisk = 'Hunter'; eyrie = 'Redeemer'; adjudicator = 'Warlord' }
$implicit = @{}   # shape -> set of masters
$explicit = @{}
$plain = @{}      # shapes a plain item mod gives

function Add($map, [string]$text, [string]$who) {
    foreach ($l in $text -split "`n") {
        $s = Shape $l
        if ($s.Length -eq 0) { continue }
        if (-not $map.ContainsKey($s)) { $map[$s] = [System.Collections.Generic.SortedSet[string]]::new() }
        if ($who) { [void]$map[$s].Add($who) }
    }
}

foreach ($m in $mods.Values) {
    if (-not $m.text) { continue }
    switch ($m.generation_type) {
        'searing_exarch_implicit' { Add $implicit $m.text 'Exarch' }
        'eater_of_worlds_implicit' { Add $implicit $m.text 'Eater' }
        { $_ -in 'prefix', 'suffix' } {
            if ($m.domain -ne 'item') { continue }
            $live = @($m.spawn_weights | Where-Object { $_.weight -gt 0 })
            if (-not $live) { continue }
            $who = @($live | ForEach-Object { if ($_.tag -match '_(shaper|elder|crusader|basilisk|eyrie|adjudicator)$') { $conqueror[$Matches[1]] } })
            if ($who.Count -eq 0) { Add $plain $m.text $null }
            else { foreach ($w in $who | Sort-Object -Unique) { Add $explicit $m.text $w } }
        }
    }
}

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($s in $implicit.Keys | Sort-Object) { $lines.Add("i`t$($implicit[$s] -join ',')`t$s") }
foreach ($s in $explicit.Keys | Sort-Object) { if (-not $plain.ContainsKey($s)) { $lines.Add("e`t$($explicit[$s] -join ',')`t$s") } }
Set-Content $out $lines -Encoding utf8NoBOM
"wrote $out : $($lines.Count) lines, $((Get-Item $out).Length) bytes"
