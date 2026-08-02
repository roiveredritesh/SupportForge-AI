import { create } from 'zustand';
import { persist } from 'zustand/middleware';

interface AuthState {
  accessToken: string | null;
  expiresAt: string | null;
  setToken: (accessToken: string, expiresAt: string) => void;
  logout: () => void;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      accessToken: null,
      expiresAt: null,
      setToken: (accessToken, expiresAt) => set({ accessToken, expiresAt }),
      logout: () => set({ accessToken: null, expiresAt: null }),
    }),
    { name: 'supportforge-auth-store' },
  ),
);
