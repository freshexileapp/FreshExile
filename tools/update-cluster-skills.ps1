# Rebuilds src/FreshExile.Core/Data/ClusterSkills.tsv from RePoE (https://repoe-fork.github.io): which notables can roll
# on a cluster jewel of each "Cluster Jewel Skill" (the enchant: affliction_chaos_damage = "12% increased Chaos Damage").
# The smart cluster search uses it: "my two notables + any third that does not stand in their way" lists the thirds.
# Run once a league, after tools/update-mod-tiers.ps1: pwsh tools/update-cluster-skills.ps1
#
# Format (tab separated):  <skill tag>  <notable>|<notable>|...   notable names as in Data/ClusterNotables.tsv

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src/FreshExile.Core/Data/ClusterSkills.tsv'
$names = Join-Path $root 'src/FreshExile.Core/Data/ClusterNotables.tsv'
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'freshexile-repoe'
New-Item -ItemType Directory -Force $tmp | Out-Null

$file = Join-Path $tmp 'mods.min.json'
if (-not (Test-Path $file) -or (Get-Item $file).LastWriteTime -lt (Get-Date).AddDays(-1)) {
    Invoke-WebRequest 'https://repoe-fork.github.io/mods.min.json' -OutFile $file -TimeoutSec 300
}
$mods = Get-Content $file -Raw | ConvertFrom-Json -AsHashtable

# the stat says "prodigious_defense", the game "Prodigious Defence": compared by letters, American spelling
function Key([string]$s) {
    ($s.ToLowerInvariant() -replace 'defence', 'defense' -replace 'armour', 'armor' -replace 'colour', 'color') -replace '[^a-z]', ''
}
$byKey = @{}
foreach ($line in Get-Content $names) {
    $n = ($line -split "`t")[0]
    if ($n) { $byKey[(Key $n)] = $n }
}

$skills = [ordered]@{}
$unknown = 0
foreach ($m in $mods.Values) {
    if ($m.domain -ne 'affliction_jewel') { continue }
    $stat = $m.stats | Where-Object { $_.id -like 'local_affliction_notable_*' } | Select-Object -First 1
    if (-not $stat) { continue }
    $name = $byKey[(Key ($stat.id -replace '^local_affliction_notable_', ''))]
    # a notable PoB has no place for (legacy ones): it never sits on a jewel the trade sells
    if (-not $name) { $unknown++; continue }
    foreach ($w in $m.spawn_weights) {
        if ($w.weight -le 0 -or -not $w.tag.StartsWith('affliction_')) { continue }
        if (-not $skills.Contains($w.tag)) { $skills[$w.tag] = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal) }
        [void]$skills[$w.tag].Add($name)
    }
}

$lines = foreach ($k in $skills.Keys | Sort-Object) { "$k`t$($skills[$k] -join '|')" }
[IO.File]::WriteAllLines($out, [string[]]$lines, [Text.UTF8Encoding]::new($false))
"$($skills.Count) skills written to $out ($unknown notables without a place left out)"
