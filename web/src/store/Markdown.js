import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
/** A small, safe markdown renderer for the website's text pages: headings, paragraphs, lists, tables, bold, italic, links. No HTML is passed through. */
export function Markdown({ text }) {
    const lines = text.replace(/\r/g, '').split('\n');
    const out = [];
    let i = 0;
    let k = 0;
    while (i < lines.length) {
        const l = lines[i];
        if (!l.trim()) {
            i++;
            continue;
        }
        const h = l.match(/^(#{1,4})\s+(.*)$/);
        if (h) {
            const level = Math.min(4, h[1].length + 1);
            const H = ('h' + level);
            out.push(_jsx(H, { children: inline(h[2]) }, k++));
            i++;
            continue;
        }
        if (/^\s*\|/.test(l)) {
            const rows = [];
            while (i < lines.length && /^\s*\|/.test(lines[i])) {
                rows.push(lines[i].trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim()));
                i++;
            }
            const body = rows.filter((r) => !r.every((c) => /^:?-{3,}:?$/.test(c) || c === ''));
            const head = rows.length > 1 && rows[1].every((c) => /^:?-{3,}:?$/.test(c)) && rows[0].some((c) => c) ? rows[0] : null;
            const data = head ? body.slice(1) : body;
            out.push(_jsx("div", { className: "md-table", children: _jsxs("table", { children: [head && _jsx("thead", { children: _jsx("tr", { children: head.map((c, j) => _jsx("th", { children: inline(c) }, j)) }) }), _jsx("tbody", { children: data.map((r, n) => _jsx("tr", { children: r.map((c, j) => j === 0 && !head ? _jsx("th", { scope: "row", children: inline(c) }, j) : _jsx("td", { children: inline(c) }, j)) }, n)) })] }) }, k++));
            continue;
        }
        if (/^\s*(\d+\.|[-*])\s+/.test(l)) {
            const ordered = /^\s*\d+\./.test(l);
            const items = [];
            while (i < lines.length && /^\s*(\d+\.|[-*])\s+/.test(lines[i])) {
                items.push(lines[i].replace(/^\s*(\d+\.|[-*])\s+(\[[ x]\]\s*)?/, ''));
                i++;
            }
            const List = ordered ? 'ol' : 'ul';
            out.push(_jsx(List, { children: items.map((t, n) => _jsx("li", { children: inline(t) }, n)) }, k++));
            continue;
        }
        const para = [];
        while (i < lines.length && lines[i].trim() && !/^(#{1,4}\s|\s*\||\s*(\d+\.|[-*])\s)/.test(lines[i])) {
            para.push(lines[i].trim());
            i++;
        }
        out.push(_jsx("p", { children: inline(para.join(' ')) }, k++));
    }
    return _jsx("div", { className: "md", children: out });
}
const SAFE = /^(https?:|mailto:|tel:|#\/)/i;
function inline(s) {
    const out = [];
    let k = 0;
    const re = /\*\*(.+?)\*\*|\*(.+?)\*|\[([^\]]+)\]\(([^)\s]+)\)|([A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})|\\([[\]*_\\])/g;
    let last = 0;
    let m;
    while ((m = re.exec(s))) {
        if (m.index > last)
            out.push(s.slice(last, m.index));
        if (m[1] != null)
            out.push(_jsx("strong", { children: inline(m[1]) }, k++));
        else if (m[2] != null)
            out.push(_jsx("em", { children: inline(m[2]) }, k++));
        else if (m[3] != null)
            out.push(SAFE.test(m[4]) ? _jsx("a", { href: m[4], rel: "noreferrer", children: m[3] }, k++) : m[3]);
        else if (m[5] != null)
            out.push(_jsx("a", { href: 'mailto:' + m[5], children: m[5] }, k++));
        else if (m[6] != null)
            out.push(m[6]);
        last = re.lastIndex;
    }
    if (last < s.length)
        out.push(s.slice(last));
    return out;
}
