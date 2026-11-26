import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import { PatchPanel, PrintPanel, ProcessPanel } from './Maintenance';
import Patching from './Patching';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const stored = { hasAgent: true, canControl: true, os: 'windows', lastPatch: '2026-11-01', checkedAt: new Date().toISOString(),
  check: { pending: 2, rebootRequired: false, items: [
    { title: 'Security Update for .NET', kb: 'KB5034441', severity: 'Critical', category: 'Security Updates', sizeMb: 42.5, reboot: true },
    { title: 'Definition update', kb: 'KB2267602', severity: '', category: 'Definition Updates', sizeMb: 1.2, reboot: false }] } };

test('updates are listed from the last check, and installing the ticked ones asks for a reason first', async () => {
  const calls: { url: string; body?: string }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, body: init?.body as string });
    if (url.endsWith('/patches')) return new Response(JSON.stringify(stored), { status: 200 });
    if (url.endsWith('/patches/install')) return new Response(JSON.stringify({ id: 'j9' }), { status: 200 });
    return new Response(JSON.stringify({ status: 'done', output: 'Installed: Security Update for .NET', data: { installed: 1, failed: 0, rebootRequired: false } }), { status: 200 });
  }));
  render(<PatchPanel assetId="7" />);
  await waitFor(() => expect(screen.getByText('Security Update for .NET')).toBeTruthy());
  expect(screen.getByText('2 waiting')).toBeTruthy(); expect(screen.getByText('Critical')).toBeTruthy();
  fireEvent.click(screen.getByLabelText('Select Security Update for .NET'));
  fireEvent.click(screen.getByRole('button', { name: 'Install 1 selected' }));
  const go = screen.getByRole('button', { name: 'Install' }) as HTMLButtonElement;
  expect(go.disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'CHG-77' } });
  expect(go.disabled).toBe(false);
  fireEvent.click(go);
  await waitFor(() => expect(calls.some((c) => c.url.endsWith('/patches/install'))).toBe(true));
  expect(JSON.parse(calls.find((c) => c.url.endsWith('/patches/install'))!.body!)).toEqual({ reason: 'CHG-77', restart: false, items: ['KB5034441'] });
});

test('a computer without the agent is told what to do', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ hasAgent: false }), { status: 200 })));
  render(<PatchPanel assetId="8" />);
  await waitFor(() => expect(screen.getByText(/Install the agent on this computer first/)).toBeTruthy());
});

test('the busiest processes show by processor and by memory', async () => {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    if (url.endsWith('/processes')) return new Response(JSON.stringify({ id: 'p1' }), { status: 200 });
    return new Response(JSON.stringify({ status: 'done', output: '', data: { cores: 4, cpu: [{ name: 'sqlservr', id: 1200, cpu: 61.5, memMb: 900 }], mem: [{ name: 'chrome', id: 88, cpu: 2, memMb: 1500 }] } }), { status: 200 });
  }));
  render(<ProcessPanel assetId="7" />);
  fireEvent.click(screen.getByRole('button', { name: 'Read now' }));
  await waitFor(() => expect(screen.getByText('sqlservr')).toBeTruthy());
  expect(screen.getByText('61.5%')).toBeTruthy(); expect(screen.getByText('1500 MB')).toBeTruthy();
});

test('print queues are read, and clearing one needs a reason', async () => {
  const calls: { url: string; body?: string }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, body: init?.body as string });
    if (url.endsWith('/printserver')) return new Response(JSON.stringify({ id: 'q1' }), { status: 200 });
    return new Response(JSON.stringify({ status: 'done', output: '1 queue(s)', data: { spooler: 'Running', ports: [{ name: 'IP_10.0.0.9', host: '10.0.0.9', port: 9100 }], drivers: [{ name: 'HP Universal', manufacturer: 'HP' }],
      queues: [{ name: 'Front desk', driver: 'HP Universal', port: 'IP_10.0.0.9', shared: true, shareName: 'FrontDesk', status: 'Normal', jobs: 3 }] } }), { status: 200 });
  }));
  render(<PrintPanel assetId="7" can />);
  fireEvent.click(screen.getByRole('button', { name: 'Read print queues' }));
  await waitFor(() => expect(screen.getByText('Front desk')).toBeTruthy());
  fireEvent.click(screen.getByRole('button', { name: 'Clear queue' }));
  const go = screen.getByRole('button', { name: 'Go ahead' }) as HTMLButtonElement;
  expect(go.disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'stuck job' } });
  fireEvent.click(go);
  await waitFor(() => expect(calls.filter((c) => c.url.endsWith('/printserver')).length).toBeGreaterThanOrEqual(2));
  expect(JSON.parse(calls.filter((c) => c.url.endsWith('/printserver'))[1].body!)).toEqual({ action: 'clear', printer: 'Front desk', reason: 'stuck job' });
});

test('the patching page counts what is waiting', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ items: [
    { id: 1, tag: 'AST-000001', name: 'FILESRV', os: 'windows', osName: 'Windows Server 2022', company: 'Acme', lastPatch: '2026-10-30', pending: 4, rebootRequired: true, checkedAt: new Date().toISOString() },
    { id: 2, tag: 'AST-000002', name: 'WS-014', os: 'windows', osName: 'Windows 11', company: 'Acme', lastPatch: null, pending: null, rebootRequired: null, checkedAt: null }] }), { status: 200 })));
  render(<Patching />);
  await waitFor(() => expect(screen.getByText('FILESRV')).toBeTruthy());
  expect(screen.getByText('1 with updates waiting')).toBeTruthy(); expect(screen.getByText('1 need a restart')).toBeTruthy(); expect(screen.getByText('1 not checked yet')).toBeTruthy();
});
