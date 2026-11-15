import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import ServiceDeskApp from './ServiceDeskApp';

beforeEach(() => { location.hash = ''; });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

type Call = { url: string; method: string; body: string };
function mock(routes: Record<string, unknown>, calls: Call[]) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url: String(url), method: init?.method ?? 'GET', body: String(init?.body ?? '') });
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return new Response(JSON.stringify(key ? routes[key] : {}), { status: key === '/api/servicedesk/tickets' && init?.method === 'POST' ? 201 : 200 });
  }));
}
const me = { staff: true, canRaise: true, role: 'admin', id: 'u1' };

test('the incidents page lists only incidents and offers a major incident view', async () => {
  const calls: Call[] = [];
  mock({ '/api/servicedesk/me': me, '/api/servicedesk/summary': {}, '/api/servicedesk/tickets': { total: 1, items: [{ id: 7, number: 'TKT-000007', subject: 'Mail is down', kind: 'incident', major: true, priority: 'critical', status: 'open', sla: 'ok', updatedAt: new Date().toISOString(), company: 'Acme', requester: 'Ravi', assignee: '' }] } }, calls);
  location.hash = '#/servicedesk/major'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Major incidents' })).toBeTruthy());
  await waitFor(() => expect(screen.getByText('Mail is down')).toBeTruthy());
  expect(calls.some((c) => c.url.startsWith('/api/servicedesk/tickets?') && c.url.includes('&kind=incident') && c.url.includes('&major=1'))).toBe(true);
  expect(['Incidents', 'Major incidents', 'Service requests'].every((n) => screen.getAllByText(n).length > 0)).toBe(true);
});

test('a new incident takes its priority from impact and urgency until a priority is chosen', async () => {
  const calls: Call[] = [];
  mock({ '/api/servicedesk/me': me, '/api/servicedesk/settings': { categories: [] }, '/api/assets/companies': [], '/api/servicedesk/fields': [], '/api/servicedesk/tickets': { id: 9 } }, calls);
  location.hash = '#/servicedesk/new'; render(<ServiceDeskApp />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'New incident' })).toBeTruthy());
  fireEvent.change(document.querySelector('input[maxlength="200"]')!, { target: { value: 'Mail is down' } });
  fireEvent.change(screen.getByLabelText(/^Impact/), { target: { value: 'high' } });
  fireEvent.change(screen.getByLabelText(/^Urgency/), { target: { value: 'high' } });
  expect((screen.getByLabelText('Priority') as HTMLSelectElement).value).toBe('critical');
  fireEvent.click(screen.getByText('Open ticket'));
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url === '/api/servicedesk/tickets')).toBe(true));
  const body = JSON.parse(calls.find((c) => c.method === 'POST' && c.url === '/api/servicedesk/tickets')!.body);
  expect(body).toMatchObject({ subject: 'Mail is down', kind: 'incident', impact: 'high', urgency: 'high', priority: 'critical' });
});
