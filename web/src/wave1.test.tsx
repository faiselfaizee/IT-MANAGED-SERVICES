import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import { Relations } from './assets/Relations';
import Packages from './assets/Packages';
import { fillCanned } from './servicedesk/Detail';
import { Settings } from './servicedesk/Admin';
import MonitoringSettings from './monitoring/Settings';
import { PageSize } from './ui';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); localStorage.clear(); });
type Call = [string, string, unknown];
function api(routes: Record<string, unknown>, calls: Call[] = []) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    calls.push([o?.method ?? 'GET', String(url), o?.body]);
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{"ok":true}', { status: 200 });
  }));
  return calls;
}

test('a canned reply gets the requester, the agent and the ticket number filled in', () => {
  const t = { requester: 'Priya Nair', assignee: 'Arun', number: 'INC-0042' };
  expect(fillCanned('Hi {requester}, {agent} here about {number}.', t)).toBe('Hi Priya, Arun here about INC-0042.');
  expect(fillCanned('Hi {Requester}, {AGENT}', { requester: '', assignee: '', number: 'X' })).toBe('Hi there, the support team');
});

test('relationships list both directions and what fails with the asset', async () => {
  const calls = api({
    '/api/assets/9/relations': { outgoing: [{ id: 1, kind: 'runs_on', assetId: 3, name: 'esx-01', tag: 'AST-3', company: 'Acme', note: '' }], incoming: [{ id: 2, kind: 'depends_on', assetId: 12, name: 'erp-app', tag: 'AST-12', company: 'Acme', note: '' }] },
    '/api/assets/9/impact': { items: [{ id: 12, name: 'erp-app', tag: 'AST-12', company: 'Acme', depth: 1, kind: 'depends_on', openTickets: 2 }] },
  });
  render(<Relations id="9" canEdit />);
  expect(await screen.findByText('runs on')).toBeTruthy();
  expect(screen.getByText('esx-01')).toBeTruthy();
  expect(screen.getByText(/is needed by this asset/)).toBeTruthy();
  expect(await screen.findByText('1 asset would be affected.')).toBeTruthy();
  expect(screen.getByText('Directly')).toBeTruthy();
  expect(calls.some(([m, u]) => m === 'GET' && u === '/api/assets/9/impact')).toBe(true);
});

test('the repository marks required and prohibited programs and lists the computers that break a rule', async () => {
  api({
    '/api/assets/packages/violations': { items: [{ packageId: 1, program: 'Wazuh agent', rule: 'required', assetId: 5, tag: 'AST-5', asset: 'PC-5', company: 'Acme', version: '' }, { packageId: 2, program: 'uTorrent', rule: 'prohibited', assetId: 6, tag: 'AST-6', asset: 'PC-6', company: 'Acme', version: '3.6' }] },
    '/api/assets/packages': { items: [{ id: 1, name: 'Wazuh agent', version: '4.8', publisher: '', os: 'windows', kind: 'winget', source: 'Wazuh.Agent', fileName: '', size: 0, sha256: '', args: '', notes: '', enabled: true, installs: 0, rule: 'required', matchName: '' }] },
  });
  render(<Packages />);
  expect(await screen.findByText('Computers that break a software rule')).toBeTruthy();
  expect(screen.getByText('Missing Wazuh agent')).toBeTruthy();
  expect(screen.getByText('Has uTorrent 3.6')).toBeTruthy();
  expect(screen.getByText('Required')).toBeTruthy();
});

test('working hours and canned replies are on the Service Desk settings', async () => {
  const calls = api({
    '/api/servicedesk/settings': { categories: ['Hardware'], sla: { respond: { critical: 1, high: 2, medium: 4, low: 8 }, resolve: { critical: 4, high: 8, medium: 24, low: 72 }, warnPct: 75, pause: true, escalate: true, hours: { enabled: false, start: '09:00', end: '18:00', days: [1, 2, 3, 4, 5], zone: 'UTC' } } },
    '/api/servicedesk/canned': [{ id: 1, title: 'Restart first', body: 'Please restart.', active: true }],
    '/api/servicedesk/fields': { items: [] },
  });
  render(<Settings />);
  fireEvent.click(await screen.findByLabelText('Count working hours only'));
  fireEvent.change(await screen.findByLabelText('Time zone'), { target: { value: 'Asia/Kolkata' } });
  expect(await screen.findByText('Restart first')).toBeTruthy();
  fireEvent.click(screen.getByText('Save settings'));
  await waitFor(() => expect(calls.some(([m, u]) => m === 'PUT' && u === '/api/servicedesk/settings')).toBe(true));
  const body = JSON.parse(String(calls.find(([m, u]) => m === 'PUT' && u === '/api/servicedesk/settings')![2]));
  expect(body.sla.hours).toMatchObject({ enabled: true, zone: 'Asia/Kolkata', days: [1, 2, 3, 4, 5] });
});

test('an alert route is added and sent with the monitoring settings', async () => {
  const calls = api({ '/api/monitoring/settings': { recipients: 'noc@x.test', mailResolved: true, companies: [], routes: [] }, '/api/assets/companies': [{ id: 7, name: 'Acme' }] });
  render(<MonitoringSettings />);
  fireEvent.click(await screen.findByText('Add a route'));
  fireEvent.change(screen.getByLabelText('Route name'), { target: { value: 'Network team' } });
  fireEvent.change(screen.getByLabelText('Kind of device'), { target: { value: 'network' } });
  fireEvent.change(screen.getByLabelText('Severity'), { target: { value: 'critical' } });
  fireEvent.change(screen.getByLabelText('Route addresses'), { target: { value: 'net@x.test' } });
  fireEvent.click(screen.getByText('Save settings'));
  await waitFor(() => expect(calls.some(([m, u]) => m === 'PUT' && u === '/api/monitoring/settings')).toBe(true));
  const body = JSON.parse(String(calls.find(([m, u]) => m === 'PUT' && u === '/api/monitoring/settings')![2]));
  expect(body.routes).toEqual([{ name: 'Network team', kinds: ['network'], companyId: null, severity: 'critical', to: 'net@x.test' }]);
});

test('rows per page offers the usual sizes and reports the choice', () => {
  const seen: number[] = [];
  render(<PageSize value={50} onChange={(n) => seen.push(n)} />);
  const sel = screen.getByLabelText('Rows per page') as HTMLSelectElement;
  expect(Array.from(sel.options).map((o) => o.value)).toEqual(['10', '25', '50', '100', '200']);
  fireEvent.change(sel, { target: { value: '100' } });
  expect(seen).toEqual([100]);
});
