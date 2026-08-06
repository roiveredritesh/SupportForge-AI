import { beforeEach, describe, expect, it } from 'vitest';
import { useAuthStore } from './useAuthStore';

describe('useAuthStore', () => {
  beforeEach(() => {
    useAuthStore.setState({ accessToken: null, expiresAt: null });
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
