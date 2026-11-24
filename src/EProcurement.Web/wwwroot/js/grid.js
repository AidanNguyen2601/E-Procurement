// ===== Lưới (table): ẩn / hiện cột, kéo thả đổi vị trí cột, kéo mép phải tiêu đề đổi độ rộng =====
// Tự áp dụng cho mọi <table class="table"> có một dòng tiêu đề (thead) và dữ liệu thuần chữ (không có ô nhập liệu / gộp dòng).
// Loại trừ một bảng bằng data-grid="off". Tùy chọn của từng bảng được nhớ theo trình duyệt (localStorage), có nút "Đặt lại".
(function () {
    const T = window.eprocT || (s => s);
    const tr = {
        'Cột': 'Columns', 'Hiện / ẩn cột': 'Show / hide columns', 'Đặt lại': 'Reset',
        'Kéo tiêu đề để đổi vị trí, kéo mép phải để đổi độ rộng': 'Drag a header to move the column, drag its right edge to resize',
        '(cột thao tác)': '(actions column)', 'Luôn giữ lại ít nhất một cột': 'Keep at least one column visible'
    };
    const isEn = (document.documentElement.lang || 'vi') === 'en';
    const L = s => isEn && tr[s] ? tr[s] : s;

    const load = k => { try { return JSON.parse(localStorage.getItem(k) || 'null'); } catch (e) { return null; } };
    const save = (k, v) => { try { v ? localStorage.setItem(k, JSON.stringify(v)) : localStorage.removeItem(k); } catch (e) { } };

    function eligible(table) {
        if (table.dataset.grid === 'off' || table.classList.contains('line-editor')) return null;
        if (!table.tHead || table.tHead.rows.length !== 1) return null;
        const head = table.tHead.rows[0];
        const n = head.cells.length;
        if (n < 2) return null;
        for (const c of head.cells) if (c.colSpan > 1 || c.rowSpan > 1) return null;
        const rows = [...table.tBodies].flatMap(b => [...b.rows]).concat(table.tFoot ? [...table.tFoot.rows] : []);
        if (rows.length === 0) return null;
        for (const r of rows) {
            if (r.cells.length === 1 && r.cells[0].colSpan >= n) continue; // dòng "chưa có dữ liệu" / tổng gộp
            if (r.cells.length !== n) return null;
            for (const c of r.cells) {
                if (c.rowSpan > 1 || c.colSpan > 1 || c.querySelector('input:not([type=hidden]), select, textarea')) return null;
            }
        }
        return { head, rows, n };
    }

    function init(table, idx) {
        const info = eligible(table);
        if (!info) return;
        const { head, rows, n } = info;
        const key = 'eproc.grid:' + location.pathname + ':' + idx + ':' + n;
        const spanRows = rows.filter(r => r.cells.length === 1 && r.cells[0].colSpan >= n);
        const dataRows = rows.filter(r => !spanRows.includes(r));
        [head, ...dataRows].forEach(r => [...r.cells].forEach((c, i) => { c._ci = i; }));
        const headers = [...head.cells];
        const label = i => (headers[i].textContent || '').replace(/\s+/g, ' ').trim() || L('(cột thao tác)');

        let st = load(key) || {};
        let order = Array.isArray(st.order) && st.order.length === n && st.order.every(x => Number.isInteger(x) && x >= 0 && x < n) && new Set(st.order).size === n ? st.order : [...Array(n).keys()];
        let hidden = new Set(Array.isArray(st.hidden) ? st.hidden.filter(x => x >= 0 && x < n) : []);
        let widths = st.widths && typeof st.widths === 'object' ? st.widths : {};
        if (hidden.size >= n) hidden = new Set();

        // Khung bọc để đặt nút "Cột" ở góc phải và cho phép cuộn ngang khi bảng rộng ra
        let host = table.parentElement;
        if (!host.classList.contains('table-responsive')) {
            host = document.createElement('div');
            host.className = 'grid-host-wrap';
            table.parentElement.insertBefore(host, table);
            host.appendChild(table);
        }
        host.classList.add('grid-host');
        table.classList.add('grid-enhanced');

        const persist = () => {
            const def = order.every((x, i) => x === i) && hidden.size === 0 && Object.keys(widths).length === 0;
            save(key, def ? null : { order, hidden: [...hidden], widths });
        };

        function apply() {
            for (const r of [head, ...dataRows]) {
                const byCi = [...r.cells].sort((a, b) => a._ci - b._ci);
                for (const ci of order) r.appendChild(byCi[ci]);
                for (const c of r.cells) c.classList.toggle('grid-hide', hidden.has(c._ci));
            }
            const visible = n - hidden.size;
            for (const r of spanRows) { const c = r.cells[0]; c.colSpan = visible; }
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

        // --- Kéo mép phải tiêu đề để đổi độ rộng ---
        headers.forEach(th => {
            th.classList.add('grid-th');
            th.draggable = true;
            th.title = th.title || L('Kéo tiêu đề để đổi vị trí, kéo mép phải để đổi độ rộng');
            const h = document.createElement('span');
            h.className = 'grid-rs';
            th.appendChild(h);
            h.addEventListener('mousedown', e => {
                e.preventDefault(); e.stopPropagation();
                if (!Object.keys(widths).length) head && [...head.cells].forEach(c => { if (!hidden.has(c._ci)) widths[c._ci] = Math.round(c.getBoundingClientRect().width); });
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
            h.addEventListener('dblclick', e => { e.stopPropagation(); delete widths[th._ci]; if (!Object.keys(widths).length) widths = {}; apply(); persist(); });
        });

        // --- Kéo thả tiêu đề để đổi vị trí cột ---
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
            const at = order.indexOf(th._ci) + (after ? 1 : 0);
            order.splice(at, 0, dragCi);
            dragCi = null; clearOver(); apply(); persist();
        });

        // --- Nút "Cột": bảng chọn ẩn / hiện ---
        const btn = document.createElement('button');
        btn.type = 'button'; btn.className = 'grid-gear'; btn.title = L('Hiện / ẩn cột'); btn.setAttribute('aria-label', L('Hiện / ẩn cột'));
        btn.innerHTML = '<svg viewBox="0 0 16 16" width="13" height="13" fill="currentColor" aria-hidden="true"><path d="M1 2.5A1.5 1.5 0 0 1 2.5 1h2A1.5 1.5 0 0 1 6 2.5v11A1.5 1.5 0 0 1 4.5 15h-2A1.5 1.5 0 0 1 1 13.5v-11zm5 0A1.5 1.5 0 0 1 7.5 1h1A1.5 1.5 0 0 1 10 2.5v11A1.5 1.5 0 0 1 8.5 15h-1A1.5 1.5 0 0 1 6 13.5v-11zm5 0A1.5 1.5 0 0 1 12.5 1h1A1.5 1.5 0 0 1 15 2.5v11a1.5 1.5 0 0 1-1.5 1.5h-1A1.5 1.5 0 0 1 11 13.5v-11z"/></svg>';
        host.appendChild(btn);
        const panel = document.createElement('div');
        panel.className = 'grid-panel'; panel.hidden = true;
        document.body.appendChild(panel);

        function renderPanel() {
            panel.innerHTML = '';
            const title = document.createElement('div'); title.className = 'grid-panel-title'; title.textContent = L('Cột'); panel.appendChild(title);
            for (const ci of order) {
                const lab = document.createElement('label'); lab.className = 'grid-panel-item';
                const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = !hidden.has(ci);
                cb.addEventListener('change', () => {
                    if (!cb.checked) { if (n - hidden.size <= 1) { cb.checked = true; alert(L('Luôn giữ lại ít nhất một cột')); return; } hidden.add(ci); } else hidden.delete(ci);
                    apply(); persist();
                });
                const sp = document.createElement('span'); sp.textContent = label(ci);
                lab.appendChild(cb); lab.appendChild(sp); panel.appendChild(lab);
            }
            const reset = document.createElement('button'); reset.type = 'button'; reset.className = 'btn btn-sm btn-outline-secondary w-100 mt-2'; reset.textContent = L('Đặt lại');
            reset.addEventListener('click', () => { order = [...Array(n).keys()]; hidden = new Set(); widths = {}; apply(); persist(); renderPanel(); });
            panel.appendChild(reset);
        }
        btn.addEventListener('click', e => { e.stopPropagation(); if (panel.hidden) { renderPanel(); panel.hidden = false; const r = btn.getBoundingClientRect(); panel.style.top = (r.bottom + 4) + "px"; panel.style.right = Math.max(8, window.innerWidth - r.right) + "px"; } else panel.hidden = true; });
        panel.addEventListener('click', e => e.stopPropagation());
        document.addEventListener('click', () => { panel.hidden = true; });
        document.addEventListener('keydown', e => { if (e.key === 'Escape') panel.hidden = true; });

        apply();
    }

    document.addEventListener('DOMContentLoaded', () => {
        document.querySelectorAll('table.table').forEach((t, i) => { try { init(t, i); } catch (e) { /* không để lỗi lưới ảnh hưởng trang */ } });
    });
})();
