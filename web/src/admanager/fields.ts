/** The user fields, grouped as in Active Directory Users and Computers, so the form reads like the tool people know. */
export type FieldDef = { k: string; l: string; wide?: boolean; type?: 'drive' | 'text' };
export const TABS: { key: string; title: string; fields: FieldDef[] }[] = [
  { key: 'general', title: 'General', fields: [
    { k: 'description', l: 'Description', wide: true }, { k: 'physicalDeliveryOfficeName', l: 'Office' }, { k: 'telephoneNumber', l: 'Telephone' }, { k: 'wWWHomePage', l: 'Web page' }] },
  { key: 'org', title: 'Organisation', fields: [
    { k: 'title', l: 'Job title' }, { k: 'department', l: 'Department' }, { k: 'company', l: 'Company' }, { k: 'division', l: 'Division' },
    { k: 'employeeID', l: 'Employee ID' }, { k: 'employeeNumber', l: 'Employee number' }, { k: 'employeeType', l: 'Employee type' }] },
  { key: 'phones', title: 'Phones', fields: [
    { k: 'mobile', l: 'Mobile' }, { k: 'homePhone', l: 'Home' }, { k: 'ipPhone', l: 'IP phone' }, { k: 'pager', l: 'Pager' }, { k: 'facsimileTelephoneNumber', l: 'Fax' }, { k: 'info', l: 'Notes', wide: true }] },
  { key: 'address', title: 'Address', fields: [
    { k: 'streetAddress', l: 'Street', wide: true }, { k: 'postOfficeBox', l: 'PO box' }, { k: 'l', l: 'City' }, { k: 'st', l: 'State or province' }, { k: 'postalCode', l: 'Postal code' }, { k: 'co', l: 'Country' }] },
  { key: 'profile', title: 'Profile', fields: [
    { k: 'profilePath', l: 'Profile path', wide: true }, { k: 'scriptPath', l: 'Logon script' }, { k: 'homeDrive', l: 'Home drive', type: 'drive' }, { k: 'homeDirectory', l: 'Home folder', wide: true }] },
  { key: 'more', title: 'Custom', fields: [1, 2, 3, 4, 5].map((n) => ({ k: 'extensionAttribute' + n, l: 'Extension attribute ' + n })) },
];
export const ALL_FIELDS = TABS.flatMap((t) => t.fields);
export const DRIVES = 'DEFGHIJKLMNOPQRSTUVWXYZ'.split('').map((c) => c + ':');

/** Fill a naming pattern: {first} {last} {initials} {f} (first letter of the first name) {l}. */
export function applyName(pattern: string, first: string, last: string, initials = ''): string {
  return pattern.replace(/\{(first|last|initials|f|l)\}/g, (_, k: string) => ({ first, last, initials, f: first.slice(0, 1), l: last.slice(0, 1) } as Record<string, string>)[k] ?? '').trim();
}
/** A sign-in name the directory accepts: letters, digits, . _ - and at most 20 characters. */
export function cleanSam(s: string): string {
  return s.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/\s+/g, '').replace(/[^A-Za-z0-9._-]/g, '').replace(/^[._-]+/, '').slice(0, 20);
}
