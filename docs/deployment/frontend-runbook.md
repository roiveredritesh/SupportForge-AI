# Frontend Deployment Runbook (IIS static hosting)

## Deploy steps
1. One-time only, before the first deploy: install the IIS URL Rewrite module, then in IIS Manager create a site "SupportForge UI" bound to port 80, physical path `C:\inetpub\supportforge-ui\`.
2. From the dev machine (or on the EC2 host itself): `Scripts/Deploy.ps1 -TargetRoot C:\inetpub -SkipBackend`. This runs `npm run build`, writes the SPA-routing `web.config` (React Router client-side routes falling back to `index.html`) into `dist/`, and copies the result into `C:\inetpub\supportforge-ui\`, preserving the prior deployment at `C:\inetpub\supportforge-ui-previous\` for rollback.
3. Verify `http://<ec2-host>/` loads the Dashboard and `http://<ec2-host>/query` works on direct navigation (not just client-side nav) — this proves the rewrite rule is correct.

## Rollback
`Scripts/Deploy.ps1 -TargetRoot C:\inetpub -Rollback` swaps `supportforge-ui-previous\` (and `supportforge-api-previous\`, if present) back into place.
