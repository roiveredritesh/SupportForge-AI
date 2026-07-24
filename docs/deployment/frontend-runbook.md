# Frontend Deployment Runbook (IIS static hosting)

## Deploy steps
1. `cd frontend && npm run build` — produces `dist/`.
2. Copy `dist/*` to `C:\inetpub\supportforge-ui\` on the EC2 instance.
3. In IIS Manager, create a site "SupportForge UI" bound to port 80, physical path `C:\inetpub\supportforge-ui\`.
4. Add a `web.config` for SPA routing (React Router client-side routes must fall back to `index.html`):

```xml
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
```

(Requires the IIS URL Rewrite module.)

5. Verify `http://<ec2-host>/` loads the Dashboard and `http://<ec2-host>/query` works on direct navigation (not just client-side nav) — this proves the rewrite rule is correct.
