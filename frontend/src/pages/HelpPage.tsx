import { useAuthStore } from '../store/useAuthStore';

// Same convention ChatPage.tsx's ELEVATED_ROLES uses.
const ELEVATED_ROLES = new Set(['L2', 'L3', 'Admin']);

export default function HelpPage() {
  const role = useAuthStore((s) => s.role);
  const isElevated = !!role && ELEVATED_ROLES.has(role);
  const isAdmin = role === 'Admin';

  return (
    <div className="mx-auto max-w-3xl space-y-8 p-6">
      <h1 className="text-xl font-semibold">Help</h1>

      <section className="space-y-2">
        <h2 className="text-lg font-medium">Getting started</h2>
        <ul className="list-disc space-y-1 pl-5 text-sm text-slate-700 dark:text-gray-300">
          <li>
            Ask a question: open <strong>AI Chat</strong>, pick a project, and describe your issue in the
            message box. Press Enter (or click Ask Agent) to submit.
          </li>
          <li>
            Give feedback: after the agent replies, use the useful / not useful controls on its answer
            to tell us whether it helped.
          </li>
          <li>
            Escalate a conversation: if an answer doesn't resolve your issue, click Escalate on that
            answer to hand it off to a level 2/3 engineer.
          </li>
        </ul>
      </section>

      {isElevated && (
        <section className="space-y-2">
          <h2 className="text-lg font-medium">For L2/L3 engineers</h2>
          <ul className="list-disc space-y-1 pl-5 text-sm text-slate-700 dark:text-gray-300">
            <li>Claim an escalation: open the Escalation Queue and claim any unassigned item to work on it.</li>
            <li>View My Issues: open My Issues to see the escalations currently assigned to you.</li>
            <li>
              See code detail, commit history, and blast radius: relevant answers in AI Chat show these
              panels alongside the response when the agent found matching code.
            </li>
            <li>
              Invite another engineer into a conversation: in AI Chat, use the Invite Engineer button to
              pull a colleague into the current conversation.
            </li>
          </ul>
        </section>
      )}

      {isAdmin && (
        <section className="space-y-2">
          <h2 className="text-lg font-medium">For Admins</h2>
          <ul className="list-disc space-y-1 pl-5 text-sm text-slate-700 dark:text-gray-300">
            <li>Register an org: from the Register page, create a new organization.</li>
            <li>Register an employee: in Settings, use the Employees section to add a new employee.</li>
            <li>
              Connect an MCP server: in Settings, use the Connected Apps (Integration Hub) section to
              connect a new MCP server.
            </li>
            <li>Manage projects and KB sources: in Settings, use the Projects section to add and edit projects and their knowledge base sources.</li>
          </ul>
        </section>
      )}
    </div>
  );
}
