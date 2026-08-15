import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type AppRole = 'L1' | 'L2' | 'L3' | 'Admin';

// U9: the backend's JWT (JwtTokenFactory) carries the user's role as a standard ClaimTypes.Role
// claim, keyed by its full URI in the token payload -- there's no separate "/me" endpoint, so this
// decodes the token client-side instead of adding one. No new dependency: a JWT payload is just the
// base64url middle segment.
// Must match System.Security.Claims.ClaimTypes.Role exactly (2008/06, not 2005/05 -- the two URIs
// look alike but are different claim schemas; using the wrong one means this always resolves to
// null and every role-gated UI element silently never renders).
const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

export function decodeRole(accessToken: string): AppRole | null {
  try {
    const payload = accessToken.split('.')[1];
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
    const claims = JSON.parse(json) as Record<string, unknown>;
    const role = claims[ROLE_CLAIM] ?? claims.role;
    return typeof role === 'string' ? (role as AppRole) : null;
  } catch {
    return null;
  }
}

interface AuthState {
  accessToken: string | null;
  expiresAt: string | null;
  role: AppRole | null;
  setToken: (accessToken: string, expiresAt: string) => void;
  logout: () => void;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      accessToken: null,
      expiresAt: null,
      role: null,
      setToken: (accessToken, expiresAt) => set({ accessToken, expiresAt, role: decodeRole(accessToken) }),
      logout: () => set({ accessToken: null, expiresAt: null, role: null }),
    }),
    { name: 'supportforge-auth-store' },
  ),
);
