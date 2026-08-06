<#
Stops the Chroma and Neo4j processes started by Start-LocalDbs.ps1.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pidFile = Join-Path $PSScriptRoot '.local-dbs.pids'
if (-not (Test-Path $pidFile)) {
    Write-Host "No .local-dbs.pids file found; nothing to stop."
    exit 0
}

Get-Content $pidFile | ForEach-Object {
    $procId = $_.Trim()
    if ($procId -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
        taskkill /PID $procId /T /F | Out-Null
        Write-Host "Stopped pid $procId"
    }
}

# neo4j.bat's java child detaches from its process tree, so /T above can miss it.
# Find it by command line instead.
$neo4jHome = Join-Path $PSScriptRoot 'neo4j\neo4j-community-5.26.0'
Get-CimInstance Win32_Process -Filter "Name = 'java.exe'" |
    Where-Object { $_.CommandLine -like "*$neo4jHome*" } |
    ForEach-Object {
        taskkill /PID $_.ProcessId /F | Out-Null
        Write-Host "Stopped orphaned neo4j java pid $($_.ProcessId)"
    }

Remove-Item $pidFile -Force
