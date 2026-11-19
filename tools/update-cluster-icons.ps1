# Rebuilds src/FreshExile.Core/Data/ClusterIcons.tsv from Path of Building's Data/ClusterJewels.lua: the icon the game
# gives the small passives of each cluster enchant. GGG's tree export knows the icons of cluster notables, not these;
# the tree overlay draws them into the rings of a cluster that is not socketed yet (the game shows nothing there).
# Run once a league: pwsh tools/update-cluster-icons.ps1
#
# Format (tab separated):  <small passive stat, numbers as #>  <icon path as in the export's sprite sheets>

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src/FreshExile.Core/Data/ClusterIcons.tsv'
$lua = (Invoke-WebRequest 'https://raw.githubusercontent.com/PathOfBuildingCommunity/PathOfBuilding/dev/src/Data/ClusterJewels.lua' -TimeoutSec 120).Content

$rows = [ordered]@{}
$rx = '\["affliction_[a-z_]+"\] = \{\s*name = "[^"]*",\s*icon = "([^"]+)".*?stats = \{ ([^}]*)\}'
foreach ($m in [regex]::Matches($lua, $rx, 'Singleline')) {
    $icon = $m.Groups[1].Value
    foreach ($s in [regex]::Matches($m.Groups[2].Value, '"([^"]+)"')) {
        $key = $s.Groups[1].Value -replace '[\d.]+', '#'
        if (-not $rows.Contains($key)) { $rows[$key] = $icon }
    }
}
$lines = foreach ($k in $rows.Keys) { "$k`t$($rows[$k])" }
[IO.File]::WriteAllLines($out, [string[]]$lines, [Text.UTF8Encoding]::new($false))
"$($rows.Count) small passive icons written to $out"
