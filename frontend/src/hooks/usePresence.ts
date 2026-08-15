import { useEffect, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { apiClient } from '../lib/apiClient';
import { useAuthStore } from '../store/useAuthStore';

// U26/U27: ConversationHub is mounted at the API host's root ("/hubs/conversation" in Program.cs),
// not under "/api" like every REST endpoint -- apiClient's baseURL always ends in "/api"
// (see .env.production), so this strips that suffix rather than hardcoding a second base URL.
function hubUrl(): string {
  const apiBase = apiClient.defaults.baseURL ?? '';
  return `${apiBase.replace(/\/api\/?$/, '')}/hubs/conversation`;
}

// U27: presence roster for PresenceIndicator -- who's currently viewing this one conversation.
// Reuses the same conversation-scoped SignalR group ChatController broadcasts new messages into
// (ConversationHub.GroupName), just for join/leave/"presence" instead of chat content.
export function usePresence(conversationId: string | null) {
  const [participantIds, setParticipantIds] = useState<string[]>([]);

  useEffect(() => {
    if (!conversationId) {
      setParticipantIds([]);
      return;
    }

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl(), { accessTokenFactory: () => useAuthStore.getState().accessToken ?? '' })
      .withAutomaticReconnect()
      .build();

    connection.on('presence', (payload: { conversationId: string; userIds: string[] }) => {
      if (payload.conversationId === conversationId) setParticipantIds(payload.userIds);
    });

    let cancelled = false;
    void connection
      .start()
      .then(() => {
        if (!cancelled) return connection.invoke('JoinConversation', conversationId);
      })
      .catch(() => {
        // ponytail: presence is a nice-to-have overlay on top of the chat itself, which already
        // works over plain HTTP -- a failed hub connection (e.g. no WebSocket support in an old
        // proxy) degrades to "no presence indicator," not a broken chat.
      });

    return () => {
      cancelled = true;
      setParticipantIds([]);
      void connection
        .invoke('LeaveConversation', conversationId)
        .catch(() => {})
        .finally(() => void connection.stop());
    };
  }, [conversationId]);

  return { participantIds };
}
