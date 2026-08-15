interface Props {
  participantIds: string[];
}

// U27: avatars/initials of who's currently viewing this conversation (from usePresence's roster).
// userIds, not usernames -- ConversationHub only ever broadcasts the NameIdentifier claim, so this
// renders an initial derived from the id itself rather than resolving a display name.
export function PresenceIndicator({ participantIds }: Props) {
  if (participantIds.length === 0) return null;

  return (
    <div className="flex items-center -space-x-2" aria-label="Currently viewing">
      {participantIds.map((id) => (
        <div
          key={id}
          title={id}
          className="flex h-7 w-7 items-center justify-center rounded-full border-2 border-white bg-indigo-500 text-xs font-medium text-white dark:border-gray-800"
        >
          {id.slice(0, 2).toUpperCase()}
        </div>
      ))}
    </div>
  );
}
