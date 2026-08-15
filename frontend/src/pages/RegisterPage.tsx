import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useRegister } from '../hooks/useRegister';

// Self-service org+Admin signup -- mirrors LoginPage.tsx exactly. AuthController.Register creates
// a new user, a new Org, and makes the user that org's Admin (one org per admin). Employees are
// added afterward by that Admin from AdminPage's Employees section (POST /api/orgs/{orgId}/employees),
// not through this form.
export default function RegisterPage() {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const register = useRegister();
  const navigate = useNavigate();

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    register.mutate(
      { userName, password },
      { onSuccess: () => navigate('/', { replace: true }) },
    );
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-slate-50 dark:bg-gray-900 dark:text-gray-100">
      <form
        onSubmit={handleSubmit}
        className="w-full max-w-sm space-y-4 rounded-xl border border-slate-200 bg-white p-6 dark:border-gray-700 dark:bg-gray-800"
      >
        <div>
          <h1 className="text-lg font-semibold">Create your organization</h1>
          <p className="text-sm text-slate-500 dark:text-gray-400">
            SupportForge AI — you'll be the Admin of a new org.
          </p>
        </div>
        <label className="block text-sm">
          Username
          <input
            className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
            value={userName}
            onChange={(e) => setUserName(e.target.value)}
            autoFocus
          />
        </label>
        <label className="block text-sm">
          Password
          <input
            type="password"
            className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </label>
        {register.isError && (
          <p className="text-sm text-red-600 dark:text-red-400">
            Could not create your account. That username may already be taken.
          </p>
        )}
        <button
          type="submit"
          className="w-full rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700 disabled:opacity-50"
          disabled={register.isPending}
        >
          {register.isPending ? 'Creating account…' : 'Create account'}
        </button>
        <p className="text-center text-sm text-slate-500 dark:text-gray-400">
          Already have an account?{' '}
          <Link to="/login" className="text-indigo-600 hover:underline dark:text-indigo-400">
            Sign in
          </Link>
        </p>
      </form>
    </div>
  );
}
