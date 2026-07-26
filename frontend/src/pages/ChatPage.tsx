import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { ScreenshotDropzone } from '../components/ScreenshotDropzone';
import { ConversationSidebar } from '../components/ConversationSidebar';
import { MessageThread } from '../components/MessageThread';
import { ProjectSwitcher } from '../components/ProjectSwitcher';
import { useChatQueryStream } from '../hooks/useChatQueryStream';
import { useAppStore } from '../store/useAppStore';
import { useConversation } from '../hooks/useConversations';
import { useSubmitFeedback } from '../hooks/useSubmitFeedback';
import type { MessageBubbleActions } from '../components/MessageBubble';

export default function ChatPage() {
  const { selectedProjectId } = useAppStore();
  const queryClient = useQueryClient();
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [screenshotBase64, setScreenshotBase64] = useState<string | undefined>();
  const [pendingQuery, setPendingQuery] = useState<string | null>(null);
  const chatStream = useChatQueryStream();
  const submitFeedback = useSubmitFeedback();
  const conversationQuery = useConversation(activeConversationId);
  const messageCountBeforeSubmit = useRef(0);

  // Once the backend has persisted the turn we just streamed, drop the local "pending" bubble
  // and let it render from the refetched history instead — otherwise it would show twice.
  useEffect(() => {
    if (!pendingQuery || chatStream.isStreaming) return;
    const messages = conversationQuery.data?.messages;
    if (messages && messages.length > messageCountBeforeSubmit.current) {
      setPendingQuery(null);
      chatStream.reset();
    }
  }, [conversationQuery.data, pendingQuery, chatStream, chatStream.isStreaming]);

  useEffect(() => {
    if (chatStream.conversationId && chatStream.conversationId !== activeConversationId) {
      setActiveConversationId(chatStream.conversationId);
    }
  }, [chatStream.conversationId, activeConversationId]);

  useEffect(() => {
    if (!chatStream.isStreaming && chatStream.conversationId) {
      void queryClient.invalidateQueries({ queryKey: ['conversation', chatStream.conversationId] });
      void queryClient.invalidateQueries({ queryKey: ['conversations', selectedProjectId] });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chatStream.isStreaming]);

  const handleNewChat = () => {
    setActiveConversationId(null);
    setPendingQuery(null);
    chatStream.reset();
    setQuery('');
  };

  const handleSubmit = () => {
    if (!selectedProjectId || !query.trim()) return;
    messageCountBeforeSubmit.current = conversationQuery.data?.messages.length ?? 0;
    setPendingQuery(query);
    void chatStream.start({
      projectId: selectedProjectId,
      query,
      screenshotBase64,
      conversationId: activeConversationId ?? undefined,
    });
    setQuery('');
  };

  const messages = conversationQuery.data?.messages ?? [];
  const lastUserQuery = !pendingQuery ? [...messages].reverse().find((m) => m.role === 'user')?.content : undefined;
  const lastAssistantActions: MessageBubbleActions | undefined = lastUserQuery
    ? {
        onCopy: () => navigator.clipboard.writeText([...messages].reverse().find((m) => m.role === 'assistant')?.content ?? ''),
        onMarkUseful: (useful) =>
          submitFeedback.mutate({ projectId: selectedProjectId!, query: lastUserQuery, useful, escalated: false }),
        onEscalate: () =>
          submitFeedback.mutate({ projectId: selectedProjectId!, query: lastUserQuery, escalated: true }),
      }
    : undefined;

  if (!selectedProjectId) {
    return (
      <div className="mx-auto max-w-3xl space-y-4 p-6">
        <h1 className="text-xl font-semibold">Chat</h1>
        <p className="text-sm text-gray-500">
          Select a project first on the <Link className="underline" to="/">home page</Link> before asking a question.
        </p>
      </div>
    );
  }

  return (
    <div className="flex h-[calc(100vh-4rem)]">
      <ConversationSidebar
        projectId={selectedProjectId}
        activeConversationId={activeConversationId}
        onSelect={setActiveConversationId}
        onNewChat={handleNewChat}
      />

      <div className="flex flex-1 flex-col">
        <div className="flex items-center justify-between border-b border-slate-200 p-3 dark:border-gray-700">
          <ProjectSwitcher hasMessages={!!(conversationQuery.data?.messages.length || pendingQuery)} onSwitch={handleNewChat} />
        </div>

        <MessageThread
          messages={messages}
          lastAssistantActions={lastAssistantActions}
          pending={
            pendingQuery
              ? {
                  query: pendingQuery,
                  draft: chatStream.draft,
                  confidence: chatStream.confidence,
                  sources: chatStream.sources,
                  actions: chatStream.draft
                    ? {
                        onCopy: () => navigator.clipboard.writeText(chatStream.draft),
                        onMarkUseful: (useful) =>
                          submitFeedback.mutate({ projectId: selectedProjectId, query: pendingQuery, useful, escalated: false }),
                        onEscalate: () =>
                          submitFeedback.mutate({ projectId: selectedProjectId, query: pendingQuery, escalated: true }),
                      }
                    : undefined,
                }
              : undefined
          }
        />

        {chatStream.isStreaming && !chatStream.draft && (
          <p className="px-6 text-sm text-gray-500">Triage → Research → Analysis → Drafting...</p>
        )}
        {chatStream.error && (
          <p className="px-6 text-sm text-red-600">
            Something went wrong. <button className="underline" onClick={chatStream.retry}>Retry</button>
          </p>
        )}

        <div className="space-y-2 border-t border-slate-200 p-3 dark:border-gray-700">
          <ScreenshotDropzone onImageSelected={setScreenshotBase64} />
          <div className="flex gap-2">
            <textarea
              className="h-16 flex-1 rounded-lg border border-slate-300 p-3 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-800"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Describe the issue..."
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault();
                  handleSubmit();
                }
              }}
            />
            <button
              className="self-end rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700 disabled:opacity-50"
              onClick={handleSubmit}
              disabled={!query.trim() || chatStream.isStreaming}
            >
              Ask Agent
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
