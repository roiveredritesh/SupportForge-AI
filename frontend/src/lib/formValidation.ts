// Shared inline-validation convention for controlled forms (KTD6: plain functions, no form
// library — every form in this app is already a controlled useState form).
//
// Each page holds its own `useState<FieldErrors>` and renders errors per field, e.g.:
//
//   {errors.password && (
//     <p id="password-error" role="alert" className="text-xs text-red-600 dark:text-red-400">
//       {errors.password}
//     </p>
//   )}
//   <input
//     aria-invalid={!!errors.password}
//     aria-describedby={errors.password ? 'password-error' : undefined}
//     ...
//   />

export type FieldErrors = Record<string, string>;

// Matches ASP.NET Core Identity's default RequiredLength (KTD5) — the server's actual config,
// not an arbitrary client-side choice.
export const PASSWORD_MIN_LENGTH = 6;

/** Returns an error entry for each field whose value is empty or whitespace-only. */
export function validateRequired(fields: Record<string, string>): FieldErrors {
  const errors: FieldErrors = {};
  for (const [name, value] of Object.entries(fields)) {
    if (!value.trim()) {
      errors[name] = 'This field is required.';
    }
  }
  return errors;
}

/** Returns an inline error message when the password is shorter than the minimum, else undefined. */
export function validatePasswordLength(password: string): string | undefined {
  if (password.length < PASSWORD_MIN_LENGTH) {
    return `Password must be at least ${PASSWORD_MIN_LENGTH} characters.`;
  }
  return undefined;
}
