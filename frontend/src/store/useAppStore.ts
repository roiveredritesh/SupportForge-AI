import { create } from 'zustand';
import { persist } from 'zustand/middleware';

type Theme = 'light' | 'dark';

interface AppState {
  selectedProjectId: string | null;
  setSelectedProjectId: (id: string | null) => void;
  theme: Theme;
  toggleTheme: () => void;
}

export const useAppStore = create<AppState>()(
  persist(
    (set) => ({
      selectedProjectId: null,
      setSelectedProjectId: (id) => set({ selectedProjectId: id }),
      theme: 'light',
      toggleTheme: () => set((s) => ({ theme: s.theme === 'light' ? 'dark' : 'light' })),
    }),
    { name: 'supportforge-app-store', partialize: (s) => ({ selectedProjectId: s.selectedProjectId, theme: s.theme }) },
  ),
);
