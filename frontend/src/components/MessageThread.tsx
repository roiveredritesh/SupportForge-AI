import { MessageBubble, type MessageBubbleActions } from './MessageBubble';
import { ThinkingIndicator } from './ThinkingIndicator';
import type { ChatMessage } from '../hooks/useConversations';
import type { ChatSource, CommitInfo } from '../hooks/useChatQuery';

interface PendingTurn {
  query: string;
  draft: string;
  confidence?: number;
  sources?: ChatSource[];
  totalTokensUsed?: number;
  codeDetails?: string[];
  commitHistory?: CommitInfo[];
  actions?: MessageBubbleActions;
}

interface Props {
  messages: ChatMessage[];
  pending?: PendingTurn;
  lastAssistantActions?: MessageBubbleActions;
}

export function MessageThread({ messages, pending, lastAssistantActions }: Props) {
  const lastAssistantId = !pending
    ? [...messages].reverse().find((m) => m.role === 'assistant')?.id
    : undefined;

  return (
    <div className="flex-1 space-y-4 overflow-y-auto p-6">
      {messages.map((m) => (
        <MessageBubble
          key={m.id}
          role={m.role}
          content={m.content}
          confidence={m.confidence}
          sources={m.sources}
          totalTokensUsed={m.totalTokensUsed}
          actions={m.id === lastAssistantId ? lastAssistantActions : undefined}
        />
      ))}

      {pending && (
        <>
          <MessageBubble role="user" content={pending.query} />
          {pending.draft ? (
            <MessageBubble
              role="assistant"
              content={pending.draft}
              confidence={pending.confidence}
              sources={pending.sources}
              totalTokensUsed={pending.totalTokensUsed}
              codeDetails={pending.codeDetails}
              commitHistory={pending.commitHistory}
              actions={pending.actions}
            />
          ) : (
            <ThinkingIndicator />
          )}
        </>
      )}

      {messages.length === 0 && !pending && (
        <p className="text-sm text-gray-500">Ask a question to start this conversation.</p>
      )}
    </div>
  );
}
