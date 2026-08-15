import { useState } from 'react';
import { useOrgs } from '../hooks/useOrgs';
import { useMcpCatalog, useMcpConnections, useConnectMcpServer, useDisconnectMcpServer } from '../hooks/useMcpConnections';
import { RequireRole } from './RequireRole';

const inputClass =
  'mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900';

// Sprint 3 (U16): Admin-only "Connected Apps" section -- lists supported MCP servers (GitHub only
// this sprint), a connect flow (credential input, e.g. a PAT), and per-connection tool checkboxes
// with tooltips explaining what each tool does. Split out of AdminPage.tsx into its own file,
// following the same pattern EmployeesSection.tsx already established (that file's own comment
// notes AdminPage was ~365 lines before its own extraction).
export function ConnectedAppsSection() {
  const { data: orgs } = useOrgs();
  const orgId = orgs?.[0]?.id;
  const { data: catalog } = useMcpCatalog(orgId);
  const { data: connections } = useMcpConnections(orgId);
  const connect = useConnectMcpServer();
  const disconnect = useDisconnectMcpServer();

  const [serverType, setServerType] = useState('');
  const [credential, setCredential] = useState('');
  const [enabledTools, setEnabledTools] = useState<string[]>([]);

  const resetForm = () => {
    setServerType('');
    setCredential('');
    setEnabledTools([]);
  };

  const toggleTool = (toolName: string) => {
    setEnabledTools((prev) => (prev.includes(toolName) ? prev.filter((t) => t !== toolName) : [...prev, toolName]));
  };

  const handleConnect = () => {
    if (!orgId || !serverType || !credential) return;
    connect.mutate({ orgId, serverType, credential, enabledTools }, { onSuccess: resetForm });
  };

  const handleDisconnect = (type: string) => {
    if (!orgId) return;
    disconnect.mutate({ orgId, serverType: type });
  };

  const selectedServerTools = catalog?.find((c) => c.serverType === serverType)?.tools ?? [];

  if (!orgId) return null;

  return (
    <RequireRole role="Admin">
      <section className="space-y-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <h2 className="font-medium">Connected Apps</h2>

        <ul className="space-y-2">
          {connections?.map((c) => (
            <li key={c.serverType} className="flex items-center justify-between rounded-lg bg-slate-50 px-3 py-2 text-sm dark:bg-gray-900">
              <span>
                <span className="font-medium">{c.serverType}</span> — tools: {c.enabledTools.join(', ') || 'none'}
              </span>
              <button
                className="text-sm text-red-600 hover:underline dark:text-red-400"
                onClick={() => handleDisconnect(c.serverType)}
                disabled={disconnect.isPending}
              >
                Disconnect
              </button>
            </li>
          ))}
          {connections?.length === 0 && <li className="text-sm text-gray-500">No apps connected yet.</li>}
        </ul>

        <div className="space-y-2 border-t border-slate-100 pt-3 dark:border-gray-700">
          <label className="block text-sm">
            Server
            <select
              className={inputClass}
              value={serverType}
              onChange={(e) => {
                setServerType(e.target.value);
                setEnabledTools([]);
              }}
            >
              <option value="">Select a server...</option>
              {catalog?.map((c) => (
                <option key={c.serverType} value={c.serverType}>
                  {c.serverType}
                </option>
              ))}
            </select>
          </label>

          {serverType && (
            <label className="block text-sm">
              {serverType === 'github' ? 'Personal Access Token' : 'Credential'}
              <input className={inputClass} type="password" value={credential} onChange={(e) => setCredential(e.target.value)} />
            </label>
          )}

          {selectedServerTools.length > 0 && (
            <fieldset className="text-sm">
              <legend>Enabled tools</legend>
              {selectedServerTools.map((tool) => (
                <label key={tool.name} className="flex items-center gap-2" title={tool.description}>
                  <input type="checkbox" checked={enabledTools.includes(tool.name)} onChange={() => toggleTool(tool.name)} />
                  {tool.name} <span className="text-xs text-gray-500">({tool.minRole}+)</span>
                </label>
              ))}
            </fieldset>
          )}

          <button
            className="rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700"
            onClick={handleConnect}
            disabled={connect.isPending || !serverType || !credential}
          >
            Connect
          </button>
        </div>
      </section>
    </RequireRole>
  );
}
