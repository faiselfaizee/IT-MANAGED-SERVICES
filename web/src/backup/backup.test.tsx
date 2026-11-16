import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import BackupApp from './BackupApp';

const sum = (company: string, companyId: number | null, x: Record<string, unknown> = {}) => ({ company, companyId, jobs: 0, devices: 0, devicesWithTools: 0, success: 0, warning: 0, failed: 0, missed: 0,
  waiting: 0, ignored: 0, ageDay: 0, ageWeek: 0, ageOlder: 0, ageNever: 0, byTool: {}, alerts: 0, ...x });

type Call = { url: string; method: string; body: string };
function mock(routes: Record<string, unknown>, calls: Call[] = []) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url: String(url), method: init?.method ?? 'GET', body: String(init?.body ?? '') });
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{}', { status: 200 });
  }));
  return calls;
}
beforeEach(() => { location.hash = ''; });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const acme = sum('Acme Ltd', 1, { jobs: 5, devices: 2, success: 2, warning: 1, failed: 1, missed: 1, ageDay: 3, ageOlder: 1, ageNever: 1, byTool: { veeam: 3, wsb: 2 }, alerts: 2 });
const dash = { all: sum('All customers', null, { jobs: 7, success: 4, warning: 1, failed: 1, missed: 1 }), tiles: [acme, sum('Globex', 2, { jobs: 2, success: 2, ageDay: 2 })], restricted: false };

test('the backup home has a tile per customer and a customer has its rings', async () => {
  mock({ '/api/backup/dashboard': dash, '/api/platform/me': { role: 'admin' } });
  location.hash = '#/backup'; render(<BackupApp />);
  await waitFor(() => expect(screen.getByText('Acme Ltd')).toBeTruthy());
  expect([...document.querySelectorAll('a.co-tile')].map((a) => a.getAttribute('href'))).toEqual(['#/backup/customer/all', '#/backup/customer/1', '#/backup/customer/2']);
  cleanup(); location.hash = '#/backup/customer/1'; render(<BackupApp />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Acme Ltd' })).toBeTruthy());
  expect(screen.getAllByText('Failed')[0].closest('a')?.getAttribute('href')).toBe('#/backup/jobs?company=1&status=failed');
  expect(screen.getByText('Over a week ago').closest('a')?.getAttribute('href')).toBe('#/backup/jobs?company=1&age=older');
  expect(screen.getByText('Veeam').closest('a')?.getAttribute('href')).toBe('#/backup/jobs?company=1&tool=veeam');
});

test('the job list sends the ring filter, and a job can be changed', async () => {
  const job = { id: 9, companyId: 1, company: 'Acme Ltd', assetId: 4, assetName: 'SRV-FS1', tool: 'veeam', toolName: 'Veeam', name: 'Nightly', source: 'agent', everyHours: 24, watched: true, notes: '',
    lastResult: 'failed', lastAt: new Date().toISOString(), lastSuccessAt: null, lastMessage: 'No space', fails: 2, alert: 'failed', ticketNumber: 'INC-0042', status: 'failed' };
  const calls = mock({ '/api/backup/jobs/9': { job, runs: [{ id: 1, started: null, ended: job.lastAt, minutes: null, result: 'failed', message: 'No space', bytes: 0 }], month: { success: 3, warning: 0, failed: 2 }, graceHours: 6 },
    '/api/backup/jobs': { total: 1, items: [job] }, '/api/platform/me': { role: 'engineer' } });
  location.hash = '#/backup/jobs?company=1&status=failed'; render(<BackupApp />);
  await waitFor(() => expect(screen.getByText('Nightly')).toBeTruthy());
  expect(calls.some((c) => c.url.startsWith('/api/backup/jobs?') && c.url.includes('&company=1') && c.url.includes('&status=failed'))).toBe(true);
  expect(screen.getByText('INC-0042')).toBeTruthy();
  cleanup(); location.hash = '#/backup/job/9'; render(<BackupApp />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Nightly' })).toBeTruthy());
  expect(screen.getByText(/the last 2 runs failed/)).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Change' }));
  fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '168' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save' }));
  await waitFor(() => expect(calls.some((c) => c.method === 'PUT' && c.url === '/api/backup/jobs/9' && JSON.parse(c.body).everyHours === 168)).toBe(true));
});

test('with no results yet the home explains how to connect', async () => {
  mock({ '/api/backup/dashboard': { all: sum('All customers', null, { devicesWithTools: 3 }), tiles: [], restricted: false }, '/api/platform/me': { role: 'viewer' } });
  location.hash = '#/backup'; render(<BackupApp />);
  await waitFor(() => expect(screen.getByText('No backup results yet')).toBeTruthy());
  expect(screen.getByText(/found backup software on 3 devices/)).toBeTruthy();
  expect(screen.queryByText('Alerts')).toBeNull();   // set-up pages are for administrators
});
