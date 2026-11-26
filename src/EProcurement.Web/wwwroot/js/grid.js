// ===== Lưới (table): ẩn / hiện cột, kéo thả đổi vị trí cột, kéo mép phải tiêu đề đổi độ rộng, phân trang =====
// Tự áp dụng cho mọi <table class="table"> có một dòng tiêu đề (thead), không có ô nhập liệu.
//  - Bảng có dòng gộp ô (dòng tổng, dòng nhóm…) vẫn ẩn / hiện và đổi độ rộng được; đổi vị trí cột chỉ khi mọi dòng đều đủ ô.
//  - Phân trang phía trình duyệt khi bảng có nhiều dòng (mặc định 25 dòng / trang, chọn 10 / 25 / 50 / 100 / Tất cả).
// Loại trừ một bảng bằng data-grid="off" (cả phân trang), chỉ tắt phân trang bằng data-paging="off".
// Tùy chọn của từng bảng (cột, độ rộng, số dòng / trang) được nhớ theo trình duyệt (localStorage).
(function () {
    const tr = {
        'Cột': 'Columns', 'Hiện / ẩn cột': 'Show / hide columns', 'Đặt lại': 'Reset',
        'Kéo tiêu đề để đổi vị trí, kéo mép phải để đổi độ rộng': 'Drag a header to move the column, drag its right edge to resize',
        'Kéo mép phải để đổi độ rộng': 'Drag the right edge to resize',
        '(cột thao tác)': '(actions column)', 'Luôn giữ lại ít nhất một cột': 'Keep at least one column visible',
        'dòng / trang': 'rows / page', 'Tất cả': 'All', 'Trang trước': 'Previous page', 'Trang sau': 'Next page',
        'Kéo thả tiêu đề để đổi vị trí cột': 'Drag headers to reorder columns', 'Nhấp đúp mép cột để trả về độ rộng gốc': 'Double-click a column edge to restore its width'
    };
    const isEn = (document.documentElement.lang || 'vi') === 'en';
    const L = s => isEn && tr[s] ? tr[s] : s;
    const nf = new Intl.NumberFormat(isEn ? 'en-US' : 'vi-VN');

    const load = k => { try { return JSON.parse(localStorage.getItem(k) || 'null'); } catch (e) { return null; } };
    const save = (k, v) => { try { v ? localStorage.setItem(k, JSON.stringify(v)) : localStorage.removeItem(k); } catch (e) { } };

    const bodyRows = table => [...table.tBodies].flatMap(b => [...b.rows]);
    const isSpanRow = (r, n) => r.cells.length === 1 && r.cells[0].colSpan >= n;

    function eligible(table) {
        if (table.dataset.grid === 'off' || table.classList.contains('line-editor')) return null;
        if (!table.tHead || table.tHead.rows.length !== 1) return null;
        const head = table.tHead.rows[0];
        const n = head.cells.length;
        if (n < 2) return null;
        for (const c of head.cells) if (c.colSpan > 1 || c.rowSpan > 1) return null;
        const rows = bodyRows(table).concat(table.tFoot ? [...table.tFoot.rows] : []);
        if (rows.length === 0) return null;
        let regular = true;
        for (const r of rows) {
            if (isSpanRow(r, n)) continue;                         // dòng "chưa có dữ liệu" / tiêu đề nhóm gộp cả dòng
            let span = 0;
            for (const c of r.cells) {
                if (c.rowSpan > 1 || c.querySelector('input:not([type=hidden]), select, textarea')) return null;
                span += c.colSpan;
                if (c.colSpan > 1) regular = false;
            }
            if (span !== n) return null;
        }
        return { head, rows, n, regular };
    }

    function svgCols() {
        return '<svg viewBox="0 0 16 16" width="13" height="13" fill="currentColor" aria-hidden="true"><path d="M1 2.5A1.5 1.5 0 0 1 2.5 1h2A1.5 1.5 0 0 1 6 2.5v11A1.5 1.5 0 0 1 4.5 15h-2A1.5 1.5 0 0 1 1 13.5v-11zm5 0A1.5 1.5 0 0 1 7.5 1h1A1.5 1.5 0 0 1 10 2.5v11A1.5 1.5 0 0 1 8.5 15h-1A1.5 1.5 0 0 1 6 13.5v-11zm5 0A1.5 1.5 0 0 1 12.5 1h1A1.5 1.5 0 0 1 15 2.5v11a1.5 1.5 0 0 1-1.5 1.5h-1A1.5 1.5 0 0 1 11 13.5v-11z"/></svg>';
    }

    /** Khung bọc bảng (cuộn ngang) + thanh công cụ phía trên + chỗ đặt phân trang phía dưới */
    function frame(table) {
        if (table._frame) return table._frame;
        let host = table.parentElement;
        if (!host.classList.contains('table-responsive')) {
            host = document.createElement('div');
            host.className = 'grid-host-wrap';
            table.parentElement.insertBefore(host, table);
            host.appendChild(table);
        }
        host.classList.add('grid-host');
        const bar = document.createElement('div'); bar.className = 'grid-bar';
        host.parentElement.insertBefore(bar, host);
        const foot = document.createElement('div'); foot.className = 'grid-foot';
        host.parentElement.insertBefore(foot, host.nextSibling);
        return (table._frame = { host, bar, foot });
    }

    // ------------------------------------------------------------------ cột: ẩn / hiện, thứ tự, độ rộng
    function initColumns(table, key) {
        const info = eligible(table);
        if (!info) return;
        const { head, rows, n, regular } = info;
        const spanRows = rows.filter(r => isSpanRow(r, n));
        const dataRows = rows.filter(r => !spanRows.includes(r));
        // vị trí cột gốc của từng ô (tính theo gộp ô) + số cột gộp ban đầu
        for (const r of [head, ...dataRows]) {
            let ci = 0;
            for (const c of r.cells) { c._ci = ci; c._span0 = c.colSpan; ci += c.colSpan; }
        }
        const headers = [...head.cells];
        const label = i => (headers[i].textContent || '').replace(/\s+/g, ' ').trim() || L('(cột thao tác)');

        let st = load(key) || {};
        let order = regular && Array.isArray(st.order) && st.order.length === n && st.order.every(x => Number.isInteger(x) && x >= 0 && x < n) && new Set(st.order).size === n ? st.order : [...Array(n).keys()];
        let hidden = new Set(Array.isArray(st.hidden) ? st.hidden.filter(x => x >= 0 && x < n) : []);
        let widths = st.widths && typeof st.widths === 'object' ? st.widths : {};
        if (hidden.size >= n) hidden = new Set();

        const { bar } = frame(table);
        table.classList.add('grid-enhanced');
        if (!regular) table.classList.add('grid-fixed-order');

        const persist = () => {
            const def = order.every((x, i) => x === i) && hidden.size === 0 && Object.keys(widths).length === 0;
            const prev = load(key) || {};
            const v = def ? {} : { order, hidden: [...hidden], widths };
            if (prev.pageSize) v.pageSize = prev.pageSize;
            save(key, Object.keys(v).length ? v : null);
        };

        function apply() {
            for (const r of [head, ...dataRows]) {
                if (regular) {
                    const byCi = [...r.cells].sort((a, b) => a._ci - b._ci);
                    for (const ci of order) r.appendChild(byCi[ci]);
                    for (const c of r.cells) c.classList.toggle('grid-hide', hidden.has(c._ci));
                } else {
                    // dòng có gộp ô: giảm số cột gộp theo các cột bị ẩn nằm trong ô; ẩn ô khi mọi cột của nó đều ẩn
                    for (const c of r.cells) {
                        let h = 0;
                        for (let k = c._ci; k < c._ci + c._span0; k++) if (hidden.has(k)) h++;
                        c.classList.toggle('grid-hide', h === c._span0);
                        if (h < c._span0) c.colSpan = c._span0 - h;
                    }
                }
            }
            const visible = n - hidden.size;
            for (const r of spanRows) r.cells[0].colSpan = visible;
            head.querySelectorAll('.grid-last').forEach(c => c.classList.remove('grid-last'));
            const vis = [...head.cells].filter(c => !hidden.has(c._ci));
            if (vis.length) vis[vis.length - 1].classList.add('grid-last');
            if (Object.keys(widths).length) {
                table.style.tableLayout = 'fixed';
                let sum = 0;
                for (const th of head.cells) {
                    const w = widths[th._ci];
                    th.style.width = w ? w + 'px' : '';
                    if (!hidden.has(th._ci)) sum += w || th.offsetWidth || 120;
                }
                table.style.width = sum + 'px'; table.style.maxWidth = 'none';
            } else {
                table.style.tableLayout = ''; table.style.width = ''; table.style.maxWidth = '';
                for (const th of head.cells) th.style.width = '';
            }
        }

        // --- kéo mép phải tiêu đề để đổi độ rộng (nhấp đúp: trả về độ rộng gốc) ---
        headers.forEach(th => {
            th.classList.add('grid-th');
            th.draggable = regular;
            th.title = th.title || L(regular ? 'Kéo tiêu đề để đổi vị trí, kéo mép phải để đổi độ rộng' : 'Kéo mép phải để đổi độ rộng');
            const h = document.createElement('span');
            h.className = 'grid-rs';
            th.appendChild(h);
            h.addEventListener('mousedown', e => {
                e.preventDefault(); e.stopPropagation();
                if (!Object.keys(widths).length) [...head.cells].forEach(c => { if (!hidden.has(c._ci)) widths[c._ci] = Math.round(c.getBoundingClientRect().width); });
                const startX = e.clientX, startW = widths[th._ci] || th.offsetWidth;
                document.body.classList.add('grid-resizing');
                const move = ev => { widths[th._ci] = Math.max(40, Math.round(startW + ev.clientX - startX)); apply(); };
                const up = () => {
                    document.removeEventListener('mousemove', move); document.removeEventListener('mouseup', up);
                    document.body.classList.remove('grid-resizing'); persist();
                };
                document.addEventListener('mousemove', move); document.addEventListener('mouseup', up);
            });
            h.addEventListener('click', e => e.stopPropagation());
            h.addEventListener('dblclick', e => { e.stopPropagation(); delete widths[th._ci]; apply(); persist(); });
        });

        // --- kéo thả tiêu đề để đổi vị trí cột (chỉ bảng mọi dòng đủ ô) ---
        if (regular) {
            let dragCi = null;
            const clearOver = () => headers.forEach(t => t.classList.remove('grid-over-l', 'grid-over-r'));
            head.addEventListener('dragstart', e => {
                const th = e.target.closest && e.target.closest('th'); if (!th || !headers.includes(th)) return;
                dragCi = th._ci; e.dataTransfer.effectAllowed = 'move'; try { e.dataTransfer.setData('text/plain', String(dragCi)); } catch (x) { }
                th.classList.add('grid-dragging');
            });
            head.addEventListener('dragend', () => { dragCi = null; clearOver(); headers.forEach(t => t.classList.remove('grid-dragging')); });
            head.addEventListener('dragover', e => {
                if (dragCi === null) return;
                const th = e.target.closest('th'); if (!th || th._ci === dragCi) return;
                e.preventDefault(); clearOver();
                const r = th.getBoundingClientRect(); th.classList.add(e.clientX < r.left + r.width / 2 ? 'grid-over-l' : 'grid-over-r');
            });
            head.addEventListener('drop', e => {
                if (dragCi === null) return;
                const th = e.target.closest('th'); if (!th || th._ci === dragCi) return;
                e.preventDefault();
                const r = th.getBoundingClientRect(), after = e.clientX >= r.left + r.width / 2;
                order = order.filter(x => x !== dragCi);
                order.splice(order.indexOf(th._ci) + (after ? 1 : 0), 0, dragCi);
                dragCi = null; clearOver(); apply(); persist();
            });
        }

        // --- nút "Cột" trên thanh công cụ: bảng chọn ẩn / hiện ---
        const btn = document.createElement('button');
        btn.type = 'button'; btn.className = 'btn btn-sm btn-outline-secondary grid-cols-btn'; btn.title = L('Hiện / ẩn cột');
        btn.innerHTML = svgCols() + ' <span>' + L('Cột') + '</span>' + (hidden.size ? ' <span class="badge text-bg-secondary grid-hid-count">' + hidden.size + '</span>' : '');
        bar.appendChild(btn);
        const panel = document.createElement('div');
        panel.className = 'grid-panel'; panel.hidden = true;
        document.body.appendChild(panel);
        const refreshBtn = () => {
            const b = btn.querySelector('.grid-hid-count'); if (b) b.remove();
            if (hidden.size) btn.insertAdjacentHTML('beforeend', ' <span class="badge text-bg-secondary grid-hid-count">' + hidden.size + '</span>');
        };

        function renderPanel() {
            panel.innerHTML = '';
            const title = document.createElement('div'); title.className = 'grid-panel-title'; title.textContent = L('Hiện / ẩn cột'); panel.appendChild(title);
            for (const ci of order) {
                const lab = document.createElement('label'); lab.className = 'grid-panel-item';
                const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = !hidden.has(ci);
                cb.addEventListener('change', () => {
                    if (!cb.checked) { if (n - hidden.size <= 1) { cb.checked = true; alert(L('Luôn giữ lại ít nhất một cột')); return; } hidden.add(ci); } else hidden.delete(ci);
                    apply(); persist(); refreshBtn();
                });
                const sp = document.createElement('span'); sp.textContent = label(ci);
                lab.appendChild(cb); lab.appendChild(sp); panel.appendChild(lab);
            }
            const tip = document.createElement('div'); tip.className = 'grid-panel-tip';
            tip.textContent = (regular ? L('Kéo thả tiêu đề để đổi vị trí cột') + '. ' : '') + L('Kéo mép phải để đổi độ rộng') + '. ' + L('Nhấp đúp mép cột để trả về độ rộng gốc') + '.';
            panel.appendChild(tip);
            const reset = document.createElement('button'); reset.type = 'button'; reset.className = 'btn btn-sm btn-outline-secondary w-100 mt-2'; reset.textContent = L('Đặt lại');
            reset.addEventListener('click', () => { order = [...Array(n).keys()]; hidden = new Set(); widths = {}; apply(); persist(); refreshBtn(); renderPanel(); });
            panel.appendChild(reset);
        }
        btn.addEventListener('click', e => {
            e.stopPropagation();
            if (panel.hidden) { renderPanel(); panel.hidden = false; const r = btn.getBoundingClientRect(); panel.style.top = (r.bottom + 4) + 'px'; panel.style.right = Math.max(8, window.innerWidth - r.right) + 'px'; }
            else panel.hidden = true;
        });
        panel.addEventListener('click', e => e.stopPropagation());
        document.addEventListener('click', () => { panel.hidden = true; });
        document.addEventListener('keydown', e => { if (e.key === 'Escape') panel.hidden = true; });

        apply();
    }

    // ------------------------------------------------------------------ phân trang
    const SIZES = [10, 25, 50, 100, 0];   // 0 = tất cả
    function initPaging(table, key) {
        if (table.dataset.grid === 'off' || table.dataset.paging === 'off' || table.classList.contains('line-editor')) return;
        const n = table.tHead && table.tHead.rows[0] ? [...table.tHead.rows[0].cells].reduce((s, c) => s + c.colSpan, 0) : 0;
        const all = bodyRows(table);
        if (all.some(r => r.classList.contains('collapse'))) return;          // bảng có dòng mở rộng chi tiết
        const rows = all.filter(r => !isSpanRow(r, n || 999));
        const st = load(key) || {};
        let size = SIZES.includes(st.pageSize) ? st.pageSize : 25;
        if (rows.length <= 10) return;                                         // ít dòng: không cần phân trang
        let page = 1;
        const { foot } = frame(table);
        table.classList.add('grid-paged');

        function pages() { return size === 0 ? 1 : Math.max(1, Math.ceil(rows.length / size)); }
        function render() {
            const total = pages(); if (page > total) page = total;
            const from = size === 0 ? 0 : (page - 1) * size, to = size === 0 ? rows.length : Math.min(rows.length, from + size);
            rows.forEach((r, i) => r.classList.toggle('grid-page-hide', i < from || i >= to));
            foot.innerHTML = '';
            const info = document.createElement('span'); info.className = 'grid-foot-info';
            info.textContent = nf.format(rows.length ? from + 1 : 0) + '–' + nf.format(to) + ' / ' + nf.format(rows.length);
            foot.appendChild(info);
            if (total > 1) {
                const nav = document.createElement('ul'); nav.className = 'pagination pagination-sm mb-0';
                const item = (label, p, disabled, active, title) => {
                    const li = document.createElement('li'); li.className = 'page-item' + (disabled ? ' disabled' : '') + (active ? ' active' : '');
                    const a = document.createElement('button'); a.type = 'button'; a.className = 'page-link'; a.textContent = label; if (title) a.title = title;
                    if (!disabled && !active) a.addEventListener('click', () => { page = p; render(); table.scrollIntoView({ block: 'nearest' }); });
                    li.appendChild(a); nav.appendChild(li);
                };
                item('‹', page - 1, page === 1, false, L('Trang trước'));
                // các số trang: 1 … (p-1) p (p+1) … cuối
                const shown = new Set([1, total, page - 1, page, page + 1].filter(x => x >= 1 && x <= total));
                let last = 0;
                [...shown].sort((a, b) => a - b).forEach(p => { if (p - last > 1) item('…', 0, true, false); item(String(p), p, false, p === page); last = p; });
                item('›', page + 1, page === total, false, L('Trang sau'));
                foot.appendChild(nav);
            }
            const sel = document.createElement('select'); sel.className = 'form-select form-select-sm grid-size';
            SIZES.forEach(s => { const o = document.createElement('option'); o.value = s; o.textContent = s === 0 ? L('Tất cả') : s; if (s === size) o.selected = true; sel.appendChild(o); });
            sel.addEventListener('change', () => {
                size = parseInt(sel.value, 10); page = 1;
                const v = load(key) || {}; if (size === 25) delete v.pageSize; else v.pageSize = size; save(key, Object.keys(v).length ? v : null);
                render();
            });
            const lab = document.createElement('label'); lab.className = 'grid-size-wrap'; lab.appendChild(sel);
            lab.appendChild(document.createTextNode(' ' + L('dòng / trang')));
            foot.appendChild(lab);
        }
        render();
    }

    document.addEventListener('DOMContentLoaded', () => {
        document.querySelectorAll('table.table').forEach((t, i) => {
            const n = t.tHead && t.tHead.rows[0] ? t.tHead.rows[0].cells.length : 0;
            const key = 'eproc.grid:' + location.pathname + ':' + i + ':' + n;
            try { initColumns(t, key); } catch (e) { /* không để lỗi lưới ảnh hưởng trang */ }
            try { initPaging(t, key); } catch (e) { }
        });
    });
})();
