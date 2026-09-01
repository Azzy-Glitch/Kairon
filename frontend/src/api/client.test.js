import { describe, expect, it } from 'vitest';
import client from './client';

describe('API client production default', () => {
  it('uses the same-origin API path when no explicit backend URL is configured', () => {
    expect(client.defaults.baseURL).toBe('/api');
  });
});
