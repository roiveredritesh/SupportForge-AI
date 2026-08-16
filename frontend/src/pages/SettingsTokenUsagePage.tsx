import { useMemo, useState } from 'react';
import { Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { useOrgs } from '../hooks/useOrgs';
import { useEmployees } from '../hooks/useEmployees';
import { useOrgTokenUsage } from '../hooks/useOrgTokenUsage';
import { RequireRole } from '../components/RequireRole';
import { SettingsNav } from '../components/SettingsNav';

const PAGE_SIZE = 25;

function toIsoDate(d: Date) {
  return d.toISOString().slice(0, 10);
}

function defaultFrom() {
  const d = new Date();
  d.setDate(d.getDate() - 30);
  return toIsoDate(d);
}

export default function SettingsTokenUsagePage() {
  const { data: orgs } = useOrgs();
  const orgId = orgs?.[0]?.id;
  const { data: employees } = useEmployees(orgId);

  const [from, setFrom] = useState(defaultFrom());
  const [to, setTo] = useState(toIsoDate(new Date()));
  const [projectId, setProjectId] = useState('');
  const [userId, setUserId] = useState('');
  const [page, setPage] = useState(0);

  // A from-date after to-date would fire an invalid/empty-range query -- block it client-side
  // rather than letting the request round-trip to learn that.
  const rangeInvalid = from > to;

  const { data, isLoading, isError } = useOrgTokenUsage(orgId, {
    from: rangeInvalid ? undefined : from,
    to: rangeInvalid ? undefined : to,
    projectId: projectId || undefined,
    userId: userId || undefined,
  });

  const employeeNameById = useMemo(() => {
    const map = new Map<string, string>();
    for (const e of employees ?? []) map.set(e.id, e.userName);
    return map;
  }, [employees]);

  const entries = data?.entries ?? [];
  const pageCount = Math.max(1, Math.ceil(entries.length / PAGE_SIZE));
  const pagedEntries = entries.slice(page * PAGE_SIZE, page * PAGE_SIZE + PAGE_SIZE);

  const resetPage = (fn: () => void) => {
    fn();
    setPage(0);
  };

  return (
    <RequireRole role="Admin">
      <div className="mx-auto max-w-4xl space-y-6 p-6">
        <h1 className="text-xl font-semibold">Settings</h1>
        <SettingsNav />

        <section className="space-y-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
          <h2 className="font-medium">Token Usage</h2>

          <div className="flex flex-wrap items-end gap-3 text-sm">
            <label className="block">
              From
              <input
                type="date"
                className="mt-1 block rounded-lg border border-slate-300 p-2 dark:border-gray-600 dark:bg-gray-900"
                value={from}
                onChange={(e) => resetPage(() => setFrom(e.target.value))}
              />
            </label>
            <label className="block">
              To
              <input
                type="date"
                className="mt-1 block rounded-lg border border-slate-300 p-2 dark:border-gray-600 dark:bg-gray-900"
                value={to}
                onChange={(e) => resetPage(() => setTo(e.target.value))}
              />
            </label>
            <label className="block">
              Project
              <select
                className="mt-1 block rounded-lg border border-slate-300 p-2 dark:border-gray-600 dark:bg-gray-900"
                value={projectId}
                onChange={(e) => resetPage(() => setProjectId(e.target.value))}
              >
                <option value="">All projects</option>
                {(data?.projectIds ?? []).map((id) => (
                  <option key={id} value={id}>
                    {id}
                  </option>
                ))}
              </select>
            </label>
            <label className="block">
              Employee
              <select
                className="mt-1 block rounded-lg border border-slate-300 p-2 dark:border-gray-600 dark:bg-gray-900"
                value={userId}
                onChange={(e) => resetPage(() => setUserId(e.target.value))}
              >
                <option value="">All employees</option>
                {(employees ?? []).map((e) => (
                  <option key={e.id} value={e.id}>
                    {e.userName}
                  </option>
                ))}
              </select>
            </label>
          </div>

          {rangeInvalid && (
            <p className="text-sm text-red-600 dark:text-red-400">
              "From" date must not be after "To" date.
            </p>
          )}

          {!rangeInvalid && isLoading && <p className="text-sm text-gray-500">Loading…</p>}
          {!rangeInvalid && isError && (
            <p className="text-sm text-red-600 dark:text-red-400">
              Could not load token usage. Try again.
            </p>
          )}
          {!rangeInvalid && !isLoading && !isError && entries.length === 0 && (
            <p className="text-sm text-gray-500">No token usage for this filter combination.</p>
          )}

          {!rangeInvalid && !isLoading && !isError && entries.length > 0 && (
            <>
              <ResponsiveContainer width="100%" height={220}>
                <LineChart data={data?.chart ?? []}>
                  <XAxis dataKey="date" tick={{ fontSize: 11 }} />
                  <YAxis allowDecimals={false} tick={{ fontSize: 11 }} />
                  <Tooltip />
                  <Line type="monotone" dataKey="count" stroke="#4f46e5" strokeWidth={2} dot={false} />
                </LineChart>
              </ResponsiveContainer>

              <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <thead>
                    <tr className="border-b border-slate-200 text-xs uppercase text-slate-400 dark:border-gray-700 dark:text-gray-500">
                      <th className="py-2 pr-3">Project</th>
                      <th className="py-2 pr-3">Employee</th>
                      <th className="py-2 pr-3">Tokens</th>
                      <th className="py-2 pr-3">Source</th>
                      <th className="py-2 pr-3">When</th>
                    </tr>
                  </thead>
                  <tbody>
                    {pagedEntries.map((entry, i) => (
                      <tr key={`${entry.projectId}-${entry.createdAt}-${i}`} className="border-b border-slate-100 dark:border-gray-800">
                        <td className="py-2 pr-3">{entry.projectId}</td>
                        <td className="py-2 pr-3">
                          {entry.userId ? employeeNameById.get(entry.userId) ?? entry.userId : 'Unknown'}
                        </td>
                        <td className="py-2 pr-3">{entry.totalTokens}</td>
                        <td className="py-2 pr-3">{entry.source}</td>
                        <td className="py-2 pr-3">{new Date(entry.createdAt).toLocaleString()}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {pageCount > 1 && (
                <div className="flex items-center justify-between text-sm">
                  <button
                    className="rounded border px-3 py-1 disabled:opacity-50"
                    onClick={() => setPage((p) => Math.max(0, p - 1))}
                    disabled={page === 0}
                  >
                    Previous
                  </button>
                  <span>
                    Page {page + 1} of {pageCount}
                  </span>
                  <button
                    className="rounded border px-3 py-1 disabled:opacity-50"
                    onClick={() => setPage((p) => Math.min(pageCount - 1, p + 1))}
                    disabled={page >= pageCount - 1}
                  >
                    Next
                  </button>
                </div>
              )}
            </>
          )}
        </section>
      </div>
    </RequireRole>
  );
}
