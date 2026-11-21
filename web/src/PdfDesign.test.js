import { jsx as _jsx } from "react/jsx-runtime";
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import PdfDesign, { PDF_STANDARD } from './PdfDesign';
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
test('shows the saved design, rejects a bad colour before sending, saves what was typed', async () => {
    const calls = [];
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
        calls.push({ url, method: init?.method, body: init?.body });
        return new Response(JSON.stringify({ ...PDF_STANDARD, headerColor: '#112233' }), { status: 200 });
    }));
    render(_jsx(PdfDesign, { url: "/api/x", title: "Design", intro: "Intro", withNote: true }));
    await waitFor(() => expect(screen.getByLabelText('Table header colour').value).toBe('#112233'));
    fireEvent.change(screen.getByLabelText('Table header colour'), { target: { value: 'teal' } });
    expect(screen.getByText('Save design').disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Table header colour'), { target: { value: '#445566' } });
    fireEvent.change(screen.getByLabelText(/Line at the foot of every page/), { target: { value: 'DamazeTech' } });
    fireEvent.click(screen.getByText('Save design'));
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true));
    const put = calls.find((c) => c.method === 'PUT');
    expect(JSON.parse(put.body)).toMatchObject({ headerColor: '#445566', footer: 'DamazeTech', headerText: '#FFFFFF' });
});
test('the standard design button restores the logo teal; the note field only shows for invoices', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ ...PDF_STANDARD, headerColor: '#abcdef' }), { status: 200 })));
    const { unmount } = render(_jsx(PdfDesign, { url: "/api/x", title: "Design", intro: "Intro" }));
    await waitFor(() => expect(screen.getByLabelText('Table header colour').value).toBe('#abcdef'));
    expect(screen.queryByLabelText(/Closing note of the invoice/)).toBeNull();
    fireEvent.click(screen.getByText('Use the standard design'));
    expect(screen.getByLabelText('Table header colour').value).toBe('#207178');
    unmount();
});
