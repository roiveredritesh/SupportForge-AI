import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { isAxiosError } from 'axios';
import { useRegister } from '../hooks/useRegister';
import { type FieldErrors, validatePasswordLength, validateRequired } from '../lib/formValidation';

// AuthController.Register returns BadRequest(string[]) for Identity validation failures (password
// complexity, duplicate username, etc.) or BadRequest(string) for a missing-field check -- surface
// whichever the server actually said instead of a hardcoded guess, since a password-complexity
// rejection and a duplicate-username rejection look identical to the user otherwise.
function getServerErrors(error: unknown) {
  if (!isAxiosError(error)) return null;
  const data = error.response?.data;
  if (Array.isArray(data) && data.every((d) => typeof d === 'string') && data.length > 0) {
    return (
      <ul className="list-disc space-y-0.5 pl-4">
        {data.map((message, i) => (
          <li key={i}>{message}</li>
        ))}
      </ul>
    );
  }
  if (typeof data === 'string' && data.length > 0) return <p>{data}</p>;
  return null;
}

// Self-service org+Admin signup -- mirrors LoginPage.tsx exactly. AuthController.Register creates
// a new user, a new Org, and makes the user that org's Admin (one org per admin). Employees are
// added afterward by that Admin from AdminPage's Employees section (POST /api/orgs/{orgId}/employees),
// not through this form.
export default function RegisterPage() {
  const [orgName, setOrgName] = useState('');
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [contactPerson, setContactPerson] = useState('');
  const [contactNumber, setContactNumber] = useState('');
  const [industry, setIndustry] = useState('');
  const [address, setAddress] = useState('');
  const [errors, setErrors] = useState<FieldErrors>({});
  const register = useRegister();
  const navigate = useNavigate();

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const fieldErrors = validateRequired({
      orgName,
      userName,
      password,
      contactPerson,
      contactNumber,
      industry,
    });
    const passwordError = validatePasswordLength(password);
    if (passwordError) fieldErrors.password = passwordError;
    if (Object.keys(fieldErrors).length > 0) {
      setErrors(fieldErrors);
      return;
    }
    setErrors({});
    register.mutate(
      {
        orgName,
        userName,
        password,
        contactPerson,
        contactNumber,
        industry,
        address: address || undefined,
      },
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
        <div>
          <label className="block text-sm">
            Organization Name
            <input
              className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
              value={orgName}
              onChange={(e) => setOrgName(e.target.value)}
              autoFocus
              aria-invalid={!!errors.orgName}
              aria-describedby={errors.orgName ? 'orgName-error' : undefined}
            />
          </label>
          {errors.orgName && (
            <p id="orgName-error" role="alert" className="text-xs text-red-600 dark:text-red-400">
              {errors.orgName}
            </p>
          )}
        </div>
        <div>
          <label className="block text-sm">
            Username
            <input
              className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
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
              type="password"
              className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
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
        <div>
          <label className="block text-sm">
            Contact Person
            <input
              className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
              value={contactPerson}
              onChange={(e) => setContactPerson(e.target.value)}
              aria-invalid={!!errors.contactPerson}
              aria-describedby={errors.contactPerson ? 'contactPerson-error' : undefined}
            />
          </label>
          {errors.contactPerson && (
            <p id="contactPerson-error" role="alert" className="text-xs text-red-600 dark:text-red-400">
              {errors.contactPerson}
            </p>
          )}
        </div>
        <div>
          <label className="block text-sm">
            Contact Number
            <input
              className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
              value={contactNumber}
              onChange={(e) => setContactNumber(e.target.value)}
              aria-invalid={!!errors.contactNumber}
              aria-describedby={errors.contactNumber ? 'contactNumber-error' : undefined}
            />
          </label>
          {errors.contactNumber && (
            <p id="contactNumber-error" role="alert" className="text-xs text-red-600 dark:text-red-400">
              {errors.contactNumber}
            </p>
          )}
        </div>
        <div>
          <label className="block text-sm">
            Industry
            <input
              className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
              value={industry}
              onChange={(e) => setIndustry(e.target.value)}
              aria-invalid={!!errors.industry}
              aria-describedby={errors.industry ? 'industry-error' : undefined}
            />
          </label>
          {errors.industry && (
            <p id="industry-error" role="alert" className="text-xs text-red-600 dark:text-red-400">
              {errors.industry}
            </p>
          )}
        </div>
        <label className="block text-sm">
          Address (optional)
          <input
            className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
            value={address}
            onChange={(e) => setAddress(e.target.value)}
          />
        </label>
        {register.isError && (
          <div className="text-sm text-red-600 dark:text-red-400">
            {getServerErrors(register.error) ?? (
              <p>Could not create your account. That username may already be taken.</p>
            )}
          </div>
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
