import { beforeEach, describe, expect, it } from 'vitest';
import { useAuthStore, decodeRole } from './useAuthStore';

// Builds a JWT with the exact shape SupportForge.Api's JwtTokenFactory issues (header/signature
// content doesn't matter for client-side decoding -- only the payload's claim keys do).
function fakeJwt(claims: Record<string, unknown>): string {
  const base64url = (obj: object) =>
    btoa(JSON.stringify(obj)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${base64url({ alg: 'HS256', typ: 'JWT' })}.${base64url(claims)}.signature`;
}

describe('useAuthStore', () => {
  beforeEach(() => {
    useAuthStore.setState({ accessToken: null, expiresAt: null, role: null });
  });

  describe('decodeRole', () => {
    // Regression test: JwtTokenFactory.cs issues the role claim as ClaimTypes.Role, whose URI is
    // http://schemas.microsoft.com/ws/2008/06/identity/claims/role -- decodeRole must key off that
    // exact URI (a prior version used the look-alike but different 2005/05 xmlsoap URI, which
    // silently decoded every real token's role as null).
    it('decodes the role from a real ClaimTypes.Role-shaped token', () => {
      const token = fakeJwt({
        sub: 'user-1',
        unique_name: 'admin1',
        'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': 'Admin',
      });

      expect(decodeRole(token)).toBe('Admin');
    });

    it('returns null when the token carries no role claim', () => {
      const token = fakeJwt({ sub: 'user-1', unique_name: 'admin1' });

      expect(decodeRole(token)).toBeNull();
    });
  });

  it('setToken decodes and stores the role from the access token', () => {
    const token = fakeJwt({
      sub: 'user-1',
      'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': 'L2',
    });

    useAuthStore.getState().setToken(token, '2026-08-07T00:00:00Z');

    expect(useAuthStore.getState().role).toBe('L2');
  });

  it('starts with no token', () => {
    expect(useAuthStore.getState().accessToken).toBeNull();
    expect(useAuthStore.getState().expiresAt).toBeNull();
  });

  it('setToken stores the access token and expiry', () => {
    useAuthStore.getState().setToken('tok123', '2026-08-07T00:00:00Z');

    expect(useAuthStore.getState().accessToken).toBe('tok123');
    expect(useAuthStore.getState().expiresAt).toBe('2026-08-07T00:00:00Z');
  });

  it('logout clears the access token and expiry', () => {
    useAuthStore.getState().setToken('tok123', '2026-08-07T00:00:00Z');

    useAuthStore.getState().logout();

    expect(useAuthStore.getState().accessToken).toBeNull();
    expect(useAuthStore.getState().expiresAt).toBeNull();
  });
});
