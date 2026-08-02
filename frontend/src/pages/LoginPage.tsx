import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useLogin } from '../hooks/useLogin';

export default function LoginPage() {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const login = useLogin();
  const navigate = useNavigate();

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    login.mutate(
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
          <h1 className="text-lg font-semibold">Sign in</h1>
          <p className="text-sm text-slate-500 dark:text-gray-400">SupportForge AI</p>
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
        {login.isError && (
          <p className="text-sm text-red-600 dark:text-red-400">Invalid username or password.</p>
        )}
        <button
          type="submit"
          className="w-full rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700 disabled:opacity-50"
          disabled={login.isPending}
        >
          {login.isPending ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </div>
  );
}
