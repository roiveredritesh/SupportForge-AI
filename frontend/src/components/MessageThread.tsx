import { MessageBubble, type MessageBubbleActions } from './MessageBubble';
import type { ChatMessage } from '../hooks/useConversations';
import type { Source } from '../hooks/useChatQuery';

interface PendingTurn {
  query: string;
  draft: string;
  confidence?: number;
  sources: Source[];
  actions?: MessageBubbleActions;
}

interface Props {
  messages: ChatMessage[];
  pending?: PendingTurn;
}

export function MessageThread({ messages, pending }: Props) {
  return (
    <div className="flex-1 space-y-4 overflow-y-auto p-6">
      {messages.map((m) => (
        <MessageBubble key={m.id} role={m.role} content={m.content} confidence={m.confidence} sources={m.sources} />
      ))}

      {pending && (
        <>
          <MessageBubble role="user" content={pending.query} />
          {pending.draft && (
            <MessageBubble
              role="assistant"
              content={pending.draft}
              confidence={pending.confidence}
              sources={pending.sources}
              actions={pending.actions}
            />
          )}
        </>
      )}

      {messages.length === 0 && !pending && (
        <p className="text-sm text-gray-500">Ask a question to start this conversation.</p>
      )}
    </div>
  );
}
