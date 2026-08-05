<#
Starts the local Chroma and Neo4j containers defined in docker-compose.yml.
  Chroma: http://localhost:8000
  Neo4j:  bolt://localhost:7687  (browser UI: http://localhost:7474)
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Push-Location (Join-Path $PSScriptRoot '..')
try {
    docker compose up -d chroma neo4j
    docker compose ps chroma neo4j
}
finally {
    Pop-Location
}
