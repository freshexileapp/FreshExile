# Rebuilds src/FreshExile.Core/Data/ModTiers.tsv from RePoE (https://repoe-fork.github.io): equipment bases with their tags
# and every prefix / suffix that can roll on them, so the overlay can tell how many tiers a mod has on a base.
# Run once a league: pwsh tools/update-mod-tiers.ps1
#
# Format (tab separated):
#   T  <tag0>,<tag1>,...                 tag table, indexed below
#   B  <base name>  <domain>  <tag idx>,...
#   M  <family>  <p|s>  <domain>  <mod name>  <tag idx>:<weight>,...   spawn weights in the game's order (first tag the base has wins)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src/FreshExile.Core/Data/ModTiers.tsv'
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'freshexile-repoe'
New-Item -ItemType Directory -Force $tmp | Out-Null

foreach ($f in 'mods.min.json', 'base_items.min.json') {
    Invoke-WebRequest "https://repoe-fork.github.io/$f" -OutFile (Join-Path $tmp $f) -TimeoutSec 300
}
$mods = Get-Content (Join-Path $tmp 'mods.min.json') -Raw | ConvertFrom-Json -AsHashtable
$bases = Get-Content (Join-Path $tmp 'base_items.min.json') -Raw | ConvertFrom-Json -AsHashtable

$classes = @('Body Armour', 'Helmet', 'Gloves', 'Boots', 'Shield', 'Quiver', 'Amulet', 'Ring', 'Belt',
    'One Hand Sword', 'Thrusting One Hand Sword', 'One Hand Axe', 'One Hand Mace', 'Sceptre', 'Claw', 'Dagger', 'Rune Dagger', 'Wand',
    'Two Hand Sword', 'Two Hand Axe', 'Two Hand Mace', 'Staff', 'Warstaff', 'Bow', 'Jewel', 'AbyssJewel')

$tagIndex = [ordered]@{}
function TagIdx([string]$t) {
    if (-not $tagIndex.Contains($t)) { $tagIndex[$t] = $tagIndex.Count }
    return $tagIndex[$t]
}

$lines = [System.Collections.Generic.List[string]]::new()
$domains = @{}
foreach ($b in $bases.Values | Sort-Object { $_.name }) {
    if ($b.release_state -ne 'released' -or $classes -notcontains $b.item_class) { continue }
    $domains[$b.domain] = $true
    $tags = ($b.tags | ForEach-Object { TagIdx $_ }) -join ','
    $lines.Add("B`t$($b.name)`t$($b.domain)`t$tags")
}

$baseTags = @{}
foreach ($b in $bases.Values) { if ($b.release_state -eq 'released' -and $classes -contains $b.item_class) { foreach ($t in $b.tags) { $baseTags[$t] = $true } } }

foreach ($m in $mods.Values | Sort-Object { $_.type }, { $_.required_level }) {
    if ($m.generation_type -notin 'prefix', 'suffix' -or $m.is_essence_only -or -not $domains.ContainsKey($m.domain)) { continue }
    if (-not $m.name) { continue }
    # only the weights a released base can meet; a mod no base can roll is left out
    $w = @($m.spawn_weights | Where-Object { $baseTags.ContainsKey($_.tag) })
    if (-not ($w | Where-Object { $_.weight -gt 0 })) { continue }
    $ws = ($w | ForEach-Object { "$(TagIdx $_.tag):$($_.weight)" }) -join ','
    $g = if ($m.generation_type -eq 'prefix') { 'p' } else { 's' }
    $lines.Add("M`t$($m.type)`t$g`t$($m.domain)`t$($m.name)`t$ws")
}

$header = "T`t" + ($tagIndex.Keys -join ',')
Set-Content $out (@($header) + $lines) -Encoding utf8NoBOM
"wrote $out : $($lines.Count) lines, $((Get-Item $out).Length) bytes"
