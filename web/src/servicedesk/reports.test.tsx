import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Reports from './Reports';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('shows the numbers and breakdowns for the month', async () => {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    if (String(url).startsWith('/api/assets/companies')) return new Response(JSON.stringify([{ id: 1, name: 'Acme' }]), { status: 200 });
    return new Response(JSON.stringify({ month: '2026-10', from: '2026-10-01T00:00:00Z', to: '2026-11-01T00:00:00Z', companyId: null, company: 'All companies',
      totals: { opened: 12, resolved: 9, backlogStart: 3, backlogEnd: 6, avgFirstResponseHours: 1.5, avgResolutionHours: 60, respondMetPercent: 92, resolveMetPercent: null },
      byPriority: [{ name: 'high', count: 4 }], byCategory: [{ name: 'Network', count: 7 }], bySource: [], byRequester: [], byAsset: [], byCompany: [{ name: 'Acme', count: 12 }],
      tickets: [{ id: 1, number: 'TKT-000001', subject: 'VPN down', status: 'open', priority: 'high', category: 'Network', requester: 'Sam', createdAt: '2026-10-02T09:00:00Z', doneAt: null }], truncated: false, changes: { completed: 2, failed: 0 } }), { status: 200 });
  }));
  render(<Reports staff />);
  await waitFor(() => expect(screen.getByText('Tickets opened')).toBeTruthy());
  expect(screen.getAllByText('12').length).toBeGreaterThan(0); expect(screen.getByText('92%')).toBeTruthy(); expect(screen.getByText('3 days')).toBeTruthy();
  expect(screen.getByText('Network')).toBeTruthy(); expect(screen.getByText('VPN down')).toBeTruthy();
});
