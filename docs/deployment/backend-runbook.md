# Backend Deployment Runbook (EC2 + IIS)

## Prerequisites on the EC2 Windows instance
1. Install IIS with the "Web Server (IIS)" role and ASP.NET Core Hosting Bundle for .NET 9.
2. Install Docker Desktop (or use Pinecone in prod — see `VectorStore:Provider` config) for the local Chroma container if not using Pinecone.
3. Create an Application Pool named `SupportForgeApi` with .NET CLR version "No Managed Code".

## Deploy steps
1. From the dev machine: `dotnet publish backend/SupportForge.Api -c Release -o publish/api`
2. Copy `publish/api/*` to `C:\inetpub\supportforge-api\` on the EC2 instance.
3. In IIS Manager, create a site "SupportForge API" bound to port 5000, physical path `C:\inetpub\supportforge-api\`, using the `SupportForgeApi` app pool.
4. Set environment variables on the App Pool (or `appsettings.Production.json`): `OpenAI__ApiKey`, `VectorStore__Provider=Pinecone`, `VectorStore__Pinecone__ApiKey`, `VectorStore__Pinecone__Environment`.
5. Start the site; verify `http://<ec2-host>:5000/health` returns `{"status":"ok"}`.

## Rollback
Keep the previous `publish/api` folder as `publish/api-previous/`; to roll back, stop the IIS site, swap folder contents, restart the site.
