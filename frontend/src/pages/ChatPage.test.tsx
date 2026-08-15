import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import ChatPage from './ChatPage';
import { useAppStore } from '../store/useAppStore';

vi.mock('../lib/apiClient', () => ({
  apiClient: {
    get: vi.fn().mockResolvedValue({ data: { id: 'conv1', projectId: 'proj1', title: 't', messages: [] } }),
    post: vi.fn().mockResolvedValue({ data: {} }),
    defaults: { baseURL: 'http://localhost/api' },
  },
}));

const startMock = vi.fn();
vi.mock('../hooks/useChatQueryStream', () => ({
  useChatQueryStream: () => ({
    draft: '',
    confidence: undefined,
    conversationId: undefined,
    sources: [],
    totalTokensUsed: undefined,
    codeDetails: undefined,
    commitHistory: undefined,
    isStreaming: false,
    error: undefined,
    start: startMock,
    retry: vi.fn(),
    reset: vi.fn(),
  }),
}));

function renderChatPage() {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ChatPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('ChatPage', () => {
  beforeEach(() => {
    startMock.mockClear();
    useAppStore.setState({ selectedProjectId: 'proj1' });
  });

  // U12: the version field is free text (no version registry to back a dropdown), and it plus the
  // parsed config both flow into the same query-submission call the screenshot/query text already use.
  it('submits the entered product version and parsed config alongside the query', () => {
    renderChatPage();

    fireEvent.change(screen.getByPlaceholderText('Describe the issue...'), { target: { value: 'why is checkout failing' } });
    fireEvent.change(screen.getByLabelText('Product version'), { target: { value: '3.2' } });
    fireEvent.change(screen.getByLabelText('Config'), { target: { value: 'env=staging, region=eu' } });
    fireEvent.click(screen.getByText('Ask Agent'));

    expect(startMock).toHaveBeenCalledWith(
      expect.objectContaining({
        projectId: 'proj1',
        query: 'why is checkout failing',
        productVersion: '3.2',
        config: { env: 'staging', region: 'eu' },
      }),
    );
  });

  it('omits productVersion and config when both fields are left blank', () => {
    renderChatPage();

    fireEvent.change(screen.getByPlaceholderText('Describe the issue...'), { target: { value: 'why is checkout failing' } });
    fireEvent.click(screen.getByText('Ask Agent'));

    expect(startMock).toHaveBeenCalledWith(
      expect.objectContaining({ productVersion: undefined, config: undefined }),
    );
  });

  it('ignores config entries without "=" and blank entries', () => {
    renderChatPage();

    fireEvent.change(screen.getByPlaceholderText('Describe the issue...'), { target: { value: 'q' } });
    fireEvent.change(screen.getByLabelText('Config'), { target: { value: 'env=staging, not-a-pair, ' } });
    fireEvent.click(screen.getByText('Ask Agent'));

    expect(startMock).toHaveBeenCalledWith(expect.objectContaining({ config: { env: 'staging' } }));
  });
});
