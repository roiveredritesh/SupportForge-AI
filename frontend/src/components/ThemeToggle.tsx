import { useEffect } from 'react';
import { useAppStore } from '../store/useAppStore';

export function ThemeToggle() {
  const { theme, toggleTheme } = useAppStore();

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark');
  }, [theme]);

  return (
    <button
      className="text-sm border rounded px-2 py-1 dark:bg-gray-800 dark:text-gray-100"
      onClick={toggleTheme}
      aria-label="Toggle dark mode"
    >
      {theme === 'dark' ? 'Light mode' : 'Dark mode'}
    </button>
  );
}
