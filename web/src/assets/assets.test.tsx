import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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

test('the asset list shows its rows', async () => {
  mockApi({ '/api/assets/summary': { total: 1, byStatus: { in_use: 1 }, warrantyEndsIn30Days: 0, warrantyEndsIn90Days: 2, warrantyExpired: 0 }, '/api/assets?': { total: 1, items: [{ id: 7, tag: 'AST-000007', name: 'Reception PC', status: 'in_use', model: 'OptiPlex', manufacturer: 'Dell', assignedTo: 'Priya', location: 'HQ' }] } });
  location.hash = '#/assets/list'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Reception PC')).toBeTruthy());
  expect(screen.getByRole('heading', { name: 'All assets' })).toBeTruthy();
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

test('the asset list shows the agent state instead of the asset status', async () => {
  mockApi({ '/api/assets/summary': { total: 3, byStatus: { in_use: 3 } }, '/api/assets?': { total: 3, items: [
    { id: 1, tag: 'AST-1', name: 'PC-ON', status: 'in_use', agent: { state: 'online', version: '2.9.3', updateDue: false } },
    { id: 2, tag: 'AST-2', name: 'PC-OLD', status: 'in_use', agent: { state: 'online', version: '2.8.6', updateDue: true } },
    { id: 3, tag: 'AST-3', name: 'PRINTER', status: 'in_use', agent: { state: 'none' } }] } });
  location.hash = '#/assets/list'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('PC-OLD')).toBeTruthy());
  expect(screen.getByRole('columnheader', { name: 'Agent' })).toBeTruthy();
  expect(screen.queryByRole('columnheader', { name: 'Status' })).toBeNull();
  expect(screen.getAllByText('Online').length).toBe(2); expect(screen.getByText('update due')).toBeTruthy(); expect(screen.getAllByText('No agent').some((e) => e.tagName === 'SPAN')).toBe(true);
});

test('an agent with an update due can be updated from the asset page, with a live log', async () => {
  let asked = false;
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const u = String(url);
    if (u.startsWith('/api/assets/7/device')) return new Response(JSON.stringify(device()), { status: 200 });
    if (u.startsWith('/api/assets/7/agent-update/log')) return new Response(JSON.stringify(asked
      ? { status: 'running', version: '2.8.6', latest: '2.9.3', online: true, items: [{ seq: 1, at: new Date().toISOString(), level: 'info', text: 'Update from 2.8.6 to 2.9.3 asked by admin.' }] }
      : { status: 'none', version: '2.8.6', latest: '2.9.3', online: true, items: [] }), { status: 200 });
    if (u === '/api/assets/7/agent-update' && init?.method === 'POST') { asked = true; return new Response('{"ok":true}', { status: 200 }); }
    return new Response('{}', { status: 404 });
  }));
  location.hash = '#/assets/7'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('update due: 2.9.3')).toBeTruthy());
  fireEvent.click(screen.getByRole('button', { name: 'Update agent now' }));
  await waitFor(() => expect(screen.getByText(/asked by admin/)).toBeTruthy());
  expect(screen.getByText('Updating')).toBeTruthy();
});

test('an ESXi host found by a scan can be read through a controller and shows its hardware', async () => {
  const esx = device({ name: 'esx01', agent: { id: '', online: false, lastSeen: null, source: 'collector', inventoryAt: new Date().toISOString() },
    watch: { mode: 'vmware', collectorId: 'c1', collector: 'Site A', readRequested: false, up: true, checkedAt: new Date().toISOString() },
    hardware: { api: 'VMware ESXi 8.0.2 build-22380479', host_state: { overall: 'yellow' },
      power_supplies: [{ name: 'Power Supply 1', state: 'OK', reading: '0' }, { name: 'Power Supply 2 AC lost', state: 'Critical', reading: '0' }],
      sensors: [{ name: 'Power Supply 2 AC lost', type: 'power', state: 'Critical', reading: '0' }, { name: 'Fan1A', type: 'fan', state: 'OK', reading: '7560 RPM' }],
      gpus: [{ name: 'Tesla T4', vendor: 'NVIDIA Corporation', slot: '0000:03:00.0' }],
      datastores: [{ name: 'datastore1', type: 'VMFS', capacity_bytes: 2e12, free_bytes: 8e11, accessible: true }],
      vms: [{ name: 'SQL01', power: 'On', guest_os: 'Windows Server 2022', ip: '10.0.0.21', cpus: 8, memory_bytes: 34359738368, disk_bytes: 107374182400 }] } });
  const calls: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const u = String(url); if (init?.method === 'POST') calls.push(u);
    if (u.startsWith('/api/assets/7/device')) return new Response(JSON.stringify(esx), { status: 200 });
    if (u.startsWith('/api/assets/collectors')) return new Response(JSON.stringify({ items: [{ id: 'c1', name: 'Site A', company: 'Acme', online: true }] }), { status: 200 });
    if (u.startsWith('/api/me') || u.includes('perms')) return new Response(JSON.stringify({ admin: true }), { status: 200 });
    return new Response('{"ok":true}', { status: 200 });
  }));
  location.hash = '#/assets/7/hardware'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Tesla T4')).toBeTruthy());
  expect(screen.getByRole('link', { name: 'Hardware' })).toBeTruthy();
  expect(screen.getByText('Hardware sensors (1 not OK)')).toBeTruthy();
  expect(screen.getAllByText('Critical').length).toBeGreaterThan(0);
  expect(screen.getByText('Virtual machines (1, 1 running)')).toBeTruthy();
  expect(screen.getByText('datastore1')).toBeTruthy();
});

test('the overview lets an administrator choose the controller and read mode, and read now', async () => {
  const esx = device({ name: 'esx01', agent: { id: '', online: false, lastSeen: null, source: '', inventoryAt: null }, hardware: {},
    watch: { mode: 'vmware', collectorId: 'c1', collector: 'Site A', readRequested: false, up: true, checkedAt: null } });
  const posts: [string, string][] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const u = String(url); if (init?.method === 'POST') posts.push([u, String(init.body)]);
    if (u.startsWith('/api/assets/7/device')) return new Response(JSON.stringify(esx), { status: 200 });
    if (u.startsWith('/api/assets/collectors')) return new Response(JSON.stringify({ items: [{ id: 'c1', name: 'Site A', company: 'Acme', online: true }] }), { status: 200 });
    return new Response('{"ok":true}', { status: 200 });
  }));
  location.hash = '#/assets/7'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Read by a controller')).toBeTruthy());
  expect(screen.getByText(/VMware logins saved under Credentials/)).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Read now' }));
  await waitFor(() => expect(posts.some(([u]) => u === '/api/assets/7/read-now')).toBe(true));
  fireEvent.change(screen.getByLabelText('Read as'), { target: { value: 'snmp' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save' }));
  await waitFor(() => expect(posts.some(([u, b]) => u === '/api/assets/7/monitor' && b.includes('"mode":"snmp"') && b.includes('"collectorId":"c1"'))).toBe(true));
});

test('a server read as VMware can also have its iDRAC read, and the physical details show on the same asset', async () => {
  const esx = device({ name: 'esx02', agent: { id: '', online: false, lastSeen: null, source: 'collector', inventoryAt: null },
    watch: { mode: 'vmware', collectorId: 'c1', collector: 'Site A', readRequested: false, up: true, checkedAt: null, bmc: '', bmcReadAt: null },
    hardware: { api: 'VMware ESXi 8.0.2', bmc_api: '15G Monolithic 7.00.00.171 (Redfish 1.17.0)', bmc: { model: '15G Monolithic', firmware: '7.00', state: 'OK' },
      memory_modules: [{ slot: 'DIMM.Socket.A2', size_bytes: 137438953472, type: 'DDR4', speed_mhz: 3200, manufacturer: 'Hynix', part: 'HMAA8GR7', state: 'OK' },
        { slot: 'DIMM.Socket.A10', size_bytes: 137438953472, type: 'DDR4', speed_mhz: 3200, manufacturer: 'Hynix', part: '', state: 'Critical' }],
      drives: [{ name: 'SSD 0', model: 'MZ7LH960', manufacturer: 'SAMSUNG', media: 'SSD', protocol: 'SATA', size_bytes: 960197124096, serial: 'S1', controller: 'PERC H755 Front', state: 'OK' }],
      storage_controllers: [{ name: 'PERC H755 Front', firmware: '52.16.1' }],
      processors: [{ socket: 'CPU.Socket.1', model: 'Xeon Gold 6338', cores: 32, threads: 64, max_mhz: 4000, state: 'OK' }],
      vms: [{ name: 'SQL01', power: 'On', cpus: 8, memory_bytes: 1, disk_bytes: 1 }] } });
  const posts: [string, string][] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const u = String(url); if (init?.method === 'POST') posts.push([u, String(init.body)]);
    if (u.startsWith('/api/assets/7/device')) return new Response(JSON.stringify(esx), { status: 200 });
    if (u.startsWith('/api/assets/collectors')) return new Response(JSON.stringify({ items: [{ id: 'c1', name: 'Site A', online: true }] }), { status: 200 });
    return new Response('{"ok":true}', { status: 200 });
  }));
  location.hash = '#/assets/7'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Read by a controller')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Management address (iLO, iDRAC, XClarity)'), { target: { value: '192.168.31.50' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save' }));
  await waitFor(() => expect(posts.some(([u, b]) => u === '/api/assets/7/monitor' && b.includes('"bmc":"192.168.31.50"') && b.includes('"mode":"vmware"'))).toBe(true));
  cleanup(); location.hash = '#/assets/7/hardware'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Memory modules (2, 256 GB)')).toBeTruthy());
  expect(screen.getByText('Physical drives (1)')).toBeTruthy(); expect(screen.getByText(/PERC H755 Front \(firmware 52.16.1\)/)).toBeTruthy();
  expect(screen.getByText(/Management controller: 15G Monolithic/)).toBeTruthy();
  expect(screen.getByText('Virtual machines (1, 1 running)')).toBeTruthy();
});

test('a switch shows its ports and its configuration versions with what changed', async () => {
  const sw = device({ name: 'CORE-SW1', agent: { id: '', online: false, lastSeen: null, source: 'collector', inventoryAt: null },
    watch: { mode: 'network', collectorId: 'c1', collector: 'Site A', readRequested: false, up: true, checkedAt: null, bmc: '' },
    hardware: { api: 'Cisco (SNMP)', port_summary: { total: 3, connected: 1, free: 1, disabled: 1, speeds: { '1 Gbps': 1 } },
      ports: [{ name: 'Gi1/0/1', description: 'Uplink', kind: 'Ethernet', state: 'Connected', speed: '1 Gbps' }, { name: 'Gi1/0/2', kind: 'Ethernet', state: 'Not connected' }, { name: 'Gi1/0/3', kind: 'Ethernet', state: 'Disabled' }],
      modules: [{ name: 'Switch 1', kind: 'Chassis', model: 'C9200L-48P-4X', serial: 'JAE25110ABC', software: '17.9.4' }] } });
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const u = String(url);
    if (u.startsWith('/api/assets/7/device')) return new Response(JSON.stringify(sw), { status: 200 });
    if (u === '/api/assets/7/configs') return new Response(JSON.stringify({ keep: 30, checkedAt: new Date().toISOString(), error: '', items: [
      { id: 2, at: '2026-11-13T10:00:00Z', source: 'ssh', vendor: 'cisco', size: 60, lines: 3 }, { id: 1, at: '2026-11-12T10:00:00Z', source: 'ssh', vendor: 'cisco', size: 50, lines: 3 }] }), { status: 200 });
    if (u === '/api/assets/7/configs/2') return new Response(JSON.stringify({ id: 2, text: 'hostname CORE-SW1\ninterface Gi1/0/1\n description Uplink to FW2' }), { status: 200 });
    if (u === '/api/assets/7/configs/1') return new Response(JSON.stringify({ id: 1, text: 'hostname CORE-SW1\ninterface Gi1/0/1\n description Uplink to FW' }), { status: 200 });
    return new Response('{}', { status: 200 });
  }));
  location.hash = '#/assets/7/hardware'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Ports: 3, 1 in use, 1 free, 1 switched off')).toBeTruthy());
  expect(screen.getByText('In use: 1 at 1 Gbps')).toBeTruthy(); expect(screen.getByText('JAE25110ABC')).toBeTruthy();
  expect(screen.getByRole('link', { name: 'Configuration' })).toBeTruthy();
  cleanup(); location.hash = '#/assets/7/config'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText(/description Uplink to FW2/)).toBeTruthy());
  fireEvent.click(screen.getByRole('button', { name: 'Changes from the version before' }));
  await waitFor(() => expect(screen.getByText('Changes: 1 lines added, 1 removed')).toBeTruthy());
  expect(screen.getByText(/^\+\s+description Uplink to FW2$/)).toBeTruthy(); expect(screen.getByText(/^-\s+description Uplink to FW$/)).toBeTruthy();
});

const metrics = (company: string, companyId: number | null, x: Record<string, number> = {}) => ({ company, companyId, total: 0, inService: 0, agentOnline: 0, agentUpdate: 0, agentOffline: 0, agentNone: 0,
  compliant: 0, nonCompliant: 0, noData: 0, warrantyCovered: 0, warrantySoon: 0, warrantyExpired: 0, warrantyUnknown: 0, computers: 0, servers: 0, network: 0, printers: 0, other: 0, hardwareNotOk: 0,
  portsTotal: 0, portsUsed: 0, portsFree: 0, portsOff: 0, ...x });

test('the assets home shows one tile per customer, worst first, and a tile opens that customer', async () => {
  const acme = metrics('Acme Ltd', 1, { total: 40, inService: 38, agentOnline: 30, agentOffline: 5, nonCompliant: 4, hardwareNotOk: 1 });
  const glob = metrics('Globex', 2, { total: 12, inService: 12, agentOnline: 12 });
  mockApi({ '/api/assets/dashboard?company=1': { customer: acme }, '/api/assets/dashboard': { all: metrics('All customers', null, { total: 52 }), customers: [glob, acme], restricted: false } });
  location.hash = '#/assets'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('Acme Ltd')).toBeTruthy());
  const tiles = [...document.querySelectorAll('a.co-tile')].map((a) => a.getAttribute('href'));
  expect(tiles).toEqual(['#/assets/customer/all', '#/assets/customer/1', '#/assets/customer/2']); // all, then the customer with problems
  expect(screen.getByRole('img', { name: 'Agents of Acme Ltd: 30 live, 0 live, update due, 5 not reporting, 0 no agent' })).toBeTruthy();
  fireEvent.change(screen.getByLabelText('Find a customer'), { target: { value: 'glo' } });
  expect(document.querySelectorAll('a.co-tile').length).toBe(1);
  cleanup(); location.hash = '#/assets/customer/1'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Acme Ltd' })).toBeTruthy());
  expect(screen.getByText('Not reporting').closest('a')?.getAttribute('href')).toBe('#/assets/list?company=1&agent=offline');
  expect(screen.getByText('Not compliant').closest('a')?.getAttribute('href')).toBe('#/assets/list?company=1&compliance=noncompliant');
  expect(screen.getByRole('link', { name: 'View all assets' }).getAttribute('href')).toBe('#/assets/list?company=1');
});

test('someone limited to one customer lands on that customer', async () => {
  const acme = metrics('Acme Ltd', 1, { total: 3 });
  mockApi({ '/api/assets/dashboard?company=1': { customer: acme }, '/api/assets/dashboard': { all: acme, customers: [acme], restricted: true } });
  location.hash = '#/assets'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Acme Ltd' })).toBeTruthy());
  expect(document.querySelectorAll('a.co-tile').length).toBe(0);
});

test('the filtered list shows where it came from and sends the filters; chosen assets get their agents updated together', async () => {
  const urls: string[] = []; const posts: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const u = String(url); urls.push(u); if (init?.method === 'POST') posts.push(String(init.body));
    if (u.startsWith('/api/assets/dashboard?company=1')) return new Response(JSON.stringify({ customer: metrics('Acme Ltd', 1) }), { status: 200 });
    if (u.startsWith('/api/assets/agents/bulk')) return new Response(JSON.stringify({ done: 1, skipped: { agent_offline: 1 } }), { status: 200 });
    if (u.startsWith('/api/assets?')) return new Response(JSON.stringify({ total: 2, items: [{ id: 1, tag: 'A1', name: 'PC-1', agent: { state: 'online', version: '2.8.6', updateDue: true } }, { id: 2, tag: 'A2', name: 'PC-2', agent: { state: 'offline' } }] }), { status: 200 });
    if (u.startsWith('/api/me') || u.includes('perms')) return new Response(JSON.stringify({ admin: true }), { status: 200 });
    return new Response('{}', { status: 200 });
  }));
  location.hash = '#/assets/list?company=1&compliance=noncompliant'; render(<AssetsApp />);
  await waitFor(() => expect(screen.getByText('PC-1')).toBeTruthy());
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Acme Ltd' })).toBeTruthy());
  expect(urls.some((u) => u.startsWith('/api/assets?') && u.includes('&company=1') && u.includes('&compliance=noncompliant'))).toBe(true);
  expect(screen.getByRole('link', { name: 'Not compliant ✕' }).getAttribute('href')).toBe('#/assets/list?company=1');
  fireEvent.click(screen.getByLabelText('Select PC-1')); fireEvent.click(screen.getByLabelText('Select PC-2'));
  const bulk = await screen.findByRole('region', { name: 'Agent actions' });
  fireEvent.click(within(bulk).getByRole('button', { name: 'Update agents' }));
  await waitFor(() => expect(screen.getByRole('status').textContent).toBe('Update asked for 1 agent. Skipped: 1 are offline.'));
  expect(JSON.parse(posts[0])).toEqual({ action: 'update', assetIds: [1, 2] });
});
