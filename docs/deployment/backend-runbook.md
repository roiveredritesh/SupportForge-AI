# Backend Deployment Runbook (EC2 + IIS)

## Local dev dependencies (Chroma + Neo4j)
`docker compose up -d` (repo root) starts both: Chroma on `localhost:8000` (KB vector store) and Neo4j on `localhost:7687` bolt / `localhost:7474` browser UI (code knowledge graph). Both match this repo's default `appsettings.json` with zero config — Neo4j runs with `NEO4J_AUTH=none`, matching `Neo4jOptions`' empty-password default. Data persists to `.chroma-data/` and `.neo4j-data/` (both gitignored).

## Prerequisites on the EC2 Windows instance
1. Install IIS with the "Web Server (IIS)" role and ASP.NET Core Hosting Bundle for .NET 9.
2. Install Docker Desktop (or use Pinecone in prod — see `VectorStore:Provider` config) for the local Chroma container if not using Pinecone. Neo4j is required regardless of `VectorStore:Provider`, since code Q&A always uses it — see `Neo4j` config in `configuration-guide.md`.
3. Create an Application Pool named `SupportForgeApi` with .NET CLR version "No Managed Code".

## Deploy steps
1. One-time only, before the first deploy: complete steps 1-3 above (IIS role, hosting bundle, app pool), then in IIS Manager create a site "SupportForge API" bound to port 5000, physical path `C:\inetpub\supportforge-api\`, using the `SupportForgeApi` app pool.
2. From the dev machine (or on the EC2 host itself, if running the script there directly): `Scripts/Deploy.ps1 -TargetRoot C:\inetpub -SkipFrontend`. This publishes the backend and copies it into `C:\inetpub\supportforge-api\`, automatically preserving the prior deployment at `C:\inetpub\supportforge-api-previous\` for rollback. If `-TargetRoot` isn't reachable from the machine you're running the script on (e.g. it needs to land on a remote EC2 host), map/mount that path first — the script doesn't set up remote access itself.
3. Set environment variables on the App Pool (or `appsettings.Production.json`), once: `OpenAI__ApiKey`, `VectorStore__Provider=Pinecone`, `VectorStore__Pinecone__ApiKey`, `VectorStore__Pinecone__Environment`, and `Cors__AllowedOrigins__0=http://<frontend-host>` (the origin the IIS-hosted frontend is served from — see `frontend-runbook.md`; without this the API only allows `http://localhost:5173` and browsers will block requests from the deployed UI).
4. Restart the IIS site (new files won't be picked up otherwise); verify `http://<ec2-host>:5000/health` returns `{"status":"ok"}`.

## Rollback
`Scripts/Deploy.ps1 -TargetRoot C:\inetpub -Rollback` swaps `supportforge-api-previous\` back into place without rebuilding anything — restart the IIS site afterward for it to take effect.
