import { useFeedbackDashboard } from '../hooks/useFeedbackDashboard';
import { RequireRole } from './RequireRole';

// U18/U21: Admin-only feedback/staleness summary -- recent negative feedback plus a reason-code
// breakdown, reading GET /api/feedback/dashboard (already scoped server-side to the Admin's own
// org/projects). Follows the same RequireRole-wrapped-section pattern as EmployeesSection and
// ConnectedAppsSection.
export function FeedbackDashboardSection() {
  const { data } = useFeedbackDashboard();
  const recentNegative = data?.recentNegative ?? [];
  const reasonCodeBreakdown = data?.reasonCodeBreakdown ?? {};

  return (
    <RequireRole role="Admin">
      <section className="space-y-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <h2 className="font-medium">Feedback</h2>

        {Object.keys(reasonCodeBreakdown).length > 0 && (
          <div className="flex flex-wrap gap-2 text-sm">
            {Object.entries(reasonCodeBreakdown).map(([reason, count]) => (
              <span key={reason} className="rounded-full bg-amber-100 px-2 py-0.5 text-amber-700 dark:bg-amber-900 dark:text-amber-300">
                {reason}: {count}
              </span>
            ))}
          </div>
        )}

        <ul className="space-y-1 text-sm">
          {recentNegative.map((entry, i) => (
            <li key={i} className="rounded-lg bg-slate-50 px-3 py-2 dark:bg-gray-900">
              <span className="text-gray-500">[{entry.projectId}]</span> {entry.query}
              {entry.reasonCode && <span className="ml-2 text-xs text-amber-600 dark:text-amber-400">({entry.reasonCode})</span>}
            </li>
          ))}
          {recentNegative.length === 0 && <li className="text-sm text-gray-500">No negative feedback recorded yet.</li>}
        </ul>
      </section>
    </RequireRole>
  );
}
