import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ResultsPanel } from './ResultsPanel';

describe('ResultsPanel', () => {
  it('renders the draft, sources, and confidence badge', () => {
    render(
      <ResultsPanel
        result={{
          draft: '**Try restarting the service.**',
          confidence: 0.85,
          sources: [{ label: 'KB: restart.md', url: 'kb/restart.md' }],
        }}
      />,
    );

    expect(screen.getByText('Try restarting the service.')).toBeInTheDocument();
    expect(screen.getByText('KB: restart.md')).toBeInTheDocument();
    expect(screen.getByText('High confidence')).toBeInTheDocument();
  });
});
