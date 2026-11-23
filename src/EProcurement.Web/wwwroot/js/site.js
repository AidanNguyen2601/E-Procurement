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

// Menu trái dạng trượt trên điện thoại
(function () {
    const t = document.querySelector('[data-nav-toggle]');
    if (!t) return;
    t.addEventListener('click', () => document.body.classList.toggle('nav-open'));
    document.querySelector('.nav-backdrop')?.addEventListener('click', () => document.body.classList.remove('nav-open'));
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
