import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import List from './assets/List';
import AssetsApp from './assets/AssetsApp';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });

function mockApi(mine: Record<string, unknown>, role: string) {
  const routes: Record<string, unknown> = {
    '/api/platform/access/mine': { assets: mine }, '/api/platform/me': { role },
    '/api/assets/summary': { total: 0, byStatus: {}, byType: [] }, '/api/assets/types': [], '/api/assets/companies': [],
    '/api/assets?': { total: 1, items: [{ id: 1, tag: 'A-1', name: 'Laptop', status: 'in_use' }] },
  };
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{}', { status: 404 });
  }));
}

test('the Add asset button shows only to people who may add', async () => {
  mockApi({ read: 'all', add: 'none', modify: 'none', delete: 'none' }, 'viewer');
  render(<List />);
  await waitFor(() => expect(screen.getByText('Laptop')).toBeTruthy());
  expect(screen.queryByText('Add asset')).toBeNull();
  cleanup();
  mockApi({ read: 'all', add: 'all', modify: 'none', delete: 'none' }, 'viewer');
  render(<List />);
  await waitFor(() => expect(screen.getByText('Add asset')).toBeTruthy());
});

test('a customer administrator sees only the pages they may read', async () => {
  mockApi({ read: [3], add: 'none', modify: 'none', delete: 'none' }, 'cadmin');
  render(<AssetsApp />);
  await waitFor(() => expect(screen.queryByText('Discover')).toBeNull());
  expect(screen.getByText('Companies')).toBeTruthy(); expect(screen.queryByText('Software')).toBeNull(); expect(screen.queryByText('Settings')).toBeNull();
});
