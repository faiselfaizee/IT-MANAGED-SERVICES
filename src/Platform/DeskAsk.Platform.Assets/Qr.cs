using System.Text;

namespace DeskAsk.Platform.Assets;

public sealed class QrCode { public int Size, Version, Mask; public bool[][] Modules = Array.Empty<bool[]>(); }

/// <summary>QR Code generator (ISO/IEC 18004), byte mode, versions 1-40, error correction L or M. No dependencies.</summary>
public static class Qr
{
    static readonly int[] ECC_L = { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 };
    static readonly int[] ECC_M = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };
    static readonly int[] BLK_L = { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 };
    static readonly int[] BLK_M = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 };
    static int[] Ecc(char e) => e == 'M' ? ECC_M : ECC_L;
    static int[] Blk(char e) => e == 'M' ? BLK_M : BLK_L;
    static int FmtBits(char e) => e == 'M' ? 0 : 1;

    static int RawModules(int v)
    {
        int r = (16 * v + 128) * v + 64;
        if (v >= 2) { int n = v / 7 + 2; r -= (25 * n - 10) * n - 55; if (v >= 7) r -= 36; }
        return r;
    }
    static int DataCodewords(int v, char e) => RawModules(v) / 8 - Ecc(e)[v] * Blk(e)[v];

    static int Mul(int x, int y) { int z = 0; for (int i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; } return z; }
    static int[] RsDivisor(int deg)
    {
        var r = new int[deg]; r[deg - 1] = 1; int root = 1;
        for (int i = 0; i < deg; i++) { for (int j = 0; j < deg; j++) { r[j] = Mul(r[j], root); if (j + 1 < deg) r[j] ^= r[j + 1]; } root = Mul(root, 2); }
        return r;
    }
    static int[] RsRemainder(List<int> data, int[] div)
    {
        var r = new int[div.Length];
        foreach (var b in data)
        {
            int f = b ^ r[0]; Array.Copy(r, 1, r, 0, r.Length - 1); r[^1] = 0;
            for (int i = 0; i < div.Length; i++) r[i] ^= Mul(div[i], f);
        }
        return r;
    }

    public static QrCode Encode(string text, char ecl = 'L', int minVersion = 1)
    {
        ecl = ecl == 'M' ? 'M' : 'L'; var bytes = Encoding.UTF8.GetBytes(text ?? "");
        int ver = Math.Max(1, minVersion);
        for (; ; ver++)
        {
            if (ver > 40) throw new Exception("Too much data for a QR code.");
            int cc = ver < 10 ? 8 : 16, used = 4 + cc + bytes.Length * 8;
            if (used <= DataCodewords(ver, ecl) * 8) break;
        }
        int cap = DataCodewords(ver, ecl) * 8; var bits = new List<int>();
        void Put(int val, int n) { for (int i = n - 1; i >= 0; i--) bits.Add((val >> i) & 1); }
        Put(4, 4); Put(bytes.Length, ver < 10 ? 8 : 16); foreach (var b in bytes) Put(b, 8);
        Put(0, Math.Min(4, cap - bits.Count)); while (bits.Count % 8 != 0) bits.Add(0);
        for (int pad = 0xEC; bits.Count < cap; pad ^= 0xEC ^ 0x11) Put(pad, 8);
        var data = new List<int>(); for (int i = 0; i < bits.Count; i += 8) { int v = 0; for (int k = 0; k < 8; k++) v = (v << 1) | bits[i + k]; data.Add(v); }
        int nb = Blk(ecl)[ver], eb = Ecc(ecl)[ver], raw = RawModules(ver) / 8, shorts = nb - raw % nb, sl = raw / nb; var div = RsDivisor(eb);
        var blocks = new List<List<int>>(); int pos = 0;
        for (int i = 0; i < nb; i++)
        {
            int len = sl - eb + (i < shorts ? 0 : 1); var d = data.GetRange(pos, len); pos += len; var e = RsRemainder(d, div);
            if (i < shorts) d.Add(0); d.AddRange(e); blocks.Add(d);
        }
        var all = new List<int>();
        for (int i = 0; i < blocks[0].Count; i++) for (int j = 0; j < blocks.Count; j++) if (i != sl - eb || j >= shorts) all.Add(blocks[j][i]);
        return Draw(ver, ecl, all, -1);
    }

    static QrCode Draw(int ver, char ecl, List<int> codewords, int forceMask)
    {
        int size = ver * 4 + 17; var m = new bool[size][]; var f = new bool[size][];
        for (int i = 0; i < size; i++) { m[i] = new bool[size]; f[i] = new bool[size]; }
        void Set(int x, int y, bool d) { m[y][x] = d; f[y][x] = true; }
        for (int i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
        void Finder(int cx, int cy) { for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++) { int d = Math.Max(Math.Abs(dx), Math.Abs(dy)), x = cx + dx, y = cy + dy; if (x >= 0 && x < size && y >= 0 && y < size) Set(x, y, d != 2 && d != 4); } }
        Finder(3, 3); Finder(size - 4, 3); Finder(3, size - 4);
        int n = ver == 1 ? 0 : ver / 7 + 2; int step = ver == 32 ? 26 : n > 0 ? (int)Math.Ceiling((ver * 4 + 4) / (double)(n * 2 - 2)) * 2 : 0; var ap = new List<int> { 6 };
        for (int p = size - 7; ap.Count < n; p -= step) ap.Insert(1, p);
        for (int i = 0; i < ap.Count; i++) for (int j = 0; j < ap.Count; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) Set(ap[i] + dx, ap[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        void Fmt(int mask)
        {
            int d = (FmtBits(ecl) << 3) | mask; int r = d; for (int i = 0; i < 10; i++) r = (r << 1) ^ ((r >> 9) * 0x537);
            int bv = ((d << 10) | r) ^ 0x5412; bool G(int i) => ((bv >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) Set(8, i, G(i)); Set(8, 7, G(6)); Set(8, 8, G(7)); Set(7, 8, G(8)); for (int i = 9; i < 15; i++) Set(14 - i, 8, G(i));
            for (int i = 0; i < 8; i++) Set(size - 1 - i, 8, G(i)); for (int i = 8; i < 15; i++) Set(8, size - 15 + i, G(i)); Set(8, size - 8, true);
        }
        Fmt(0);
        if (ver >= 7)
        {
            int r = ver; for (int i = 0; i < 12; i++) r = (r << 1) ^ ((r >> 11) * 0x1F25);
            int bv = (ver << 12) | r;
            for (int i = 0; i < 18; i++) { bool bit = ((bv >> i) & 1) != 0; int a = size - 11 + (i % 3), b = i / 3; Set(a, b, bit); Set(b, a, bit); }
        }
        int idx = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++) for (int j = 0; j < 2; j++)
                {
                    int x = right - j; bool up = ((right + 1) & 2) == 0; int y = up ? size - 1 - vert : vert;
                    if (!f[y][x] && idx < codewords.Count * 8) { m[y][x] = ((codewords[idx >> 3] >> (7 - (idx & 7))) & 1) != 0; idx++; }
                }
        }
        bool MaskAt(int mk, int x, int y) => mk switch
        {
            0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0, 4 => (x / 3 + y / 2) % 2 == 0,
            5 => x * y % 2 + x * y % 3 == 0, 6 => (x * y % 2 + x * y % 3) % 2 == 0, _ => ((x + y) % 2 + x * y % 3) % 2 == 0
        };
        void Apply(int mk) { for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (!f[y][x] && MaskAt(mk, x, y)) m[y][x] = !m[y][x]; }
        int best = 0, bestP = int.MaxValue;
        for (int mk = 0; mk < 8; mk++) { if (forceMask >= 0 && mk != forceMask) continue; Apply(mk); Fmt(mk); int p = Penalty(m, size); if (p < bestP) { bestP = p; best = mk; } Apply(mk); }
        Apply(best); Fmt(best);
        return new QrCode { Size = size, Modules = m, Version = ver, Mask = best };
    }

    static int Penalty(bool[][] m, int size)
    {
        int r = 0;
        void Line(Func<int, int, bool> get)
        {
            for (int a = 0; a < size; a++)
            {
                var sb = new StringBuilder("0000"); int run = 0; bool? col = null;
                for (int b = 0; b < size; b++)
                {
                    bool c = get(a, b); sb.Append(c ? '1' : '0');
                    if (col == c) { run++; if (run == 5) r += 3; else if (run > 5) r++; } else { col = c; run = 1; }
                }
                sb.Append("0000"); var str = sb.ToString();
                for (int i = 0; i + 11 <= str.Length; i++) { var w = str.Substring(i, 11); if (w == "00001011101" || w == "10111010000") r += 40; }
            }
        }
        Line((y, x) => m[y][x]); Line((x, y) => m[y][x]);
        for (int y = 0; y < size - 1; y++) for (int x = 0; x < size - 1; x++) { bool c = m[y][x]; if (c == m[y][x + 1] && c == m[y + 1][x] && c == m[y + 1][x + 1]) r += 3; }
        int dark = 0; foreach (var row in m) foreach (var c in row) if (c) dark++;
        int total = size * size; r += ((int)Math.Ceiling(Math.Abs(dark * 20 - total * 10) / (double)total) - 1) * 10;
        return r;
    }

    /// <summary>SVG string; quiet zone of `quiet` modules; width/height in mm when `mm` is given.</summary>
    public static string Svg(QrCode qr, int quiet = 4, double mm = 0)
    {
        int n = qr.Size + quiet * 2; var d = new StringBuilder();
        for (int y = 0; y < qr.Size; y++)
        {
            int x = 0; var row = qr.Modules[y];
            while (x < qr.Size) { if (row[x]) { int w = 1; while (x + w < qr.Size && row[x + w]) w++; d.Append('M').Append(x + quiet).Append(' ').Append(y + quiet).Append('h').Append(w).Append("v1h-").Append(w).Append('z'); x += w; } else x++; }
        }
        var dim = mm > 0 ? " width=\"" + mm.ToString(System.Globalization.CultureInfo.InvariantCulture) + "mm\" height=\"" + mm.ToString(System.Globalization.CultureInfo.InvariantCulture) + "mm\"" : "";
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 " + n + " " + n + "\"" + dim + " shape-rendering=\"crispEdges\"><rect width=\"" + n + "\" height=\"" + n + "\" fill=\"#fff\"/><path d=\"" + d + "\" fill=\"#000\"/></svg>";
    }
}
