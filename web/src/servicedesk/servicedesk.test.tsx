import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import ServiceDeskApp from './ServiceDeskApp';

const ticket = (over: Record<string, unknown> = {}) => ({
  id: 5, number: 'TKT-000005', subject: 'Printer down', description: 'Third floor', status: 'open', priority: 'high', category: 'Hardware', companyId: 1, company: 'Acme',
  requesterId: null, requester: 'Pat', requesterEmail: 'pat@acme.test', assigneeId: null, assignee: '', assetId: null, asset: '', source: 'web',
  createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), firstResponseAt: null, resolvedAt: null, closedAt: null,
  respondDue: new Date(Date.now() + 3600e3).toISOString(), resolveDue: new Date(Date.now() + 7200e3).toISOString(), paused: false, sla: 'warning', respond: 'ok', ...over,
});
const staffMe = { id: 'u', name: 'Eng', role: 'engineer', staff: true, customer: false, canRaise: true, canModify: true, readOnly: false };
const custMe = { id: 'c', name: 'Cat', role: 'customer', staff: false, customer: true, companyId: 1, company: 'Acme', canRaise: true, canModify: true, readOnly: false };

function mockApi(routes: Record<string, unknown>) {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{}', { status: 404 });
  }));
}
beforeEach(() => { location.hash = ''; });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('list shows tickets with their service level and the staff-only filters', async () => {
  mockApi({
    '/api/servicedesk/me': staffMe, '/api/servicedesk/summary': { open: 3, progress: 1, hold: 2, unassigned: 4, mine: 1, overdue: 1 },
    '/api/servicedesk/tickets?': { total: 1, items: [ticket()] },
  });
  location.hash = '#/servicedesk'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText('Printer down')).toBeTruthy());
  expect(screen.getByText('Due soon')).toBeTruthy(); expect(screen.getAllByText('High').length).toBeGreaterThan(0);
  expect(screen.getByLabelText('Assigned to')).toBeTruthy();
  expect(screen.getByText('Unassigned', { selector: 'dt' })).toBeTruthy();
});

test('a customer does not get staff filters or the internal-note switch', async () => {
  mockApi({
    '/api/servicedesk/me': custMe, '/api/servicedesk/summary': { open: 1, progress: 0, hold: 0, overdue: 0 },
    '/api/servicedesk/tickets?': { total: 1, items: [ticket()] },
  });
  location.hash = '#/servicedesk'; const { unmount } = render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText('Printer down')).toBeTruthy());
  expect(screen.queryByLabelText('Assigned to')).toBeNull();
  unmount();
  mockApi({
    '/api/servicedesk/me': custMe,
    '/api/servicedesk/tickets/5': { ticket: ticket(), comments: [{ id: 1, kind: 'public', author: 'Eng', body: 'On it', at: new Date().toISOString() }], attachments: [], can: { edit: false, comment: true, internalNote: false, status: true } },
  });
  location.hash = '#/servicedesk/5'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText('On it')).toBeTruthy());
  expect(screen.getByLabelText('Reply')).toBeTruthy();
  expect(screen.queryByText(/Internal note \(/)).toBeNull();
  expect(screen.getByText('Close ticket')).toBeTruthy();
});

test('staff see internal notes marked and can assign', async () => {
  mockApi({
    '/api/servicedesk/me': staffMe, '/api/servicedesk/staff': [{ id: 's1', name: 'Sam' }], '/api/servicedesk/settings': { categories: ['Hardware'], sla: {} },
    '/api/servicedesk/tickets/5': { ticket: ticket(), comments: [{ id: 2, kind: 'internal', author: 'Eng', body: 'Check the VPN', at: new Date().toISOString() }], attachments: [{ id: 9, name: 'shot.png', size: 2048, by: 'Pat', internal: false }], can: { edit: true, comment: true, internalNote: true, status: true } },
  });
  location.hash = '#/servicedesk/5'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText('Check the VPN')).toBeTruthy());
  expect(screen.getByText('Internal note')).toBeTruthy();
  expect(screen.getByText('shot.png')).toBeTruthy();
  await waitFor(() => expect(screen.getByText('Sam')).toBeTruthy());
  expect(screen.getByLabelText(/Internal note \(/)).toBeTruthy();
});

test('a customer without a company is told to ask the administrator', async () => {
  mockApi({ '/api/servicedesk/me': { ...custMe, companyId: null, company: null, canRaise: false }, '/api/servicedesk/settings': { categories: [], sla: {} } });
  location.hash = '#/servicedesk/new'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByText(/not linked to a company/)).toBeTruthy());
});

test('settings page shows the service levels', async () => {
  mockApi({
    '/api/servicedesk/me': { ...staffMe, role: 'admin' },
    '/api/servicedesk/settings': { categories: ['Hardware', 'Other'], sla: { respond: { critical: 1, high: 2, medium: 4, low: 8 }, resolve: { critical: 4, high: 8, medium: 24, low: 72 }, warnPct: 75, pause: true, escalate: true } },
  });
  location.hash = '#/servicedesk/settings'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByLabelText('Critical resolution')).toBeTruthy());
  expect((screen.getByLabelText('Critical resolution') as HTMLInputElement).value).toBe('4');
  fireEvent.change(screen.getByLabelText('Critical resolution'), { target: { value: '6' } });
  expect((screen.getByLabelText('Critical resolution') as HTMLInputElement).value).toBe('6');
});
