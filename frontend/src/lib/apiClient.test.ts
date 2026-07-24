import { describe, expect, it } from 'vitest';
import { apiClient } from './apiClient';

describe('apiClient', () => {
  it('has the API base URL configured', () => {
    expect(apiClient.defaults.baseURL).toBe('https://localhost:5001/api');
  });
});
