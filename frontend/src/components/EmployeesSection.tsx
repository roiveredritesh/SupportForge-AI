import { useState } from 'react';
import { useOrgs } from '../hooks/useOrgs';
import { useEmployees, type EmployeeRole } from '../hooks/useEmployees';
import { useRegisterEmployee } from '../hooks/useRegisterEmployee';
import { useProjects } from '../hooks/useProjects';
import { RequireRole } from './RequireRole';
import { type FieldErrors, validatePasswordLength, validateRequired } from '../lib/formValidation';

const inputClass =
  'mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900';

// U8: Admin-only "Employees" section -- lists the org's employees (name, role, project scope) and
// registers new ones via POST /api/orgs/{orgId}/employees. Split out of AdminPage.tsx (already
// ~365 lines before this) into its own file, following the same one-concern-per-file pattern as
// AdminPage's other extracted pieces (FreshnessBadge, DeadLetterList) just promoted to a real
// module since this one needs its own data hooks. RequireRole wraps the whole section: it's a
// client-side mirror of the server's [Authorize(Roles="Admin")], the server is the real gate.
export function EmployeesSection() {
  const { data: orgs } = useOrgs();
  const orgId = orgs?.[0]?.id;
  const { data: employees } = useEmployees(orgId);
  const { data: projects } = useProjects();
  const registerEmployee = useRegisterEmployee();

  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<EmployeeRole>('L1');
  const [projectIds, setProjectIds] = useState<string[]>([]);
  const [errors, setErrors] = useState<FieldErrors>({});

  const resetForm = () => {
    setUserName('');
    setPassword('');
    setRole('L1');
    setProjectIds([]);
    setErrors({});
  };

  const handleRegister = () => {
    if (!orgId) return;
    const fieldErrors = validateRequired({ userName, password });
    const passwordError = validatePasswordLength(password);
    if (passwordError) fieldErrors.password = passwordError;
    if (Object.keys(fieldErrors).length > 0) {
      setErrors(fieldErrors);
      return;
    }
    setErrors({});
    registerEmployee.mutate({ orgId, userName, password, role, projectIds }, { onSuccess: resetForm });
  };

  const toggleProject = (projectId: string) => {
    setProjectIds((prev) => (prev.includes(projectId) ? prev.filter((id) => id !== projectId) : [...prev, projectId]));
  };

  if (!orgId) return null;

  return (
    <RequireRole role="Admin">
      <section className="space-y-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <h2 className="font-medium">Employees</h2>

        {employees?.length === 0 ? (
          <p className="text-sm text-gray-500">No employees registered yet.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead>
                <tr className="border-b border-slate-200 text-xs uppercase text-slate-400 dark:border-gray-700 dark:text-gray-500">
                  <th className="py-2 pr-3">Username</th>
                  <th className="py-2 pr-3">Role</th>
                  <th className="py-2 pr-3">Projects</th>
                </tr>
              </thead>
              <tbody>
                {employees?.map((e) => (
                  <tr key={e.id} className="border-b border-slate-100 dark:border-gray-800">
                    <td className="py-2 pr-3 font-medium">{e.userName}</td>
                    <td className="py-2 pr-3">{e.role}</td>
                    <td className="py-2 pr-3">
                      {e.projectIds.map((id) => projects?.find((p) => p.id === id)?.name ?? id).join(', ') || 'none'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <div className="space-y-2 border-t border-slate-100 pt-3 dark:border-gray-700">
          <div>
            <label className="block text-sm">
              Username
              <input
                className={inputClass}
                value={userName}
                onChange={(e) => setUserName(e.target.value)}
                aria-invalid={!!errors.userName}
                aria-describedby={errors.userName ? 'userName-error' : undefined}
              />
            </label>
            {errors.userName && (
              <p id="userName-error" role="alert" className="text-xs text-red-600 dark:text-red-400">
                {errors.userName}
              </p>
            )}
          </div>
          <div>
            <label className="block text-sm">
              Password
              <input
                className={inputClass}
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                aria-invalid={!!errors.password}
                aria-describedby={errors.password ? 'password-error' : undefined}
              />
            </label>
            {errors.password && (
              <p id="password-error" role="alert" className="text-xs text-red-600 dark:text-red-400">
                {errors.password}
              </p>
            )}
          </div>
          <label className="block text-sm">
            Role
            <select className={inputClass} value={role} onChange={(e) => setRole(e.target.value as EmployeeRole)}>
              <option value="L1">L1</option>
              <option value="L2">L2</option>
              <option value="L3">L3</option>
            </select>
          </label>
          <fieldset className="text-sm">
            <legend>Project scope</legend>
            {projects?.map((p) => (
              <label key={p.id} className="flex items-center gap-2">
                <input type="checkbox" checked={projectIds.includes(p.id)} onChange={() => toggleProject(p.id)} />
                {p.name}
              </label>
            ))}
          </fieldset>
          <button
            className="rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700"
            onClick={handleRegister}
            disabled={registerEmployee.isPending}
          >
            Register Employee
          </button>
        </div>
      </section>
    </RequireRole>
  );
}
