import { useConversations, useDeleteConversation } from '../hooks/useConversations';

interface Props {
  projectId: string;
  activeConversationId: string | null;
  onSelect: (id: string) => void;
  onNewChat: () => void;
}

export function ConversationSidebar({ projectId, activeConversationId, onSelect, onNewChat }: Props) {
  const { data: conversations } = useConversations(projectId);
  const deleteConversation = useDeleteConversation();

  return (
    <div className="flex h-full w-64 flex-col border-r border-slate-200 dark:border-gray-700">
      <div className="p-3">
        <button
          className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm hover:bg-slate-100 dark:border-gray-600 dark:hover:bg-gray-700"
          onClick={onNewChat}
        >
          + New chat
        </button>
      </div>
      <ul className="flex-1 space-y-1 overflow-y-auto px-2 pb-3">
        {conversations?.map((c) => (
          <li key={c.id}>
            <div
              className={`group flex items-center justify-between rounded-lg px-3 py-2 text-sm cursor-pointer ${
                c.id === activeConversationId ? 'bg-indigo-100 dark:bg-indigo-900/40' : 'hover:bg-slate-100 dark:hover:bg-gray-700'
              }`}
              onClick={() => onSelect(c.id)}
            >
              <div className="truncate">
                <div className="truncate font-medium">{c.title}</div>
                <div className="text-xs text-gray-500 dark:text-gray-400">
                  {new Date(c.updatedAt).toLocaleString()}
                </div>
              </div>
              <button
                className="ml-2 hidden text-xs text-red-600 group-hover:block"
                onClick={(e) => {
                  e.stopPropagation();
                  deleteConversation.mutate(c.id);
                  if (c.id === activeConversationId) onNewChat();
                }}
              >
                Delete
              </button>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
