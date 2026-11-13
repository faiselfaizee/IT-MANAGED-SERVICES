import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import AssetsApp from './AssetsApp';

const device = (over: Record<string, unknown> = {}) => ({
  id: 7, tag: 'AST-000007', name: 'Reception PC', status: 'in_use', category: 'IT hardware', companyId: 1, company: 'Acme',
  identity: { make: 'Dell', model: 'OptiPlex', serial: 'SN1', bios: '', cpuMake: '', cpuModel: 'i5', cpuSocket: '', cpus: 1, cores: 4, coresPerCpu: 4, logical: 8, ramBytes: 17179869184, storageBytes: 512110190592 },
  system: { hostname: 'RECEPTION', dnsName: '', ip: '10.0.0.5', domainType: 'domain', domain: 'acme.local', osFamily: 'windows', os: 'Windows 11', osVersion: '23H2', antivirus: 'Protected', antivirusProducts: [], lastPatch: '2026-10-01', uptimeSeconds: 90000 },
  agent: { id: 'g1', online: true, lastSeen: new Date().toISOString(), source: 'agent', inventoryAt: null },
  ownership: { location: 'Front desk', assignedTo: '', purchaseDate: '', warrantyEnd: '', underAmc: true, amc: 'Gold', rented: false, notes: '' },
  remote: { assetAllows: true, companyAllows: true },
  storage: [{ name: 'Disk 0', model: 'NVMe', type: 'SSD', size_bytes: 512110190592, partitions: [{ name: 'C:', mount: 'C:', fs: 'NTFS', size_bytes: 500000000000, free_bytes: 20000000000 }] }],
  network: [{ name: 'Ethernet', description: 'Intel', mac: 'AA:BB:CC:00:11:22', connected: true, speed_mbps: 1000, ips: ['10.0.0.5'] }], connectedIps: ['10.0.0.5'], ...over,
});

function mockApi(routes: Record<string, unknown>) {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const key = Object.keys(routes).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{}', { status: 404 });
  }));
}
beforeEach(() => { location.hash = ''; });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('asset list shows rows and the summary', async () => {
  mockApi({ '/api/assets/summary': { total: 1, byStatus: { in_use: 1 }, warrantyEndsIn30Days: 0, warrantyEndsIn90Days: 2, warrantyExpired: 0 }, '/api/assets?': { total: 1, items: [{ id: 7, tag: 'AST-000007', name: 'Reception PC', status: 'in_use', model: 'OptiPlex', manufacturer: 'Dell', assignedTo: 'Priya', location: 'HQ' }] } });
  location.hash = '#/assets'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Reception PC')).toBeTruthy());
  expect(screen.getByText('2 in 90 days')).toBeTruthy();
});

test('asset detail shows hardware, AMC badge and storage usage', async () => {
  mockApi({ '/api/assets/7/device': device() });
  location.hash = '#/assets/7'; const { unmount } = render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Reception PC')).toBeTruthy());
  expect(screen.getByText('Dell')).toBeTruthy(); expect(screen.getByText('Gold')).toBeTruthy(); expect(screen.getAllByText('Online').length).toBe(2);
  unmount(); location.hash = '#/assets/7/storage'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('NTFS')).toBeTruthy());
});

test('remote tabs explain why they are blocked', async () => {
  mockApi({ '/api/assets/7/device': device({ remote: { assetAllows: true, companyAllows: false } }), '/api/assets/commands': { count: 0, items: [] }, '/api/assets/7/jobs': [] });
  location.hash = '#/assets/7/commands'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText(/switched off for this customer/)).toBeTruthy());
});

test('a device without an agent offers no shell', async () => {
  mockApi({ '/api/assets/7/device': device({ agent: { id: '', online: false, lastSeen: null, source: '', inventoryAt: null } }) });
  location.hash = '#/assets/7/shell'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText(/has no agent/)).toBeTruthy());
  expect((screen.getByRole('button', { name: 'Open shell' }) as HTMLButtonElement).disabled).toBe(true);
});

test('commands tab lists only commands for the device system and asks before disruptive ones', async () => {
  mockApi({ '/api/assets/7/device': device(), '/api/assets/7/jobs': [], '/api/assets/commands': { count: 3, items: [
    { id: 1, title: 'Ping a host', description: '', category: 'Network', os: 'windows', shell: 'cmd', risk: 'safe', params: [{ name: 'host', label: 'Host', default: '8.8.8.8' }], enabled: true },
    { id: 2, title: 'Linux only', description: '', category: 'Network', os: 'linux', shell: 'bash', risk: 'safe', params: [], enabled: true },
    { id: 3, title: 'Restart computer', description: '', category: 'Power', os: 'windows', shell: 'cmd', risk: 'disruptive', params: [], enabled: true }] } });
  location.hash = '#/assets/7/commands'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Ping a host')).toBeTruthy());
  expect(screen.queryByText('Linux only')).toBeNull();
  fireEvent.click(screen.getAllByRole('button', { name: 'Run…' })[1]);
  const run = await screen.findByRole('button', { name: 'Run on device' }); expect((run as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByLabelText(/I understand/)); expect((run as HTMLButtonElement).disabled).toBe(false);
});

test('companies page shows the remote switch per customer', async () => {
  mockApi({ '/api/assets/companies': [{ id: 1, name: 'Acme', code: 'AC', contactName: 'Sam', contactEmail: '', phone: '', status: 'active', remoteAllowed: false, assets: 3 }] });
  location.hash = '#/assets/companies'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Acme')).toBeTruthy());
  expect(screen.getByText('Off')).toBeTruthy();
});

test('installing the agent through a controller shows each step as it happens', async () => {
  let posted = false;
  const lines = [{ seq: 1, at: new Date().toISOString(), level: 'info', text: 'Queued by admin.' }, { seq: 2, at: new Date().toISOString(), level: 'warn', text: 'Port 5985 does not answer on DKPRDHR01' }];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const u = String(url);
    if (u === '/api/assets/7/device') return new Response(JSON.stringify(device({ agent: { id: '', online: false, lastSeen: null, source: '', inventoryAt: null } })), { status: 200 });
    if (u === '/api/assets/7/agent-install' && init?.method === 'POST') { posted = true; return new Response('{"status":"queued"}', { status: 200 }); }
    if (u.startsWith('/api/assets/7/agent-install/log')) {
      const after = Number(new URL(u, 'http://x').searchParams.get('after') ?? 0);
      return new Response(JSON.stringify(posted ? { status: 'running', message: '', items: lines.filter((l) => l.seq > after) } : { status: 'none', message: '', items: [] }), { status: 200 });
    }
    return new Response('{}', { status: 404 });
  }));
  location.hash = '#/assets/7'; render(<AssetsApp />);
  const button = await screen.findByText('Install agent through a collector');
  fireEvent.click(button);
  await waitFor(() => expect(screen.getByText('Queued by admin.')).toBeTruthy());
  expect(screen.getByText('Port 5985 does not answer on DKPRDHR01')).toBeTruthy();
  expect(screen.getByText('Installing')).toBeTruthy();
  expect(screen.queryByText('Install agent through a collector')).toBeNull(); // no second click while it runs
});

test('asset detail offers the PDF and the full export', async () => {
  mockApi({ '/api/assets/7/device': device() });
  location.hash = '#/assets/7'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Reception PC')).toBeTruthy());
  expect(screen.getByRole('button', { name: 'Export PDF' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Export all details (CSV)' })).toBeTruthy();
});

test('credentials are grouped by customer and can be filtered by hardware category', async () => {
  mockApi({
    '/api/assets/companies': [{ id: 1, name: 'Acme' }, { id: 2, name: 'Globex' }],
    '/api/assets/discover/creds': { items: [
      { id: 1, name: 'Domain admin', kind: 'windows', username: 'ACME\\adm', companyId: 1, company: 'Acme', accountType: 'domain', category: 'computer', vendor: '', notes: '' },
      { id: 2, name: 'iLO read only', kind: 'bmc', username: 'audit', companyId: 1, company: 'Acme', accountType: '', category: 'server', vendor: 'HPE iLO', notes: 'Audit account' },
      { id: 3, name: 'FortiGate API', kind: 'netapi', username: 'api', companyId: 2, company: 'Globex', accountType: '', category: 'firewall', vendor: 'Fortinet FortiGate', notes: '' }] },
  });
  location.hash = '#/assets/credentials'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('iLO read only')).toBeTruthy());
  expect(screen.getAllByRole('columnheader', { name: /^(Acme|Globex)$/ }).length).toBe(2);
  expect(screen.getByText('HPE iLO')).toBeTruthy(); expect(screen.getByText('Audit account')).toBeTruthy();
  fireEvent.change(screen.getByLabelText('Hardware category'), { target: { value: 'firewall' } });
  await waitFor(() => expect(screen.queryByText('iLO read only')).toBeNull());
  expect(screen.getByText('FortiGate API')).toBeTruthy();
});
