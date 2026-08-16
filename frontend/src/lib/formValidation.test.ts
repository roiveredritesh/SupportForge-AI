import { describe, expect, it } from 'vitest';
import { validateRequired, validatePasswordLength, PASSWORD_MIN_LENGTH } from './formValidation';

describe('validateRequired', () => {
  it('returns an error for an empty string', () => {
    expect(validateRequired({ name: '' })).toEqual({ name: 'This field is required.' });
  });

  it('returns an error for a whitespace-only string', () => {
    expect(validateRequired({ name: '   ' })).toEqual({ name: 'This field is required.' });
  });

  it('omits a key for a non-empty value', () => {
    expect(validateRequired({ name: 'Acme' })).toEqual({});
  });

  it('returns an error entry for each of multiple empty fields', () => {
    expect(validateRequired({ name: '', email: '', password: 'secret1' })).toEqual({
      name: 'This field is required.',
      email: 'This field is required.',
    });
  });
});

describe('validatePasswordLength', () => {
  it('returns an error message for a 5-character password', () => {
    expect(validatePasswordLength('abcde')).toBe(
      `Password must be at least ${PASSWORD_MIN_LENGTH} characters.`,
    );
  });

  it('returns no error for a 6-character password', () => {
    expect(validatePasswordLength('abcdef')).toBeUndefined();
  });

  it('returns no error for a 20-character password', () => {
    expect(validatePasswordLength('a'.repeat(20))).toBeUndefined();
  });
});
