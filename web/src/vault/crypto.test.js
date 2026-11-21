import { describe, expect, it } from 'vitest';
import * as C from './crypto';
describe('vault crypto', () => {
    it('sets up, unlocks, changes the password and recovers', async () => {
        const k = await C.createKeys('correct horse battery', 1000);
        expect(k.kdf.iterations).toBe(1000);
        expect(k.recoveryCode).toMatch(/^([A-Z2-9]{4}-){7}[A-Z2-9]{4}$/);
        expect(JSON.parse(k.wrappedPrivate)).toMatchObject({ v: 1 });
        await expect(C.unlock('wrong', k.wrappedPrivate, k.kdf)).rejects.toThrow('Wrong master password.');
        const priv = await C.unlock('correct horse battery', k.wrappedPrivate, k.kdf);
        // a vault key given to me opens with the unlocked key
        const vk = await C.newAesKey();
        const box = await C.encryptJson(vk, { password: 'S3cret' });
        const mine = await C.unwrapKey(priv, await C.wrapFor(k.publicKey, vk));
        expect(await C.decryptJson(mine, box)).toEqual({ password: 'S3cret' });
        // new master password
        const r = await C.rewrap({ password: 'correct horse battery', wrappedPrivate: k.wrappedPrivate, kdf: k.kdf }, 'new long password', 1000);
        await expect(C.unlock('correct horse battery', r.wrappedPrivate, r.kdf)).rejects.toThrow();
        const again = await C.unlock('new long password', r.wrappedPrivate, r.kdf);
        expect(await C.decryptJson(await C.unwrapKey(again, await C.wrapFor(k.publicKey, vk)), box)).toEqual({ password: 'S3cret' });
        // recovery code, typed in lower case without dashes
        const rec = await C.rewrap({ recoveryCode: k.recoveryCode.toLowerCase().replace(/-/g, ' '), wrappedRecovery: k.wrappedRecovery }, 'after recovery', 1000);
        await C.unlock('after recovery', rec.wrappedPrivate, rec.kdf);
        await expect(C.rewrap({ recoveryCode: 'AAAA-BBBB', wrappedRecovery: k.wrappedRecovery }, 'x', 1000)).rejects.toThrow('Wrong recovery code.');
    }, 30000);
    it('seals a copy for someone else only they can open', async () => {
        const a = await C.createKeys('a', 1000), b = await C.createKeys('b', 1000);
        const sealed = await C.sealFor(b.publicKey, { title: 'Firewall', password: 'x' });
        expect(await C.openSealed(b.privateKey, sealed)).toEqual({ title: 'Firewall', password: 'x' });
        await expect(C.openSealed(a.privateKey, sealed)).rejects.toThrow();
    }, 30000);
    it('rejects tampered data and keys of other vaults', async () => {
        const k1 = await C.newAesKey(), k2 = await C.newAesKey();
        const box = await C.encryptJson(k1, 'hello');
        await expect(C.decryptJson(k2, box)).rejects.toThrow('Could not decrypt');
        const o = JSON.parse(box);
        o.ct = o.ct.slice(0, -4) + 'AAAA';
        await expect(C.decryptJson(k1, JSON.stringify(o))).rejects.toThrow();
    });
    it('send links need the fragment, and the passcode when there is one', async () => {
        const frag = C.random(32);
        const box = await C.encryptJson(await C.sendKey(frag, '4821'), 'wifi: Guest-2026');
        expect(await C.decryptJson(await C.sendKey(frag, '4821'), box)).toBe('wifi: Guest-2026');
        await expect(C.decryptJson(await C.sendKey(frag, '0000'), box)).rejects.toThrow();
        await expect(C.decryptJson(await C.sendKey(C.random(32), '4821'), box)).rejects.toThrow();
        expect(C.unb64(C.b64u(frag))).toEqual(frag);
    });
    it('generates passwords with every chosen kind and judges strength', () => {
        for (let i = 0; i < 50; i++) {
            const p = C.generate({ ...C.DEFAULT_GEN, length: 16 });
            expect(p).toHaveLength(16);
            expect(p).toMatch(/[A-Z]/);
            expect(p).toMatch(/[a-z]/);
            expect(p).toMatch(/[0-9]/);
            expect(p).toMatch(/[^A-Za-z0-9]/);
            expect(p).not.toMatch(/[O0Il1]/);
        }
        expect(C.generate({ ...C.DEFAULT_GEN, upper: false, symbols: false, digits: false, length: 30 })).toMatch(/^[a-z]{30}$/);
        expect(C.strength('Password2024!').score).toBeLessThanOrEqual(1);
        expect(C.strength('12345678').score).toBe(0);
        expect(C.strength(C.generate(C.DEFAULT_GEN)).score).toBe(4);
    });
    it('computes RFC 6238 codes and finds breached passwords', async () => {
        // RFC 6238 test secret "12345678901234567890" in base32, at 59 s: 94287082 (8 digits) -> 287082 with 6
        const secret = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ';
        expect((await C.totp(secret, 59_000)).code).toBe('287082');
        expect((await C.totp(`otpauth://totp/x?secret=${secret}&digits=8`, 1111111109_000)).code).toBe('07081804');
        expect(await C.totp('not a secret!')).toBeNull();
        const h = await C.sha1Hex('password');
        expect(h).toBe('5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8');
        expect(C.breachCount('1E4C9B93F3F0682250B6CF8331B7EE68FD8:9659365\nAAAA:2', h)).toBe(9659365);
        expect(C.breachCount('AAAA:2', h)).toBe(0);
    });
});
