import { useState } from 'react';
import { useChangePassword } from '../hooks/useChangePassword';

export default function AccountPage() {
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const changePassword = useChangePassword();

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    changePassword.mutate(
      { currentPassword, newPassword },
      {
        onSuccess: () => {
          setCurrentPassword('');
          setNewPassword('');
        },
      },
    );
  };

  return (
    <div className="mx-auto max-w-md p-6">
      <h1 className="text-lg font-semibold">Account</h1>
      <form onSubmit={handleSubmit} className="mt-4 space-y-4 rounded-xl border border-slate-200 bg-white p-6 dark:border-gray-700 dark:bg-gray-800">
        <h2 className="text-sm font-semibold">Change password</h2>
        <label className="block text-sm">
          Current password
          <input
            type="password"
            className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
            value={currentPassword}
            onChange={(e) => setCurrentPassword(e.target.value)}
          />
        </label>
        <label className="block text-sm">
          New password
          <input
            type="password"
            className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
          />
        </label>
        {changePassword.isError && (
          <p className="text-sm text-red-600 dark:text-red-400">
            Could not change your password. Check your current password and try again.
          </p>
        )}
        {changePassword.isSuccess && (
          <p className="text-sm text-emerald-600 dark:text-emerald-400">Password changed.</p>
        )}
        <button
          type="submit"
          className="w-full rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700 disabled:opacity-50"
          disabled={changePassword.isPending}
        >
          {changePassword.isPending ? 'Changing…' : 'Change password'}
        </button>
      </form>
    </div>
  );
}
