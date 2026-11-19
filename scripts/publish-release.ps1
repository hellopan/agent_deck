param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Go = 'go',
    [string]$Repo = 'hellopan/agent_deck',
    [string]$Notes = '',
    [switch]$Publish
)

# Stage one desktop release and its update manifest. Nothing is uploaded unless
# -Publish is given: creating a GitHub release is a deliberate, authenticated
# action, never a side effect of building.
#
#   .\scripts\publish-release.ps1 -Version 0.2.4 -Go D:\...\_tools\go\bin\go.exe
#   .\scripts\publish-release.ps1 -Version 0.2.4 -Go ... -Publish
#
# The manifest URL is predictable (releases/download/<tag>/<asset>), so it can
# be written before the release exists. GitHub redirects the download to its
# CDN; the tray follows redirects and verifies SHA-256 before replacing itself.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'bin'
$stage = Join-Path $bin "release-$Version"
New-Item -ItemType Directory -Path $stage -Force | Out-Null

if (-not (Get-Command $Go -ErrorAction SilentlyContinue)) {
    throw "Go not found: pass -Go <path to go.exe>"
}

Push-Location $root
try {
    $backend = Join-Path $bin 'agentdeck-task-lab.exe'
    & $Go build -trimpath -o (Join-Path $bin 'agentdeck-lab.exe') ./cmd/agentdeck-lab
    if ($LASTEXITCODE -ne 0) { throw 'go build failed' }
    Copy-Item -LiteralPath (Join-Path $bin 'agentdeck-lab.exe') -Destination $backend -Force

    dotnet publish (Join-Path $root 'windows/AgentDeck.Tray') -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:AgentDeckBackend="$backend" -p:Version="$Version" -p:DebugType=none `
        -o (Join-Path $bin '.publish-tray')
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
} finally {
    Pop-Location
}

$exe = Join-Path $stage 'AgentDeck-Setup.exe'
Copy-Item -LiteralPath (Join-Path $bin '.publish-tray/AgentDeck.Tray.exe') -Destination $exe -Force

$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToUpperInvariant()
$size = (Get-Item -LiteralPath $exe).Length
$tag = "v$Version"
$asset = "https://github.com/$Repo/releases/download/$tag/AgentDeck-Setup.exe"
$manifest = [ordered]@{
    windows = [ordered]@{
        version = $Version
        notes   = if ($Notes) { $Notes } else { "AgentDeck $Version" }
        full    = $asset
        sha256  = $hash
        size    = $size
    }
}
$json = $manifest | ConvertTo-Json -Depth 4
$manifestPath = Join-Path $stage 'latest.json'
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "staged   : $exe ($size bytes)"
Write-Host "manifest : $manifestPath"
Write-Host "sha256   : $hash"

if ($Publish) {
    gh release create $tag $exe $manifestPath --repo $Repo --title "AgentDeck $Version" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
    Write-Host "published: $tag"
} else {
    Write-Host "not published. To publish:"
    Write-Host "  gh release create $tag `"$exe`" `"$manifestPath`" --repo $Repo --generate-notes"
}
