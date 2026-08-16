import { NavLink } from 'react-router-dom';

// U3: shared sub-nav for the four /settings/* pages, mirroring Layout.tsx's own
// NavLink-with-isActive-className pattern for the active-link highlight.
const SETTINGS_NAV_ITEMS = [
  { to: '/settings/projects', label: 'Projects' },
  { to: '/settings/employees', label: 'Employees' },
  { to: '/settings/connected-apps', label: 'Connected Apps' },
  { to: '/settings/feedback', label: 'Feedback' },
  // U8: nav link always renders, same as the other Admin-only settings pages -- each page's own
  // RequireRole gate (not this nav) is what actually hides content from non-Admins.
  { to: '/settings/token-usage', label: 'Token Usage' },
];

export function SettingsNav() {
  return (
    <nav className="flex gap-1 border-b border-slate-200 dark:border-gray-700">
      {SETTINGS_NAV_ITEMS.map(({ to, label }) => (
        <NavLink
          key={to}
          to={to}
          className={({ isActive }) =>
            `border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
              isActive
                ? 'border-indigo-600 text-indigo-600 dark:border-indigo-400 dark:text-indigo-400'
                : 'border-transparent text-slate-500 hover:text-slate-700 dark:text-gray-400 dark:hover:text-gray-200'
            }`
          }
        >
          {label}
        </NavLink>
      ))}
    </nav>
  );
}
