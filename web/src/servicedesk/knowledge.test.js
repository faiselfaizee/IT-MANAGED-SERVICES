import { jsx as _jsx } from "react/jsx-runtime";
import { afterEach, expect, test, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { KbList, Suggestions } from './Knowledge';
afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });
function mockApi(articles) {
    const urls = [];
    vi.stubGlobal('fetch', vi.fn(async (url) => { urls.push(String(url)); return new Response(JSON.stringify(String(url).startsWith('/api/servicedesk/kb') ? articles : {}), { status: 200 }); }));
    return urls;
}
const wifi = { id: 1, title: 'Reset your Wi-Fi password', snippet: 'Open Settings', visibility: 'everyone', published: true };
const draft = { id: 2, title: 'Printer drivers', snippet: 'Not ready', visibility: 'staff', published: false };
test('people see the articles and can search; only staff get New article and see drafts', async () => {
    const urls = mockApi([wifi]);
    render(_jsx(KbList, { staff: false }));
    await waitFor(() => expect(screen.getByText('Reset your Wi-Fi password')).toBeTruthy());
    expect(screen.queryByText('New article')).toBeNull();
    fireEvent.change(screen.getByLabelText('Search articles'), { target: { value: 'wifi' } });
    await waitFor(() => expect(urls.some((u) => u.includes('q=wifi'))).toBe(true));
    cleanup();
    mockApi([wifi, draft]);
    render(_jsx(KbList, { staff: true }));
    await waitFor(() => expect(screen.getByText('Draft')).toBeTruthy());
    expect(screen.getByText('New article')).toBeTruthy();
});
test('an empty search explains what to do next', async () => {
    mockApi([]);
    render(_jsx(KbList, { staff: false }));
    await waitFor(() => expect(screen.getByText('No articles yet.')).toBeTruthy());
});
test('suggestions wait for a real subject, then list matching published articles', async () => {
    const urls = mockApi([wifi, draft]);
    const { rerender } = render(_jsx(Suggestions, { subject: "wi" }));
    await new Promise((r) => setTimeout(r, 500));
    expect(urls.length).toBe(0);
    rerender(_jsx(Suggestions, { subject: "wifi password" }));
    await waitFor(() => expect(screen.getByText('Reset your Wi-Fi password')).toBeTruthy());
    expect(screen.queryByText('Printer drivers')).toBeNull();
    expect(urls[0]).toContain('limit=4');
});
