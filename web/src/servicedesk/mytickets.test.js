import { jsx as _jsx } from "react/jsx-runtime";
import { afterEach, expect, test, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import MyTickets, { dueWords } from './MyTickets';
afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });
const hour = 3600000;
const now = Date.parse('2026-11-21T10:00:00Z');
test('due dates are said in plain words', () => {
    expect(dueWords({ status: 'open', resolveDue: new Date(now + 3 * hour).toISOString() }, now)).toBe('We aim to fix it in 3 hours');
    expect(dueWords({ status: 'open', resolveDue: new Date(now + 30 * 60000).toISOString() }, now)).toBe('We aim to fix it in less than an hour');
    expect(dueWords({ status: 'progress', resolveDue: new Date(now + 96 * hour).toISOString() }, now)).toBe('We aim to fix it in 4 days');
    expect(dueWords({ status: 'open', resolveDue: new Date(now - 5 * hour).toISOString() }, now)).toBe('Overdue by 5 hours');
    expect(dueWords({ status: 'hold', resolveDue: new Date(now + hour).toISOString() }, now)).toBe('Waiting for your reply');
    expect(dueWords({ status: 'open' }, now)).toBe('');
});
function mockApi() {
    const calls = [];
    const t = (id, status) => ({ id, number: 'INC-000' + id, subject: 'Subject ' + id, status, priority: 'medium', sla: 'ok', updatedAt: new Date().toISOString(), resolveDue: new Date(Date.now() + 5 * hour).toISOString() });
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
        calls.push({ url: String(url), method: init?.method });
        if (String(url).startsWith('/api/servicedesk/summary'))
            return new Response(JSON.stringify({ open: 2, progress: 1, hold: 1, resolved: 3, closed: 0 }), { status: 200 });
        if (String(url).startsWith('/api/servicedesk/tickets?')) {
            const st = new URL(String(url), 'http://x').searchParams.get('status');
            return new Response(JSON.stringify({ total: 1, items: [st === 'hold' ? t(2, 'hold') : st === 'resolved,closed' ? t(3, 'resolved') : t(1, 'open')] }), { status: 200 });
        }
        return new Response('{}', { status: 200 });
    }));
    return calls;
}
test('My tickets groups tickets, flags the ones waiting for the employee and offers Reopen on finished ones', async () => {
    const calls = mockApi();
    render(_jsx(MyTickets, { me: { canRaise: true } }));
    await waitFor(() => expect(screen.getByText('Subject 1')).toBeTruthy());
    expect(screen.getByText('We aim to fix it in 5 hours')).toBeTruthy();
    fireEvent.click(screen.getByRole('link', { name: /Waiting for you/ }));
    await waitFor(() => expect(screen.getByText('Subject 2')).toBeTruthy());
    expect(screen.getByText('Waiting for your reply')).toBeTruthy();
    fireEvent.click(screen.getByRole('link', { name: /Finished/ }));
    await waitFor(() => expect(screen.getByText('Subject 3')).toBeTruthy());
    fireEvent.click(screen.getByText('Reopen'));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/servicedesk/tickets/3/status' && c.method === 'POST')).toBe(true));
});
