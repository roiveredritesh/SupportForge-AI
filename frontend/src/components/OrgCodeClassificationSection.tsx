import { useState, useEffect } from 'react';
import { useOrgs } from '../hooks/useOrgs';
import { useUpdateOrg } from '../hooks/useUpdateOrg';
import { RequireRole } from './RequireRole';

// Org-wide master switch for the code-graph Tier 2 LLM classification stage (sends up to 60 lines
// -- or 2KB for minified files -- of source content to the configured LLM provider). A project's
// own "Enable AI code classification" checkbox (SettingsProjectsPage) also has to be on; this is
// the org-level half of that two-key consent, Admin-only same as EmployeesSection/ConnectedAppsSection.
export function OrgCodeClassificationSection() {
  const { data: orgs } = useOrgs();
  const org = orgs?.[0];
  const updateOrg = useUpdateOrg();
  const [enabled, setEnabled] = useState(false);

  useEffect(() => {
    if (org) setEnabled(org.codeClassificationEnabled ?? false);
  }, [org]);

  if (!org) return null;

  const handleSave = () => {
    updateOrg.mutate({ ...org, codeClassificationEnabled: enabled });
  };

  return (
    <RequireRole role="Admin">
      <section className="space-y-3 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <h2 className="font-medium">Org Settings</h2>
        <label className="flex items-center gap-2 text-sm text-slate-600 dark:text-gray-300">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
          Enable AI code classification for this org
        </label>
        <p className="text-xs text-gray-500">
          Lets the code-graph classifier send file content to the configured AI model to tell business
          logic apart from vendored/generated code. Each project also needs its own toggle enabled
          below -- both must be on for that project.
        </p>
        <button
          className="rounded-lg bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700 disabled:opacity-50"
          onClick={handleSave}
          disabled={updateOrg.isPending}
        >
          Save
        </button>
        {updateOrg.isSuccess && <p className="text-sm text-emerald-600 dark:text-emerald-400">Saved.</p>}
      </section>
    </RequireRole>
  );
}
