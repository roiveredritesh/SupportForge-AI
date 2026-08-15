import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type AppRole = 'L1' | 'L2' | 'L3' | 'Admin';

// U9: the backend's JWT (JwtTokenFactory) carries the user's role as a standard ClaimTypes.Role
// claim, keyed by its full URI in the token payload -- there's no separate "/me" endpoint, so this
// decodes the token client-side instead of adding one. No new dependency: a JWT payload is just the
// base64url middle segment.
const ROLE_CLAIM = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/role';

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
