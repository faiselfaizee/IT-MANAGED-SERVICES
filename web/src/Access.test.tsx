import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Access from './Access';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mockApi(routes: Record<string, unknown>) {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{}', { status: 404 });
  }));
}

test('groups show their people and nested groups', async () => {
  mockApi({
    '/api/platform/access/groups': [{ id: 'g1', name: 'Asset editors', description: 'Can edit', members: [{ kind: 'user', id: 'u1', name: 'Sam' }, { kind: 'group', id: 'g2', name: 'Helpdesk' }] }],
    '/api/platform/users': [],
  });
  render(<Access />);
  await waitFor(() => expect(screen.getByText('Asset editors')).toBeTruthy());
  expect(screen.getByText('Sam')).toBeTruthy(); expect(screen.getByText('Helpdesk')).toBeTruthy();
  expect(screen.getByText('Group')).toBeTruthy(); expect(screen.getByText('Person')).toBeTruthy();
});

test('permissions read as who, what and for which company', async () => {
  mockApi({
    '/api/platform/access/grants': [{ id: 1, kind: 'group', principalId: 'g1', principal: 'Asset editors', module: 'assets', action: 'modify', companyId: null }, { id: 2, kind: 'user', principalId: 'u', principal: 'Cat', module: 'assets', action: 'read', companyId: 3 }],
    '/api/platform/access/groups': [], '/api/platform/users': [], '/api/assets/companies': [{ id: 3, name: 'Acme' }],
  });
  render(<Access />);
  screen.getByText('Permissions').click();
  await waitFor(() => expect(screen.getByText('Asset editors')).toBeTruthy());
  expect(screen.getByText('Change')).toBeTruthy(); expect(screen.getByText('Every company')).toBeTruthy();
  await waitFor(() => expect(screen.getByText('Acme')).toBeTruthy());
});
