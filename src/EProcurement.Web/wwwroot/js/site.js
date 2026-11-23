// Xác nhận trước thao tác quan trọng: <form data-confirm="..."> hoặc <button data-confirm="..."> (ưu tiên nút được bấm)
document.addEventListener('submit', function (e) {
    const msg = (e.submitter && e.submitter.getAttribute('data-confirm')) || e.target.getAttribute('data-confirm');
    if (msg && !confirm(msg)) e.preventDefault();
});

// ===== Bộ soạn dòng chứng từ (PR / RFQ / PO) =====
(function () {
    const table = document.querySelector('table.line-editor');
    if (!table) return;
    const items = JSON.parse(table.getAttribute('data-items') || '[]');
    const showPrice = table.getAttribute('data-show-price') === '1';
    const tbody = table.querySelector('tbody');
    const fmt = n => Math.round(n).toLocaleString('vi-VN');
    const num = el => parseFloat(el && el.value ? el.value : '0') || 0;

    function fill(sel) {
        const cur = sel.getAttribute('data-value') || sel.value;
        sel.innerHTML = '<option value="">-- Hàng ngoài danh mục --</option>' +
            items.map(i => `<option value="${i.id}">${i.code} – ${i.name}</option>`).join('');
        if (cur) sel.value = cur;
    }
    function recalc() {
        let sum = 0;
        tbody.querySelectorAll('tr.line').forEach((tr, i) => {
            tr.querySelector('.idx').textContent = i + 1;
            if (!showPrice) return;
            const t = num(tr.querySelector('[data-f=Quantity]')) * num(tr.querySelector('[data-f=Price]'));
            tr.querySelector('.total').textContent = fmt(t);
            sum += t;
        });
        const s = table.querySelector('[data-sum]');
        if (s) s.textContent = fmt(sum);
        document.dispatchEvent(new CustomEvent('lines-changed', { detail: sum }));
    }
    tbody.querySelectorAll('select.item-select').forEach(fill);
    document.querySelector('[data-add-line]').addEventListener('click', () => {
        const tr = tbody.querySelector('tr.line').cloneNode(true);
        tr.querySelectorAll('input').forEach(i => i.value = i.getAttribute('data-f') === 'Quantity' ? '1' : (i.getAttribute('data-f') === 'Price' ? '0' : ''));
        const sel = tr.querySelector('select'); sel.removeAttribute('data-value'); fill(sel); sel.value = '';
        tbody.appendChild(tr); recalc();
    });
    tbody.addEventListener('click', e => {
        if (!e.target.classList.contains('remove-line')) return;
        if (tbody.querySelectorAll('tr.line').length > 1) e.target.closest('tr').remove();
        else e.target.closest('tr').querySelectorAll('input').forEach(i => i.value = '');
        recalc();
    });
    tbody.addEventListener('change', e => {
        if (!e.target.classList.contains('item-select')) return;
        const it = items.find(x => String(x.id) === e.target.value);
        if (!it) return;
        const tr = e.target.closest('tr');
        tr.querySelector('[data-f=Description]').value = it.name;
        tr.querySelector('[data-f=Unit]').value = it.unit;
        const p = tr.querySelector('[data-f=Price]');
        if (p && (!p.value || p.value === '0')) p.value = it.price;
        recalc();
    });
    document.addEventListener('input', e => { if (e.target.classList.contains('calc')) recalc(); });
    // Đặt tên Lines[i].Field liên tục trước khi gửi để model binding đọc đúng
    table.closest('form').addEventListener('submit', () => {
        tbody.querySelectorAll('tr.line').forEach((tr, i) =>
            tr.querySelectorAll('[data-f]').forEach(el => el.name = `Lines[${i}].${el.getAttribute('data-f')}`));
    });
    recalc();
})();

// Ký xác nhận quyết định: tự thêm ô mật khẩu vào mọi form duyệt / từ chối (máy chủ kiểm tra bắt buộc)
(function () {
    if (document.body.dataset.sign !== '1') return;
    document.querySelectorAll('form').forEach(f => {
        const action = (f.getAttribute('action') || '').split('?')[0];
        if (!/\/(Decide|DecidePlan|DecideResult|DecideAmendment|Appraise)(\/\d+)?$/.test(action) || f.querySelector('[name=signPassword]')) return;
        const input = document.createElement('input');
        input.type = 'password'; input.name = 'signPassword'; input.required = true; input.autocomplete = 'current-password';
        input.className = 'form-control form-control-sm sign-password'; input.placeholder = 'Mật khẩu ký'; input.title = 'Nhập mật khẩu để ký xác nhận quyết định';
        const btn = f.querySelector('button');
        btn ? btn.before(input) : f.appendChild(input);
    });
})();

// ===== Menu trái: rút gọn (chỉ icon) / mở rộng / ẩn, nhớ trạng thái nhóm, tìm nhanh =====
// Máy tính (≥ 992px): nút ☰ đổi đầy đủ ↔ rút gọn; nút « trên logo rút gọn; nút "Ẩn" ẩn hẳn; Ctrl+B ẩn / hiện. Điện thoại: ☰ mở menu trượt.
(function () {
    const body = document.body, sidebar = document.querySelector('.sidebar');
    if (!sidebar) return;
    const KEY = 'eproc.nav', GKEY = 'eproc.navGroups';
    const desktop = () => window.matchMedia('(min-width: 992px)').matches;
    const state = () => body.classList.contains('nav-hidden') ? 'hidden' : body.classList.contains('nav-mini') ? 'mini' : 'full';
    function set(s) {
        body.classList.remove('nav-mini', 'nav-hidden');
        if (s !== 'full') body.classList.add('nav-' + s);
        try { localStorage.setItem(KEY, s); } catch (e) { }
    }
    const on = (sel, fn) => document.querySelectorAll(sel).forEach(b => b.addEventListener('click', fn));
    on('[data-nav-toggle]', () => desktop() ? set(state() === 'full' ? 'mini' : 'full') : body.classList.toggle('nav-open'));
    on('[data-nav-mini]', () => desktop() ? set(state() === 'mini' ? 'full' : 'mini') : body.classList.remove('nav-open'));
    on('[data-nav-hide]', () => desktop() ? set('hidden') : body.classList.remove('nav-open'));
    document.querySelector('.nav-backdrop')?.addEventListener('click', () => body.classList.remove('nav-open'));
    document.addEventListener('keydown', e => {
        if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 'b' || e.target.closest('input, textarea, select, [contenteditable]')) return;
        e.preventDefault();
        if (desktop()) set(state() === 'hidden' ? 'full' : 'hidden'); else body.classList.toggle('nav-open');
    });

    // Nhóm menu: nhớ đóng / mở; nhóm chứa trang đang xem luôn mở
    const groups = [...sidebar.querySelectorAll('details[data-key]')];
    let saved = {};
    try { saved = JSON.parse(localStorage.getItem(GKEY) || '{}'); } catch (e) { }
    const save = () => { saved = Object.fromEntries(groups.map(d => [d.dataset.key, d.open])); try { localStorage.setItem(GKEY, JSON.stringify(saved)); } catch (e) { } };
    const hasActive = d => !!d.querySelector('a.active');
    // Chế độ gọn (accordion): chỉ mở một nhóm cấp 1; mở nhóm này thì các nhóm khác thu lại
    const accordion = () => document.documentElement.dataset.accordion === '1';
    groups.forEach(d => {
        if (hasActive(d)) d.open = true;
        else if (accordion() && d.classList.contains('nav-tree')) d.open = false;
        else if (d.dataset.key in saved) d.open = saved[d.dataset.key];
        d.addEventListener('toggle', () => {
            if (searching) return;
            if (accordion() && d.open && d.classList.contains('nav-tree'))
                groups.forEach(o => { if (o !== d && o.classList.contains('nav-tree') && o.open) o.open = false; });
            save();
        });
    });
    on('[data-nav-expand-all]', () => { groups.forEach(d => d.open = true); save(); });
    on('[data-nav-collapse-all]', () => { groups.forEach(d => d.open = hasActive(d)); save(); });

    // Chú thích khi menu rút gọn (chỉ còn icon)
    sidebar.querySelectorAll('nav a, nav summary').forEach(el => {
        const t = el.querySelector('span')?.textContent.trim();
        if (t && !el.title) el.title = t;
    });

    // Tìm nhanh chức năng: lọc mục, tự mở nhóm chứa mục khớp
    let searching = false;
    const box = document.getElementById('nav-search');
    box?.addEventListener('input', () => {
        const k = box.value.trim().toLocaleLowerCase('vi');
        searching = k.length > 0;
        const links = [...sidebar.querySelectorAll('nav a')];
        links.forEach(a => a.classList.toggle('nav-hit-hidden', searching && !a.textContent.toLocaleLowerCase('vi').includes(k)));
        // Gõ trúng tên nhóm → hiện cả nhóm
        if (searching) groups.forEach(d => { if (d.querySelector(":scope > summary").textContent.toLocaleLowerCase("vi").includes(k)) d.querySelectorAll("a").forEach(a => a.classList.remove("nav-hit-hidden")); });
        sidebar.querySelectorAll('nav .nav-group').forEach(g => g.classList.toggle('nav-hit-hidden', searching));
        [...groups].reverse().forEach(d => {
            const any = !!d.querySelector('a:not(.nav-hit-hidden)');
            d.classList.toggle('nav-hit-hidden', searching && !any);
            if (searching && any) d.open = true;
        });
        if (!searching) groups.forEach(d => d.open = hasActive(d) || (accordion() && d.classList.contains('nav-tree') ? false : d.dataset.key in saved ? saved[d.dataset.key] : true));
    });
    box?.addEventListener('keydown', e => {
        if (e.key === 'Enter') { const first = sidebar.querySelector('nav a:not(.nav-hit-hidden)'); if (first) { e.preventDefault(); location.href = first.href; } }
        if (e.key === 'Escape') { box.value = ''; box.dispatchEvent(new Event('input')); }
    });
    // Mục đang xem nằm ngoài vùng nhìn thấy của menu → cuộn tới giữa
    const act = sidebar.querySelector('nav a.active'), navEl = sidebar.querySelector('nav');
    if (act && navEl) {
        const a = act.getBoundingClientRect(), n = navEl.getBoundingClientRect();
        if (a.bottom > n.bottom || a.top < n.top) navEl.scrollTop += a.top - n.top - n.height / 2 + a.height / 2;
    }
})();

// ===== Giao diện: áp dụng tùy chọn (dùng cho xem trước ở trang Giao diện) + menu ngang trên / dưới =====
window.eprocUi = {
    apply(p) {
        const h = document.documentElement;
        if (p.nav) h.dataset.nav = p.nav;
        if (p.menu) h.dataset.menu = p.menu;
        if (p.accordion !== undefined) h.dataset.accordion = p.accordion ? '1' : '0';
        if (p.color) { h.style.setProperty('--accent', p.color); document.querySelector('meta[name=theme-color]')?.setAttribute('content', p.color); }
        if (p.fontStack) h.style.setProperty('--font', p.fontStack);
        if (p.size) h.style.fontSize = p.size + 'px';
        let link = document.getElementById('ui-font');
        if (p.fontHref) {
            if (!link) { link = document.createElement('link'); link.rel = 'stylesheet'; link.id = 'ui-font'; document.head.appendChild(link); }
            if (link.getAttribute('href') !== p.fontHref) link.href = p.fontHref;
        }
        this.dropDirection();
    },
    // Menu ở dưới: danh sách thả lên trên
    dropDirection() {
        const up = document.documentElement.dataset.nav === 'bottom';
        document.querySelectorAll('.hnav .dropdown').forEach(d => { d.classList.toggle('dropup', up); d.classList.toggle('dropdown', !up); });
    }
};
(function () {
    const hnav = document.querySelector('.hnav');
    if (!hnav) return;
    window.eprocUi.dropDirection();
    // Tìm chức năng trong menu ngang
    const box = document.getElementById('hnav-search'), out = document.getElementById('hnav-results');
    const links = [...hnav.querySelectorAll('.hnav-menu a')];
    const btn = hnav.querySelector('.hnav-search-btn');
    btn?.addEventListener('shown.bs.dropdown', () => box.focus());
    box?.addEventListener('input', () => {
        const k = box.value.trim().toLocaleLowerCase('vi');
        out.innerHTML = '';
        if (!k) return;
        links.filter(a => a.textContent.toLocaleLowerCase('vi').includes(k)).slice(0, 12).forEach(a => {
            const c = a.cloneNode(true); c.className = 'dropdown-item'; out.appendChild(c);
        });
        if (!out.children.length) out.innerHTML = '<div class="small text-muted px-2 py-1">Không tìm thấy chức năng</div>';
    });
    box?.addEventListener('keydown', e => {
        if (e.key === 'Enter') { const a = out.querySelector('a'); if (a) { e.preventDefault(); location.href = a.href; } }
    });
})();

// Đếm ngược: <span data-countdown="số giây còn lại"> (tính từ server, không phụ thuộc múi giờ máy khách)
(function () {
    const els = document.querySelectorAll('[data-countdown]');
    if (!els.length) return;
    const start = Date.now();
    function tick() {
        els.forEach(el => {
            const left = Math.max(0, Math.floor(parseInt(el.getAttribute('data-countdown'), 10) - (Date.now() - start) / 1000));
            const d = Math.floor(left / 86400), h = Math.floor(left % 86400 / 3600), m = Math.floor(left % 3600 / 60), s = left % 60;
            el.textContent = left === 0 ? 'đã hết giờ' : (d ? d + ' ngày ' : '') + String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0') + ':' + String(s).padStart(2, '0');
            el.classList.toggle('text-danger', left < 86400);
        });
    }
    tick();
    setInterval(tick, 1000);
})();

// Chèn căn cứ từ danh mục Quyết định: <select class="decision-picker" data-target="id ô nhập">
document.addEventListener('change', function (e) {
    const sel = e.target;
    if (!sel.classList || !sel.classList.contains('decision-picker') || !sel.value) return;
    const box = document.getElementById(sel.getAttribute('data-target'));
    if (box) {
        const cur = box.value.trim();
        if (!cur.includes(sel.value)) box.value = cur ? cur + (box.tagName === 'TEXTAREA' ? '\n' : '; ') + sel.value : sel.value;
    }
    sel.value = '';
});
