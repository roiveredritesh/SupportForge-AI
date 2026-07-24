import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ThemeToggle } from './ThemeToggle';
import { useAppStore } from '../store/useAppStore';

describe('ThemeToggle', () => {
  it('toggles the theme and applies the dark class to the document root', () => {
    render(<ThemeToggle />);

    expect(useAppStore.getState().theme).toBe('light');
    expect(document.documentElement.classList.contains('dark')).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: /dark mode/i }));

    expect(useAppStore.getState().theme).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });
});
