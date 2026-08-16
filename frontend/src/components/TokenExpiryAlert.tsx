import { useOrgs } from '../hooks/useOrgs';
import { useMcpConnections } from '../hooks/useMcpConnections';
import { RequireRole } from './RequireRole';

// U7/KTD9: fixed 7-day "expiring soon" threshold -- matches the plan's AE5, not configurable.
const EXPIRING_SOON_DAYS = 7;

function daysUntil(expiresAt: string): number {
  const ms = new Date(expiresAt).getTime() - Date.now();
  return Math.ceil(ms / (1000 * 60 * 60 * 24));
}

// U7: Admin-only banner on the Dashboard (KTD9) warning about GitHub connections nearing/past
// their captured expiry. No dedicated "/mcp-connections/expiring" endpoint -- the existing
// GetConnections list already carries ExpiresAt, so the 7-day threshold check happens here,
// client-side, rather than duplicating it as a second backend endpoint.
export function TokenExpiryAlert() {
  const { data: orgs } = useOrgs();
  const orgId = orgs?.[0]?.id;
  const { data: connections } = useMcpConnections(orgId);

  const withExpiry = (connections ?? []).filter((c): c is typeof c & { expiresAt: string } => !!c.expiresAt);
  const expired = withExpiry.filter((c) => daysUntil(c.expiresAt) < 0);
  const expiringSoon = withExpiry.filter((c) => {
    const days = daysUntil(c.expiresAt);
    return days >= 0 && days <= EXPIRING_SOON_DAYS;
  });

  if (expired.length === 0 && expiringSoon.length === 0) return null;

  return (
    <RequireRole role="Admin">
      <div className="space-y-2">
        {expired.map((c) => (
          <div
            key={c.serverType}
            className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950 dark:text-red-300"
          >
            The <span className="font-medium">{c.serverType}</span> connection has expired. Reconnect with a
            fresh token to restore access.
          </div>
        ))}
        {expiringSoon.map((c) => (
          <div
            key={c.serverType}
            className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900 dark:bg-amber-950 dark:text-amber-300"
          >
            The <span className="font-medium">{c.serverType}</span> connection will expire in{' '}
            {daysUntil(c.expiresAt)} day{daysUntil(c.expiresAt) === 1 ? '' : 's'}. Reconnect soon to avoid an
            interruption.
          </div>
        ))}
      </div>
    </RequireRole>
  );
}
