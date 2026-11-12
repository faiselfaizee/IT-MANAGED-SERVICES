import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import ServiceDeskApp from './ServiceDeskApp';

const staffMe = { id: 'u1', name: 'Eng', role: 'engineer', staff: true, customer: false, canRaise: true, canModify: true, readOnly: false };
const custMe = { id: 'c', name: 'Cat', role: 'customer', staff: false, customer: true, companyId: 1, canRaise: true, canModify: true, readOnly: false };
const change = (over: Record<string, unknown> = {}) => ({ id: 7, number: 'CHG-000007', title: 'Patch the file server', description: '', type: 'normal', status: 'review', risk: 'medium', impact: 'low', reason: 'Security fix', plan: 'Install at night', rollback: 'Restore snapshot', outcome: '', companyId: null, companyName: 'Acme', ownerId: 'u2', ownerName: 'Other', startAt: null, endAt: null, quorum: 2, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), ...over });

function mockApi(routes: Record<string, unknown>) {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{}', { status: 404 });
  }));
}
beforeEach(() => { location.hash = ''; });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('staff see the Changes and Problems pages and the approval buttons', async () => {
  mockApi({ '/api/servicedesk/me': staffMe, '/api/servicedesk/changes/7': { change: change(), votes: [{ userId: 'u3', name: 'Ann', decision: 'approve', comment: '', at: new Date().toISOString() }], notes: [], tickets: [], problems: [] } });
  location.hash = '#/servicedesk/changes/7'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText(/CHG-000007/)).toBeTruthy());
  expect(screen.getByText('Approve')).toBeTruthy(); expect(screen.getByText('Reject')).toBeTruthy();
  expect(screen.getByText('1 of 2 needed')).toBeTruthy();
  expect(screen.getByText('Changes', { selector: 'a' })).toBeTruthy(); expect(screen.getByText('Problems', { selector: 'a' })).toBeTruthy();
});

test('the owner of a change cannot approve it', async () => {
  mockApi({ '/api/servicedesk/me': staffMe, '/api/servicedesk/changes/7': { change: change({ ownerId: 'u1' }), votes: [], notes: [], tickets: [], problems: [] } });
  location.hash = '#/servicedesk/changes/7'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText(/CHG-000007/)).toBeTruthy());
  expect(screen.queryByText('Approve')).toBeNull();
});

test('customers do not get the Changes or Problems pages', async () => {
  mockApi({ '/api/servicedesk/me': custMe, '/api/servicedesk/summary': {}, '/api/servicedesk/tickets?': { total: 0, items: [] } });
  location.hash = '#/servicedesk/changes'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText('Tickets', { selector: 'h1' })).toBeTruthy());
  expect(screen.queryByText('Changes', { selector: 'a' })).toBeNull();
});

test('the problem list offers to raise a problem from repeated tickets', async () => {
  mockApi({ '/api/servicedesk/me': staffMe, '/api/servicedesk/problems/suggestions': { items: [{ category: 'Network', count: 4, tickets: [1, 2, 3, 4] }] }, '/api/servicedesk/problems?': { items: [] } });
  location.hash = '#/servicedesk/problems'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText('Raise a problem')).toBeTruthy());
  expect(screen.getByText('Network', { selector: 'strong' })).toBeTruthy();
});

test('a missing licence names the module that is missing', async () => {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => String(url).startsWith('/api/servicedesk/me')
    ? new Response(JSON.stringify({ error: 'module_not_licensed', module: 'servicedesk' }), { status: 402 }) : new Response('{}', { status: 404 })));
  location.hash = '#/servicedesk'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getAllByText(/Service Desk is not part of this licence/).length).toBeGreaterThan(0));
});
