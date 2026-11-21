import { jsx as _jsx } from "react/jsx-runtime";
import { afterEach, expect, test, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import PersonInput from './PersonInput';
import DirectorySetup from './DirectorySetup';
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
const ok = (o) => new Response(JSON.stringify(o), { status: 200, headers: { 'content-type': 'application/json' } });
test('the name box offers the people of the customer\'s domain controller and still takes a typed name', async () => {
    const urls = [];
    vi.stubGlobal('fetch', vi.fn(async (url) => { urls.push(String(url)); return ok({ items: [{ companyId: 3, account: 'ana', name: 'Ana Lee', email: 'ana@acme.test', department: 'Sales' }] }); }));
    const seen = [];
    render(_jsx(PersonInput, { value: "an", onChange: (v) => seen.push(v), companyId: 3, label: "Who" }));
    await waitFor(() => expect(document.querySelector('datalist option')?.getAttribute('value')).toBe('Ana Lee'));
    expect(urls[0]).toContain('companyId=3');
    expect(urls[0]).toContain('search=an');
    fireEvent.change(screen.getByLabelText('Who'), { target: { value: 'Someone Else' } });
    expect(seen).toEqual(['Someone Else']);
});
test('the domain controller of a customer is saved, tested and read from one window', async () => {
    const calls = [];
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
        calls.push((init?.method ?? 'GET') + ' ' + url);
        if (String(url).endsWith('/sync'))
            return ok({ ok: true, count: 42 });
        return ok({ domain: { id: 1, baseDn: 'DC=acme,DC=test', servers: 'dc01.acme.test', tls: true, bindDn: 'svc@acme.test', controllerId: 'ctl1', lastOk: '2026-11-21T10:00:00Z', lastError: '', people: 40, syncedAt: '2026-11-21T10:00:00Z', syncError: '' }, controllers: [{ id: 'ctl1', name: 'HQ controller', online: true }] });
    }));
    render(_jsx(DirectorySetup, { company: { id: 3, name: 'Acme' }, onClose: () => { } }));
    await waitFor(() => expect(screen.getByText(/40 people/)).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'Sync now' }));
    await waitFor(() => expect(screen.getByText('42 people read.')).toBeTruthy());
    expect(calls).toContain('POST /api/directory/companies/3/sync');
});
