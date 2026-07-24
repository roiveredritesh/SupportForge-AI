import { create } from 'zustand';

type Theme = 'light' | 'dark';

interface AppState {
  selectedProjectId: string | null;
  setSelectedProjectId: (id: string) => void;
  theme: Theme;
  toggleTheme: () => void;
}

export const useAppStore = create<AppState>((set) => ({
  selectedProjectId: null,
  setSelectedProjectId: (id) => set({ selectedProjectId: id }),
  theme: 'light',
  toggleTheme: () => set((s) => ({ theme: s.theme === 'light' ? 'dark' : 'light' })),
}));
