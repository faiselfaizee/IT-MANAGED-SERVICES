import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import Assistant, { tableCsv } from './Assistant';

const api = vi.fn();
vi.mock('./http', async (orig) => ({ ...(await orig<typeof import('./http')>()), api: (...a: unknown[]) => api(...a) }));

beforeEach(() => { api.mockReset(); });
afterEach(cleanup);

describe('Ask DeskAsk', () => {
  it('offers examples, asks the server and shows a linked table', async () => {
    api.mockResolvedValue({ text: 'Overdue tickets: 1.', count: 1, suggestions: ['Tickets with no engineer'], tables: [{ title: 'Overdue tickets', headers: ['Number', 'Subject'], rows: [['TKT-000007', 'Printer jam']], links: ['#/servicedesk/7'], more: false }] });
    render(<Assistant />);
    expect(screen.getByText('Show overdue tickets')).toBeTruthy();
    fireEvent.click(screen.getByText('Show overdue tickets'));
    await waitFor(() => expect(screen.getByText('Printer jam')).toBeTruthy());
    expect(api).toHaveBeenCalledWith('/api/assets/assistant/ask', { method: 'POST', body: { q: 'Show overdue tickets' } });
    expect(screen.getByText('TKT-000007').closest('a')?.getAttribute('href')).toBe('#/servicedesk/7');
    expect(screen.getByText('Tickets with no engineer')).toBeTruthy();
  });

  it('shows the error when the question cannot be answered', async () => {
    api.mockRejectedValue(new Error('Type a question of up to 300 characters.'));
    render(<Assistant />);
    fireEvent.change(screen.getByLabelText('Your question'), { target: { value: 'x' } });
    fireEvent.click(screen.getByText('Ask'));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('up to 300'));
  });

  it('writes a table as CSV with quotes escaped', () => {
    expect(tableCsv({ title: 't', headers: ['A', 'B'], rows: [['x "y"', 'z']], links: [], more: false })).toBe('"A","B"\r\n"x ""y""","z"');
  });
});
