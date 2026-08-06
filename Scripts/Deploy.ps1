<#
C8 (gap-closing-solutions.md Phase C, item 8): scripts the deploy steps from
docs/deployment/backend-runbook.md and frontend-runbook.md -- dotnet publish / npm build, then
copies into place at -TargetRoot. Does NOT set up remote access: if -TargetRoot points at the EC2
host (a mapped drive or UNC share), that network path must already be reachable from wherever this
script runs -- WinRM/file-share configuration is a one-time infra step outside this script's scope,
same as the one-time IIS app-pool/site creation documented in the runbooks.

Keeps the previous deployment for rollback: before overwriting api/ui in place, the existing folder
is renamed to "<name>-previous". Use -Rollback to swap it back without rebuilding anything.

Usage:
  Scripts/Deploy.ps1 -TargetRoot 'C:\inetpub'              # deploys both api and ui
  Scripts/Deploy.ps1 -TargetRoot 'C:\inetpub' -SkipFrontend # backend only
  Scripts/Deploy.ps1 -TargetRoot 'C:\inetpub' -Rollback     # restores the previous deployment
#>
param(
    [string]$TargetRoot = (Join-Path $PSScriptRoot '..\publish'),
    [switch]$SkipFrontend,
    [switch]$SkipBackend,
    [switch]$Rollback
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Join-Path $PSScriptRoot '..'
$apiTarget = Join-Path $TargetRoot 'supportforge-api'
$uiTarget = Join-Path $TargetRoot 'supportforge-ui'

# frontend-runbook.md step 4 -- the SPA-routing rewrite rule, generated here instead of being a
# manually-recreated file on the target so it can't drift from what the runbook documents.
$spaWebConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="React Routes" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
          </conditions>
          <action type="Rewrite" url="/index.html" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
'@

function Backup-Then-Copy {
    param([string]$SourceDir, [string]$TargetDir)
    $previousDir = "$TargetDir-previous"
    if (Test-Path $TargetDir) {
        if (Test-Path $previousDir) { Remove-Item $previousDir -Recurse -Force }
        Rename-Item $TargetDir $previousDir
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $TargetDir) | Out-Null
    Copy-Item $SourceDir $TargetDir -Recurse
}

function Restore-Previous {
    param([string]$TargetDir)
    $previousDir = "$TargetDir-previous"
    if (-not (Test-Path $previousDir)) {
        Write-Warning "No previous deployment found at $previousDir -- skipping."
        return
    }
    if (Test-Path $TargetDir) { Remove-Item $TargetDir -Recurse -Force }
    Rename-Item $previousDir $TargetDir
    Write-Host "Rolled back $TargetDir"
}

if ($Rollback) {
    Restore-Previous -TargetDir $apiTarget
    Restore-Previous -TargetDir $uiTarget
    return
}

if (-not $SkipBackend) {
    Write-Host "Publishing backend..."
    $publishDir = Join-Path $repoRoot 'publish\api-build'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $repoRoot 'backend\SupportForge.Api') -c Release -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    Backup-Then-Copy -SourceDir $publishDir -TargetDir $apiTarget
    Write-Host "Backend deployed to $apiTarget (previous kept at $apiTarget-previous)"
}

if (-not $SkipFrontend) {
    Write-Host "Building frontend..."
    Push-Location (Join-Path $repoRoot 'frontend')
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
    }
    finally { Pop-Location }

    $distDir = Join-Path $repoRoot 'frontend\dist'
    Set-Content -Path (Join-Path $distDir 'web.config') -Value $spaWebConfig
    Backup-Then-Copy -SourceDir $distDir -TargetDir $uiTarget
    Write-Host "Frontend deployed to $uiTarget (previous kept at $uiTarget-previous)"
}

Write-Host "Done. One-time IIS setup (app pool, site bindings, URL Rewrite module) is still manual -- see docs/deployment/*-runbook.md."
