import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor } from '@testing-library/react';
import { Health, Policies, Dns, DirMap } from './admanager/Insight';

const load = vi.fn();
vi.mock('./http', async (orig) => ({ ...(await orig<typeof import('./http')>()), useLoad: (path: string | null) => load(path) }));
const domain = { id: 3, name: 'acme.test' };
beforeEach(() => load.mockReset());
afterEach(cleanup);

describe('AD insight pages', () => {
  it('lists findings worst first with their items', () => {
    load.mockReturnValue({ loading: false, error: '', data: { summary: { bad: 1, warn: 0, ok: 1 }, findings: [
      { key: 'krbtgt', title: 'Password of the krbtgt account', level: 'bad', text: 'Last changed 400 days ago.', items: [] },
      { key: 'priv-spn', title: 'Administrators with a service name', level: 'warn', text: 'Use a separate account.', items: ['svc-sql'], count: 3 }] } });
    render(<Health domain={domain} />);
    expect(screen.getByText('Password of the krbtgt account')).toBeTruthy();
    expect(screen.getByText('Fix')).toBeTruthy();
    expect(screen.getByText('First 1 of 3')).toBeTruthy();
    expect(screen.getByText(/1 to fix, 0 to review, 1 fine/)).toBeTruthy();
  });

  it('shows policies, where they apply and filters the unlinked ones', () => {
    load.mockReturnValue({ loading: false, error: '', data: { summary: { total: 2, unlinked: 1, empty: 1, allOff: 0 }, blocked: ['Kiosks'], gpos: [
      { id: '{A}', name: 'Default Domain Policy', status: 'Enabled', links: [{ target: '(domain) acme', enforced: true, disabled: false }], userVersion: 2, computerVersion: 2, unlinked: false, empty: false, allOff: false },
      { id: '{B}', name: 'Old Policy', status: 'Enabled', links: [], userVersion: 0, computerVersion: 0, unlinked: true, empty: true, allOff: false }] } });
    render(<Policies domain={domain} />);
    expect(screen.getByText('(domain) acme · enforced')).toBeTruthy();
    expect(screen.getByText('Kiosks')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Show'), { target: { value: 'unlinked' } });
    expect(screen.queryByText('Default Domain Policy')).toBeNull();
    expect(screen.getByText('Old Policy')).toBeTruthy();
  });

  it('chooses a DNS zone and shows its records', async () => {
    load.mockImplementation((path?: string | null) => !path ? { loading: false, error: '', data: null }
      : path.includes('zone=') ? { loading: false, error: '', data: { records: [{ name: 'www', type: 'A', data: '10.0.0.5', ttl: 3600 }] } }
      : { loading: false, error: '', data: { zones: [{ name: 'acme.test', source: 'domain' }] } });
    render(<Dns domain={domain} />);
    fireEvent.change(screen.getByLabelText('Zone'), { target: { value: 'domain|acme.test' } });
    await waitFor(() => expect(screen.getByText('10.0.0.5')).toBeTruthy());
  });

  it('draws the structure with counts, controllers and trusts', () => {
    load.mockReturnValue({ loading: false, error: '', data: { truncated: false, dcs: [{ dn: 'x', name: 'dc01.acme.test', os: 'Windows Server 2022' }], trusts: [{ partner: 'old.test', direction: 'two-way', type: 'Active Directory', forest: false }],
      tree: { dn: 'DC=acme', name: 'acme.test', kind: 'domain', users: 0, computers: 0, groups: 0, totalUsers: 5, totalComputers: 3, totalGroups: 1, children: [{ dn: 'OU=Staff', name: 'Staff', kind: 'ou', users: 5, computers: 3, groups: 1, totalUsers: 5, totalComputers: 3, totalGroups: 1, children: [] }] } } });
    render(<DirMap domain={domain} />);
    expect(screen.getByText('Staff')).toBeTruthy();
    expect(screen.getByText(/dc01.acme.test/)).toBeTruthy();
    expect(screen.getByText(/old.test/)).toBeTruthy();
  });
});
