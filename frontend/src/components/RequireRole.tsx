import type { ReactNode } from 'react';
import { useAuthStore, type AppRole } from '../store/useAuthStore';

interface Props {
  role: AppRole;
  children: ReactNode;
}

// U8: client-side mirror of the server-side gate ([Authorize(Roles="Admin")] on
// OrgsController's employee endpoints) -- purely a UI convenience (hide a section the API would
// 403 anyway), the server remains the actual enforcement point. Unlike RequireAuth, this isn't a
// route guard (no redirect) -- it just renders nothing when the role doesn't match, so it can wrap
// a section within a page (e.g. AdminPage's Employees section).
export function RequireRole({ role, children }: Props) {
  const currentRole = useAuthStore((s) => s.role);
  if (currentRole !== role) return null;
  return <>{children}</>;
}
