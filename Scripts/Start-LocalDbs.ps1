<#
Starts local Chroma and Neo4j without Docker (Docker Desktop isn't installed on this machine).
  Chroma: http://localhost:8000            (pip package `chromadb`, already installed)
  Neo4j:  bolt://localhost:7687  (browser UI: http://localhost:7474)
          Server lives in Scripts/neo4j/neo4j-community-5.26.0 (downloaded, auth disabled to
          match the old docker-compose NEO4J_AUTH=none).

Both run as hidden background processes. Use Stop-LocalDbs.ps1 to stop them.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Push-Location (Join-Path $PSScriptRoot '..')
try {
    $chromaData = Join-Path (Get-Location) '.chroma-data'
    New-Item -ItemType Directory -Force -Path $chromaData | Out-Null

    $chromaProc = Start-Process chroma -ArgumentList @('run', '--path', "`"$chromaData`"", '--port', '8000') `
        -WorkingDirectory (Get-Location) -WindowStyle Hidden -PassThru
    $neo4jHome = Join-Path $PSScriptRoot 'neo4j\neo4j-community-5.26.0'
    $neo4jProc = Start-Process (Join-Path $neo4jHome 'bin\neo4j.bat') -ArgumentList 'console' `
        -WorkingDirectory $neo4jHome -WindowStyle Hidden -PassThru

    $chromaProc.Id, $neo4jProc.Id | Set-Content (Join-Path $PSScriptRoot '.local-dbs.pids')

    Write-Host "Chroma starting (pid $($chromaProc.Id)) on http://localhost:8000"
    Write-Host "Neo4j starting (pid $($neo4jProc.Id)) on bolt://localhost:7687 / http://localhost:7474"
    Write-Host "Give Neo4j ~15s to finish booting. Run Scripts/Stop-LocalDbs.ps1 to stop both."
}
finally {
    Pop-Location
}
