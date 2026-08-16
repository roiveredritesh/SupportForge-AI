import { useEffect, type ComponentType } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { ThemeToggle } from './ThemeToggle';
import { useAuthStore, type AppRole } from '../store/useAuthStore';
import { useAppStore } from '../store/useAppStore';
import { useProjects } from '../hooks/useProjects';

const NAV_ITEMS: { to: string; label: string; end: boolean; icon: ComponentType<{ className?: string }>; roles?: AppRole[] }[] = [
  { to: '/', label: 'Dashboard', end: true, icon: DashboardIcon },
  { to: '/query', label: 'AI Chat', end: false, icon: ChatIcon },
  // U21: server gate is GET /api/escalations' [Authorize(Roles="L2,L3,Admin")] -- this is only the
  // client-side convenience mirror, same convention EmployeesSection's RequireRole established.
  { to: '/escalations', label: 'Escalations', end: false, icon: EscalationIcon, roles: ['L2', 'L3', 'Admin'] },
  { to: '/my-issues', label: 'My Issues', end: false, icon: EscalationIcon, roles: ['L2', 'L3', 'Admin'] },
  { to: '/settings', label: 'Settings', end: false, icon: SettingsIcon },
];

export default function Layout() {
  const logout = useAuthStore((s) => s.logout);
  const role = useAuthStore((s) => s.role);
  const navigate = useNavigate();
  const { data: projects } = useProjects();
  const { selectedProjectId, setSelectedProjectId } = useAppStore();
  const visibleNavItems = NAV_ITEMS.filter((item) => !item.roles || (role && item.roles.includes(role)));

  // A projectId persisted from a previous session (deleted project, different
  // backend, stale browser profile) doesn't match any option in the project
  // <select>s, which fall back to displaying the first project as if selected
  // while still submitting the stale id underneath. Clear it once real data loads.
  useEffect(() => {
    if (projects && selectedProjectId && !projects.some((p) => p.id === selectedProjectId)) {
      setSelectedProjectId(null);
    }
  }, [projects, selectedProjectId, setSelectedProjectId]);

  const handleLogout = () => {
    logout();
    navigate('/login', { replace: true });
  };

  return (
    <div className="flex min-h-screen bg-slate-50 dark:bg-gray-900 dark:text-gray-100">
      <aside className="w-60 shrink-0 border-r border-slate-200 bg-white dark:border-gray-700 dark:bg-gray-800 flex flex-col">
        <div className="flex items-center gap-2 px-5 py-5">
          <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-indigo-600 text-sm font-bold text-white">
            S
          </span>
          <div>
            <p className="text-sm font-semibold leading-tight">SupportForge AI</p>
            <p className="text-[11px] uppercase tracking-wide text-slate-400 dark:text-gray-500">
              Support Pipeline
            </p>
          </div>
        </div>

        <nav className="flex-1 space-y-1 px-3">
          {visibleNavItems.map(({ to, label, end, icon: Icon }) => (
            <NavLink
              key={to}
              to={to}
              end={end}
              className={({ isActive }) =>
                `flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors ${
                  isActive
                    ? 'bg-indigo-50 text-indigo-600 dark:bg-indigo-500/10 dark:text-indigo-400'
                    : 'text-slate-600 hover:bg-slate-100 dark:text-gray-300 dark:hover:bg-gray-700'
                }`
              }
            >
              <Icon className="h-4 w-4" />
              {label}
            </NavLink>
          ))}
        </nav>
      </aside>

      <div className="flex flex-1 flex-col">
        <header className="flex h-14 items-center justify-end gap-3 border-b border-slate-200 px-6 dark:border-gray-700">
          <ThemeToggle />
          <button
            onClick={handleLogout}
            className="rounded-lg border border-slate-300 px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-100 dark:border-gray-600 dark:text-gray-300 dark:hover:bg-gray-700"
          >
            Log out
          </button>
        </header>
        <main className="flex-1">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

function DashboardIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
      <path d="M3 3h6v6H3V3zm8 0h6v4h-6V3zm0 6h6v8h-6V9zM3 11h6v6H3v-6z" />
    </svg>
  );
}

function ChatIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
      <path
        fillRule="evenodd"
        d="M2 5a2 2 0 012-2h12a2 2 0 012 2v7a2 2 0 01-2 2H9l-4 3v-3H4a2 2 0 01-2-2V5z"
        clipRule="evenodd"
      />
    </svg>
  );
}

function EscalationIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
      <path
        fillRule="evenodd"
        d="M8.257 3.099c.765-1.36 2.72-1.36 3.486 0l6.28 11.19c.75 1.334-.213 2.987-1.743 2.987H3.72c-1.53 0-2.493-1.653-1.743-2.987l6.28-11.19zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 8a1 1 0 100-2 1 1 0 000 2z"
        clipRule="evenodd"
      />
    </svg>
  );
}

function SettingsIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
      <path
        fillRule="evenodd"
        d="M11.49 3.17c-.38-1.56-2.6-1.56-2.98 0a1.532 1.532 0 01-2.286.948c-1.372-.836-2.942.734-2.106 2.106.54.886.061 2.042-.947 2.287-1.561.379-1.561 2.6 0 2.978a1.532 1.532 0 01.947 2.287c-.836 1.372.734 2.942 2.106 2.106a1.532 1.532 0 012.287.947c.379 1.561 2.6 1.561 2.978 0a1.533 1.533 0 012.287-.947c1.372.836 2.942-.734 2.106-2.106a1.533 1.533 0 01.947-2.287c1.561-.379 1.561-2.6 0-2.978a1.532 1.532 0 01-.947-2.287c.836-1.372-.734-2.942-2.106-2.106a1.532 1.532 0 01-2.287-.947zM10 13a3 3 0 100-6 3 3 0 000 6z"
        clipRule="evenodd"
      />
    </svg>
  );
}
