# Publishes the current code to the public repository and builds the release archive.
#
# The working history stays private (it carries the author's own name). The public repository gets a history of its
# own on the local branch "public": every release is one commit there, with the tree of the current HEAD, authored by
# FreshExile with the GitHub no-reply address. Run from a clean working tree:
#
#   pwsh tools/publish.ps1 -Version 0.3.8            # commit on "public" + release/FreshExile.zip
#   pwsh tools/publish.ps1 -Version 0.3.8 -Push      # ... and push "public" to github.com/freshexileapp/FreshExile
#
# Then on GitHub: Releases -> Draft a new release -> tag v<Version> -> attach release/FreshExile.zip -> Publish.

param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$Push
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (git status --porcelain) { throw 'The working tree has uncommitted changes: commit them first.' }

# the version the app shows ("Версия 0.3.8") must be the one released
$csproj = 'src/FreshExile.App/FreshExile.App.csproj'
if ((Get-Content $csproj -Raw) -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    throw "$csproj has another <Version>: set it to $Version and commit first."
}

# one commit on "public": the current tree, the previous public release as its parent
$env:GIT_AUTHOR_NAME = 'FreshExile'
$env:GIT_AUTHOR_EMAIL = '358479879+freshexileapp@users.noreply.github.com'
$env:GIT_COMMITTER_NAME = $env:GIT_AUTHOR_NAME
$env:GIT_COMMITTER_EMAIL = $env:GIT_AUTHOR_EMAIL
$tree = git rev-parse 'HEAD^{tree}'
if ((git rev-parse 'public^{tree}') -eq $tree) {
    Write-Host 'public already has this tree: no new commit'
} else {
    $commit = git commit-tree $tree -p public -m "Fresh Exile $Version"
    git update-ref refs/heads/public $commit
    Write-Host "public: $(git log -1 --format='%h %an <%ae> %s' public)"
}

# the archive: one self-contained, compressed exe (no .NET to install) and the license
$out = Join-Path $root 'release'
$pub = Join-Path ([IO.Path]::GetTempPath()) 'freshexile-publish'
Remove-Item $pub -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/FreshExile.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $pub -nologo -v q
Copy-Item LICENSE (Join-Path $pub 'LICENSE.txt')
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out 'FreshExile.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pub 'FreshExile.exe'), (Join-Path $pub 'LICENSE.txt') -DestinationPath $zip
Write-Host "archive: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

if ($Push) { git push public public:main }
