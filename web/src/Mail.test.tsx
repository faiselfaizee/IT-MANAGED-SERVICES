import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Mail from './Mail';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
const cfg = { provider: 'smtp', from: 'desk@acme.test', fromName: 'IT', host: 'smtp.acme.test', port: 587, security: 'starttls', user: 'u', hasPassword: true, tenantId: '', clientId: '', hasSecret: false, on: true };

function mock(extra: Record<string, () => Response> = {}, calls: [string, string, string][] = []) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    calls.push([o?.method ?? 'GET', String(url), String(o?.body ?? '')]);
    const k = Object.keys(extra).find((x) => String(url).startsWith(x)); if (k) return extra[k]();
    if (String(url).startsWith('/api/platform/mail/config')) return new Response(JSON.stringify(cfg), { status: 200 });
    if (String(url).startsWith('/api/platform/mail/outbox')) return new Response(JSON.stringify({ items: [{ id: 1, to: 'pat@acme.test', subject: '[TKT-1] Hi', status: 'failed', attempts: 5, error: 'Connection refused', createdAt: new Date().toISOString(), sentAt: null }] }), { status: 200 });
    return new Response('{}', { status: 200 });
  }));
}

test('shows the saved settings without the password and lists failed messages with a retry', async () => {
  mock(); render(<Mail />);
  await waitFor(() => expect(screen.getByDisplayValue('smtp.acme.test')).toBeTruthy());
  expect(screen.getByText(/Password \(saved; type to replace\)/)).toBeTruthy();
  expect(screen.getByText('Connection refused')).toBeTruthy(); expect(screen.getByText('Try again')).toBeTruthy();
});

test('saving leaves the password out when it was not retyped', async () => {
  const calls: [string, string, string][] = []; mock({}, calls); render(<Mail />);
  await waitFor(() => expect(screen.getByDisplayValue('smtp.acme.test')).toBeTruthy());
  fireEvent.click(screen.getByText('Save settings'));
  await waitFor(() => expect(calls.some((c) => c[0] === 'PUT')).toBe(true));
  expect(calls.find((c) => c[0] === 'PUT')![2]).not.toContain('password');
});

test('a failed test shows why', async () => {
  mock({ '/api/platform/mail/test': () => new Response(JSON.stringify({ error: 'send_failed', detail: 'No connection could be made' }), { status: 400 }) }); render(<Mail />);
  await waitFor(() => expect(screen.getByDisplayValue('smtp.acme.test')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Send to'), { target: { value: 'me@acme.test' } });
  fireEvent.click(screen.getByText('Send test'));
  await waitFor(() => expect(screen.getByText('No connection could be made')).toBeTruthy());
});
