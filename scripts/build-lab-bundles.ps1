param([string]$Go = 'go')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'bin'
$stage = Join-Path $output ('lab-build-' + [guid]::NewGuid().ToString('N'))
$previousOS = $env:GOOS
$previousArch = $env:GOARCH
$previousCGO = $env:CGO_ENABLED
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Push-Location $root
try {
    $env:GOARCH = 'amd64'
    $env:CGO_ENABLED = '0'
    foreach ($platform in @('windows', 'linux')) {
        $env:GOOS = $platform
        $directory = Join-Path $stage $platform
        New-Item -ItemType Directory -Path $directory | Out-Null
        $binary = if ($platform -eq 'windows') { 'agentdeck-lab.exe' } else { 'agentdeck-lab' }
        & $Go build -trimpath -o (Join-Path $directory $binary) ./cmd/agentdeck-lab
        if ($LASTEXITCODE -ne 0) { throw "Go build failed for $platform" }
        Copy-Item -LiteralPath 'docs/private-lab-deploy.md' -Destination (Join-Path $directory 'DEPLOY.md')
        $archive = Join-Path $output "agentdeck-lab-$platform-amd64.zip"
        Compress-Archive -Path (Join-Path $directory '*') -DestinationPath $archive -Force
        Get-FileHash -LiteralPath $archive -Algorithm SHA256
    }
} finally {
    Pop-Location
    $env:GOOS = $previousOS
    $env:GOARCH = $previousArch
    $env:CGO_ENABLED = $previousCGO
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    if ((Split-Path -Parent $resolvedStage) -eq (Resolve-Path -LiteralPath $output).Path -and
        (Split-Path -Leaf $resolvedStage) -match '^lab-build-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
