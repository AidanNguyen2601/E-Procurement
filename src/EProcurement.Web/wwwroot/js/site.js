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
