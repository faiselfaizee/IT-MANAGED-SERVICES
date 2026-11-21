const notes = { k: 'notes', label: 'Notes', multiline: true };
const user = { k: 'username', label: 'User name' };
const pw = { k: 'password', label: 'Password', secret: true, kind: 'password' };
export const TYPES = [
    { key: 'login', label: 'Login', plural: 'Logins', main: 'username', fields: [user, pw, { k: 'url', label: 'Web address', kind: 'url', placeholder: 'https://portal.example.com' }, { k: 'totp', label: 'One-time code key (TOTP)', secret: true, kind: 'totp', placeholder: 'Secret or otpauth:// address' }, notes] },
    { key: 'server', label: 'Server account', plural: 'Server accounts', main: 'host', fields: [{ k: 'host', label: 'Server name or address' }, { k: 'protocol', label: 'Connect with', kind: 'select', options: ['RDP', 'SSH', 'WinRM', 'Web console', 'Other'] }, { k: 'port', label: 'Port' }, { k: 'domain', label: 'Domain' }, user, pw, notes] },
    { key: 'network', label: 'Network device', plural: 'Network devices', main: 'host', fields: [{ k: 'host', label: 'Address' }, { k: 'url', label: 'Management address', kind: 'url' }, user, pw, { k: 'enable', label: 'Enable password', secret: true, kind: 'password' }, { k: 'snmp', label: 'SNMP community or user', secret: true }, notes] },
    { key: 'database', label: 'Database', plural: 'Databases', main: 'host', fields: [{ k: 'engine', label: 'Engine', kind: 'select', options: ['SQL Server', 'PostgreSQL', 'MySQL / MariaDB', 'Oracle', 'MongoDB', 'Other'] }, { k: 'host', label: 'Server' }, { k: 'port', label: 'Port' }, { k: 'database', label: 'Database' }, user, pw, notes] },
    { key: 'wifi', label: 'Wi-Fi', plural: 'Wi-Fi networks', main: 'ssid', fields: [{ k: 'ssid', label: 'Network name (SSID)' }, { ...pw, label: 'Key' }, { k: 'security', label: 'Security', kind: 'select', options: ['WPA3', 'WPA2', 'WPA2 Enterprise', 'Open'] }, notes] },
    { key: 'ssh', label: 'SSH key', plural: 'SSH keys', main: 'username', fields: [user, { k: 'host', label: 'Hosts' }, { k: 'privateKey', label: 'Private key', secret: true, multiline: true }, { k: 'publicKey', label: 'Public key', multiline: true }, { k: 'passphrase', label: 'Passphrase', secret: true, kind: 'password' }, notes] },
    { key: 'apikey', label: 'API key', plural: 'API keys', main: 'service', fields: [{ k: 'service', label: 'Service' }, { k: 'keyId', label: 'Key or client ID' }, { k: 'secret', label: 'Secret', secret: true, kind: 'password' }, { k: 'url', label: 'Address', kind: 'url' }, { k: 'expires', label: 'Expires' }, notes] },
    { key: 'card', label: 'Payment card', plural: 'Payment cards', main: 'brand', fields: [{ k: 'cardholder', label: 'Name on card' }, { k: 'brand', label: 'Brand' }, { k: 'number', label: 'Number', secret: true }, { k: 'expiry', label: 'Expires (MM/YY)' }, { k: 'cvv', label: 'Security code', secret: true }, notes] },
    { key: 'identity', label: 'Identity', plural: 'Identities', main: 'fullName', fields: [{ k: 'fullName', label: 'Full name' }, { k: 'email', label: 'Email' }, { k: 'phone', label: 'Phone' }, { k: 'company', label: 'Company' }, { k: 'address', label: 'Address', multiline: true }, notes] },
    { key: 'note', label: 'Secure note', plural: 'Secure notes', fields: [{ ...notes, label: 'Note', secret: true }] },
];
export const typeDef = (k) => TYPES.find((t) => t.key === k) ?? TYPES[0];
/** The fields that hold passwords (for the health report and breach check). */
export const passwordFields = (k) => typeDef(k).fields.filter((f) => f.kind === 'password').map((f) => f.k);
export const customOf = (d) => { try {
    return d.custom ? JSON.parse(d.custom) : [];
}
catch {
    return [];
} };
export const ROLE_WORDS = { request: 'May ask for access', view: 'Can view', edit: 'Can edit', manage: 'Manages' };
export const KIND_WORDS = { customer: 'Customer', internal: 'Internal', personal: 'Personal' };
