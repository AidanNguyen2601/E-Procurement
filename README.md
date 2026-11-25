# E-Procurement — ASP.NET Core 8 MVC + SQL Server

Hệ thống mua sắm điện tử cơ bản, số hóa quy trình từ đề nghị mua hàng đến nhận hàng và đánh giá nhà cung cấp.

```
Đề nghị mua hàng (PR) → Trưởng bộ phận duyệt → (vượt ngưỡng) Giám đốc duyệt
   → Yêu cầu báo giá (RFQ, mời ≥ 3 NCC) → Nhập báo giá → So sánh → Chọn NCC
   → Đơn mua hàng (PO) → (vượt ngưỡng) Giám đốc duyệt → Gửi NCC
   → Nhận hàng (GRN, nhiều đợt, đạt / không đạt) → PR hoàn tất → Đánh giá NCC
```

## 1. Tính năng

| Module | Chức năng chính |
|---|---|
| Dashboard | Chứng từ chờ tôi duyệt, PR chờ mua hàng, RFQ đang mở, PO trễ hạn giao; giá trị mua tháng / năm, tiết kiệm so với dự toán, chi tiêu 6 tháng, theo nhóm hàng, top NCC |
| Đề nghị mua hàng (PR) | Tạo nhiều dòng (chọn từ danh mục hoặc hàng ngoài danh mục), dự toán tự tính; lưu nháp / gửi duyệt; duyệt 2 cấp theo ngưỡng; từ chối bắt buộc lý do; hủy; thanh tiến trình + nhật ký phê duyệt |
| Gói mua sắm & KHLCNT | Gom 1 hoặc nhiều PR đã duyệt thành **gói mua sắm** (giá gói, nguồn vốn, hình thức + gợi ý theo hạn mức / PO tham chiếu, phương thức, loại HĐ, thời gian); **kế hoạch lựa chọn NCC** gồm nhiều gói, Giám đốc ký duyệt, in tờ trình / quyết định. Chỉ gói đã duyệt mới được lập hồ sơ chào giá chỉ định / chào giá cạnh tranh / HSMT / đơn mua sắm trực tiếp; dòng hàng lấy từ tất cả PR trong gói. Dữ liệu cũ tự sinh gói + KHLCNT "chuyển đổi" |
| Lĩnh vực gói & hạn mức theo loại mua sắm | Mỗi gói có **lĩnh vực** (danh mục dùng chung "Lĩnh vực gói": Hàng hóa, Dịch vụ phi tư vấn, Dịch vụ tư vấn, Xây lắp, Thiết bị & phần mềm CNTT, Bảo trì – sửa chữa, Hỗn hợp), bắt buộc chọn khi lập gói (tự gợi ý từ nội dung đề nghị; gói cũ được gán tự động), hiển thị ở danh sách / chi tiết gói và lọc được ở Gói mua sắm, Tiến độ gói. Thiết lập → Hạn mức & thời gian gói: **bộ hạn mức riêng cho từng lĩnh vực** (mặc định có sẵn bộ Xây lắp thời gian thực hiện dài hơn); lĩnh vực chưa có bộ riêng dùng bộ "Mặc định". **Hạn mức theo hình thức mua sắm** (mua trực tiếp, chào hàng cạnh tranh, chỉ định thầu, đấu thầu rộng rãi): mỗi bộ có thể gắn lĩnh vực và / hoặc hình thức; gói dùng bộ cụ thể nhất có sẵn theo thứ tự lĩnh vực + hình thức → lĩnh vực → hình thức → Mặc định (mặc định có sẵn bộ cho cả 4 hình thức, càng cạnh tranh thì giai đoạn lựa chọn NCC càng dài). Ô tiền (giá gói, giá gói tối đa của hạn mức, đơn giá BOQ, giá phát sinh) **tự định dạng 1.000.000 khi gõ** và gửi số thuần lên máy chủ |
| Tiến độ gói & hạn mức thời gian | Mỗi gói đi qua 6 giai đoạn: lập gói → duyệt KHLCNT → lựa chọn NCC → ký hợp đồng & đặt hàng → thực hiện (giao nhận & nghiệm thu) → thanh toán; trạng thái được suy ra từ dữ liệu thật (KHLCNT, hồ sơ chọn NCC, hợp đồng, đơn hàng, phiếu nhận, thanh toán, nhật ký phê duyệt). **Thiết lập → Hạn mức & thời gian gói** (Admin): các hạn mức theo giá gói (mặc định nhỏ ≤ 100 triệu & vừa ≤ 1 tỷ & lớn ≤ 5 tỷ & rất lớn) với số ngày cho từng giai đoạn; giai đoạn sau tính từ lúc giai đoạn trước xong. Menu **Kế hoạch & đề nghị → Tiến độ gói**: chip đếm số gói theo giai đoạn & đúng tiến độ & sắp đến hạn & quá hạn, thanh các giai đoạn từng gói (xanh đúng hạn, vàng xong nhưng trễ, xanh dương đang làm, đỏ quá hạn), hạn giai đoạn và số ngày trễ; trang chi tiết gói có bảng tiến độ từng giai đoạn (kế hoạch & thực tế, trễ bao nhiêu ngày). **Cảnh báo tự động** (Thiết lập → Cảnh báo): "Gói sắp đến hạn giai đoạn" nhắc người phụ trách và thành viên gói trước N ngày; "Gói quá hạn giai đoạn" cảnh báo người phụ trách, thành viên và Giám đốc, nhắc lại mỗi tuần, có ngày ân hạn |
| BOQ phân cấp | Bảng khối lượng nhiều cấp cho từng gói mua sắm (từ trang chi tiết gói → "Lập & sửa BOQ"): hạng mục → công việc → chi tiết (tối đa 8 cấp), mã tự đánh 1, 1.1, 1.1.1…; dòng nhóm tự cộng dồn từ dòng con, dòng lá = khối lượng × đơn giá. Thêm dòng con, sửa, xóa cả nhánh, lên & xuống, thụt vào & lùi ra, thu gọn & mở nhánh; **nhập** dán từ Excel & CSV (Tab hoặc `;`, mã quyết định cấp) hoặc nạp BOQ mẫu, **xuất CSV** nhập lại được; "Áp tổng BOQ vào giá gói" và cảnh báo lệch giá gói. Chỉ sửa khi gói ở trạng thái Nháp và theo quyền theo gói; các trạng thái khác chỉ xem. **Đấu thầu theo BOQ**: gói thầu tạo từ gói có BOQ tự sinh dòng từ các dòng lá của BOQ (giữ mã và hạng mục cha); nhà thầu chào đơn giá từng dòng trên cổng NCC; sau khi mở thầu, trang "So sánh giá theo BOQ" (gói thầu → Kết quả đánh giá) hiển thị ma trận dòng × nhà thầu: giá thấp nhất từng dòng, lệch so với dự toán, cảnh báo đơn giá thấp (&lt; 70%) / cao (&gt; 130%) bất thường hoặc thiếu giá, cộng dồn theo hạng mục, xuất CSV. **BOQ trong hợp đồng & nghiệm thu khối lượng**: hợp đồng ký từ gói thầu theo BOQ giữ nguyên cây BOQ với đơn giá trúng thầu; trang hợp đồng có bảng tiến độ (KL hợp đồng / đã nghiệm thu / %) và nút "Lập nghiệm thu khối lượng": mỗi đợt nhập khối lượng hoàn thành từng dòng, lũy kế không được vượt khối lượng hợp đồng; biên bản phải được người khác (Giám đốc / nhân viên mua hàng khác) xác nhận; kế toán "Đề nghị thanh toán" theo khối lượng đã nghiệm thu (gồm VAT, tự khấu trừ tạm ứng theo tỷ lệ, chặn vượt giá trị hợp đồng) → Giám đốc duyệt → chi như các đề nghị thanh toán khác. **Cơ cấu giá & phiên bản BOQ**: mỗi dòng có thể nhập cơ cấu đơn giá vật liệu / nhân công / máy thi công (đơn giá = tổng ba thành phần); mục "Cơ cấu chi phí" tính chi phí trực tiếp → chi phí chung → lợi nhuận / thu nhập chịu thuế tính trước → dự phòng → giá trị trước VAT (áp vào giá gói) → VAT → tổng cộng, kèm tách vật liệu / nhân công / máy; nhập BOQ từ **file Excel `.xlsx`** (sheet đầu, đọc trực tiếp không cần thư viện ngoài), CSV hoặc dán văn bản với các cột tùy chọn Vật liệu / Nhân công / Máy, có BOQ xây dựng mẫu; BOQ được **lưu phiên bản** tự động trước khi thay thế, khôi phục hoặc lập gói thầu (và lưu tay), có trang so sánh phiên bản với BOQ hiện tại (thêm / xóa / sửa, chênh lệch thành tiền) và khôi phục. **Khối lượng phát sinh**: trang hợp đồng → "Điều chỉnh khối lượng" lập phụ lục sửa khối lượng từng dòng BOQ và / hoặc thêm dòng phát sinh dưới một hạng mục (mã tự đánh), giá trị chênh lệch tự tính theo đơn giá hợp đồng; khối lượng mới không được thấp hơn khối lượng đã nghiệm thu; Giám đốc duyệt phụ lục như các phụ lục khác, khi duyệt hệ thống áp dụng khối lượng mới, cộng giá trị hợp đồng, giữ khối lượng gốc để so sánh (KL ban đầu / hiện hành / chênh lệch) và nghiệm thu, thanh toán theo khối lượng mới. **Giá dự toán – giá trúng thầu – giá thực tế theo gói thầu** (mọi gói thầu, không riêng BOQ): menu Lựa chọn NCC → Đấu thầu → "Dự toán – thực tế" (tổng hợp các gói thầu đã có kết quả, gồm tiết kiệm do đấu thầu) và nút "Dự toán – thực tế" trên từng gói thầu: giá gói, dự toán (tổng các dòng), giá trúng thầu (± dự toán), giá hợp đồng hiện hành (kể cả phụ lục), thực tế (khối lượng đã thực hiện × đơn giá hợp đồng – theo nghiệm thu khối lượng với hợp đồng BOQ, theo hàng đã nhận đạt với hợp đồng hàng hóa – so với dự toán của đúng phần đã làm), tiến độ giá trị, thực chi / chờ chi (hợp đồng khung: thêm giá trị đã đặt hàng so với hạn mức hợp đồng, thực tế tính từ hàng đã nhận qua các đơn đặt hàng theo đợt); so sánh từng dòng, dòng phát sinh được đánh dấu, xuất CSV |
| Quản lý nguồn cung | **Sơ tuyển NCC** theo nhóm hàng (tiêu chí bắt buộc + chấm điểm, NCC nộp hồ sơ trên cổng, chấm, Giám đốc ký duyệt; NCC đạt được ưu tiên mời chào giá, NCC chờ thẩm định chuyển Đã duyệt) + danh sách NCC tự đăng ký chờ thẩm định · **Đánh giá NCC**: bảng xếp loại A/B/C/D từ phiếu đánh giá + % đúng hạn + % hàng không đạt, đánh giá định kỳ theo quý có gợi ý điểm · **Ngân hàng giá**: đơn giá từ đơn hàng, báo giá, HSDT, chỉ định, hợp đồng và giá khảo sát nhập tay · **Bảng giá tiêu chuẩn**: giá chuẩn + giá trần theo thời kỳ, đề xuất từ ngân hàng giá, Giám đốc ký duyệt, thay giá tham khảo; PO vượt giá trần bắt buộc lý do + Giám đốc duyệt · 3 báo cáo nguồn cung |
| Chờ tôi duyệt | Hộp duyệt tập trung: Trưởng bộ phận duyệt PR phòng mình; Giám đốc duyệt PR & PO vượt ngưỡng ngay trên danh sách |
| Danh mục hàng hóa | Mã, nhóm hàng, đơn vị, giá tham khảo, quy cách, thương hiệu, xuất xứ |
| Danh mục dùng chung | 16 loại: phòng ban, đơn vị tính, điều khoản thanh toán, loại hợp đồng, địa điểm giao hàng, ngân hàng, thời hạn bảo hành, đơn vị tiền tệ, nguồn vốn, loại đơn hàng, tổ triển khai, quyết định (chèn nhanh làm căn cứ KHLCNT / bảng giá), lĩnh vực kinh doanh (NCC), xếp loại NCC (thang điểm dùng cho Đánh giá NCC), xuất xứ, thương hiệu; kèm lối tắt tới hàng hóa, nhóm hàng, NCC, nhân viên; đổi tên một giá trị thì dữ liệu gốc đang dùng tên cũ cập nhật theo |
| Menu & giao diện | Menu gom 7 nhóm (Kế hoạch & yêu cầu, Lựa chọn NCC, Hợp đồng & thanh toán, Nguồn cung, Báo cáo, Danh mục, Hệ thống), chế độ gọn chỉ mở một nhóm. Trang Giao diện (menu người dùng): vị trí menu trái / phải / ngang trên / ngang dưới, màu chủ đạo (12 màu + màu tùy chọn), menu tối / sáng, font (hệ thống hoặc font web), cỡ chữ 12–17px; xem trước ngay, lưu theo từng người dùng; Quản trị đặt giao diện mặc định toàn hệ thống |
| Song ngữ Việt – Anh | 2 lá cờ trên thanh trên cùng, trang đăng nhập và cổng NCC; lựa chọn nhớ theo trình duyệt (cookie). Đã dịch: khung trang nội bộ (menu, thanh trên, đăng nhập, Dashboard, tên trạng thái, vai trò, tiêu đề trang) và toàn bộ cổng nhà cung cấp (màn hình, thông báo, lỗi nhập liệu). Chỉ đổi ngôn ngữ hiển thị, định dạng số / tiền / ngày giữ kiểu Việt Nam; dữ liệu người dùng nhập không dịch. Từ điển: `Infrastructure/I18n.En.cs` – câu chưa dịch tự hiện tiếng Việt, dịch thêm bằng cách bọc `@L["..."]` / `I18n.T("...")` và thêm một dòng vào từ điển |
| Thiết lập | Trang tổng quan Thiết lập; **thiết lập phân quyền** (ma trận chức năng × vai trò, chỉ thu hẹp quyền mặc định, áp cả menu và truy cập trực tiếp); **quyền theo gói thầu** (Phụ trách / Thành viên / Chỉ xem – gói đã phân quyền chỉ người được giao sửa); **bộ tiêu chí đánh giá** dùng lại (sơ tuyển, HSDT năng lực / kỹ thuật, báo giá) – chèn vào đợt sơ tuyển và hồ sơ mời thầu; **tiêu chí đánh giá NCC** (tên, trọng số, hướng dẫn chấm – điểm tổng hợp có trọng số); **cảnh báo** (bật / tắt, ngưỡng 11 loại nhắc hạn); **email** (SMTP trên web, gửi thử); **mẫu thông báo** (mẫu email có mã chèn, bật / tắt kênh theo loại chứng từ); **mã tự sinh** (mẫu số chứng từ {MA}/{YYYY}/{MM}/{STT}…); **phân cấp hàng hóa** (cây nhóm hàng nhiều cấp, lọc hàng hóa gồm nhóm con); **biểu mẫu** (thư viện file mẫu, thông tin công ty + quốc hiệu trên bản in) |
| Cơ cấu tổ chức | Cây đơn vị Công ty → Khối / Chi nhánh → Phòng ban → Tổ (mã, loại, trưởng đơn vị, trung tâm chi phí); dạng cây và sơ đồ tổ chức; chi tiết đơn vị: đơn vị cấp dưới, nhân sự, số liệu gộp (đề nghị, ngân sách, đã đặt hàng), chuyển người dùng vào đơn vị. Tên đơn vị = phòng ban dùng trong toàn hệ thống (danh mục Phòng ban tự đồng bộ, đổi tên lan sang người dùng / đề nghị / ngân sách / kế hoạch / hạn mức). Luồng duyệt giữ theo phòng ban. Báo cáo lọc theo khối / chi nhánh gộp cả đơn vị cấp dưới. Lần nâng cấp đầu tự dựng cây từ các phòng ban đang dùng |
| Nhà cung cấp | Hồ sơ NCC, MST không trùng, nhóm hàng cung cấp, trạng thái (chờ thẩm định / đã duyệt / tạm ngưng / danh sách đen), điểm đánh giá, lịch sử PO và tham gia báo giá, tỷ lệ trúng thầu |
| Yêu cầu báo giá (RFQ) | Lập từ PR đã duyệt; gợi ý NCC đã phê duyệt theo nhóm hàng (sắp theo điểm); bắt buộc tối thiểu N NCC; in thư mời báo giá; nhập báo giá từng NCC (đơn giá, VAT, thời gian giao, bảo hành, thanh toán) |
| So sánh báo giá | Ma trận dòng hàng × NCC, tô xanh giá thấp nhất từng dòng, khung NCC có tổng thấp nhất, % so với dự toán; chọn NCC kèm lý do (ghi nhật ký nếu không chọn giá thấp nhất) → tự sinh PO nháp theo giá đã báo |
| Đơn mua hàng (PO) | Lập từ RFQ, từ PR hoặc trực tiếp; chặn đặt hàng NCC chưa duyệt / danh sách đen; duyệt theo ngưỡng; gửi NCC; in đơn đặt hàng; hủy (khi chưa nhận hàng); cảnh báo trễ hạn |
| Nhận hàng (GRN) | Nhận nhiều đợt, số lượng đạt / không đạt, không cho nhận vượt số đặt; tự cập nhật trạng thái PO (nhận một phần / đủ) và hoàn tất PR; in phiếu nhận |
| Đánh giá NCC | 4 tiêu chí (chất lượng, tiến độ, giá, dịch vụ) sau khi nhận đủ → cập nhật điểm trung bình NCC |
| Người dùng | 6 vai trò: Người đề nghị, Trưởng bộ phận, Ban Giám đốc, Nhân viên mua hàng, Kế toán, Quản trị; gán phòng ban (theo danh mục) |
| Chỉ định thầu / Đấu thầu rộng rãi / Hợp đồng | Hồ sơ chào giá chỉ định (căn cứ, chứng minh, cảnh báo chia nhỏ gói), **gửi yêu cầu báo giá cho NCC qua cổng, báo giá niêm phong**; danh sách báo giá NCC / báo giá chào hàng cạnh tranh / đánh giá hồ sơ chào giá; gói thầu 1 túi / 2 túi, tổ chuyên gia, làm rõ – sửa đổi HSMT, kiến nghị; **đàm phán hợp đồng** theo thứ tự xếp hạng (giá trúng thầu = giá sau đàm phán, biên bản in được); **báo cáo thẩm định kết quả LCNT** bởi người thẩm định độc lập (ký xác nhận, bắt buộc thống nhất trước khi Giám đốc duyệt); danh sách theo bước: đánh giá HSDT/HSCG, đàm phán, báo cáo đánh giá, thẩm định; hợp đồng, hợp đồng khung đặt hàng nhiều đợt; xuất dữ liệu đăng tải gói thầu |
| Báo cáo | 10 báo cáo chuẩn (chi tiêu, hình thức, tiến độ PR, giao hàng, NCC, cạnh tranh, hợp đồng, ngân sách, phân tích giá, cảnh báo rủi ro) – lọc, in, Excel; báo cáo động xuất SQL / thiết kế |

### Tính năng theo lộ trình mở rộng

| Giai đoạn | Tính năng |
|---|---|
| 1. Vận hành | **Thông báo** trong hệ thống + email (SMTP), nhắc hạn tự động · **Ngân sách** phòng ban × nhóm hàng × năm, kiểm soát khi gửi / duyệt PR · **Hạn mức duyệt** theo phòng ban, **ủy quyền** duyệt khi vắng mặt |
| 2. Cổng NCC (`/PortalAccount/Login`) | Menu: hướng dẫn nộp thầu · quản lý đơn đặt hàng (tìm, lọc trạng thái theo góc nhìn NCC, xem nhanh từng đơn) · quản lý gói thầu · **quản lý chào giá** (chào giá cạnh tranh + chỉ định) · thông báo mời thầu / mời sơ tuyển / kết quả (xem không cần đăng nhập) · đăng ký sơ tuyển · NCC tự đăng ký, hồ sơ pháp lý (xác minh, cảnh báo hết hạn) · **báo giá / HSDT online niêm phong** (mã hóa đến khi mở, biên nhận SHA-256 in được, lịch sử các lần nộp) · dự thầu: tải file HSMT, gửi đề nghị làm rõ, cảnh báo HSMT sửa đổi sau khi đã nộp, đếm ngược đóng thầu, HSDT nhiều thành phần (kỹ thuật / tài chính – gói hai túi chỉ mở file tài chính khi mở hồ sơ tài chính / bảo đảm / năng lực) có tự tính giá và kiểm tra bảo đảm dự thầu, hiệu lực HSDT trước khi nộp · biên bản mở thầu, kết quả đánh giá của mình, thông báo kết quả, kiến nghị online · xác nhận PO, báo giao hàng, gửi hóa đơn · khóa tài khoản khi sai mật khẩu nhiều lần |
| 3. Mua hàng → thanh toán | **Hóa đơn & đối chiếu 3 bên** PO – phiếu nhận – hóa đơn · đề nghị thanh toán (phạt chậm giao, khấu trừ tạm ứng) · hợp đồng nâng cao (phụ lục, lịch thanh toán, bảo lãnh) · **REST API** `/api/v1` cho ERP (API key) |
| 4. Tối ưu & tuân thủ | **Kế hoạch mua sắm năm** + gom nhu cầu · **mua theo hợp đồng khung** (catalog) · phân tích giá & cảnh báo rủi ro · **ký xác nhận** quyết định phê duyệt (mật khẩu + mã băm nội dung) · giao diện điện thoại, cài như ứng dụng |

Trang quản trị: Danh mục dùng chung, Hạn mức & ủy quyền, Tích hợp ERP (API key + tài liệu endpoint), Dữ liệu mẫu (nạp / gỡ bộ dữ liệu 15 tháng; "Nạp bổ sung luồng mở rộng" thêm sơ tuyển, ngân hàng giá, bảng giá tiêu chuẩn, kế hoạch năm, đàm phán / thẩm định LCNT, hỏi đáp HSMT, cổng NCC mẫu ncc1–4@ncc-mau.vn, hồ sơ pháp lý NCC, cơ cấu khối, nhóm hàng 2 cấp, quyền theo gói, biểu mẫu Word, **và bộ dữ liệu xây dựng theo BOQ** `MAU-…-XD-`: gói nháp đang lập BOQ 15 dòng có cơ cấu giá / chi phí chung, lợi nhuận, dự phòng và 2 phiên bản để so sánh; gói đấu thầu theo BOQ với 3 nhà thầu chào giá từng dòng – có cảnh báo giá thấp / cao bất thường –, hợp đồng thi công, tạm ứng, phụ lục khối lượng phát sinh PL01, 3 đợt nghiệm thu khối lượng (đã chi / chờ duyệt thanh toán / nháp)). **Kịch bản nghiệp vụ** (menu người dùng): 8 luồng theo bước, mỗi bước có tài khoản nên dùng và link tới chứng từ đang ở đúng bước.

Ngưỡng duyệt cấu hình trong `appsettings.json`:

```json
"Procurement": {
  "PrDirectorThreshold": 50000000,   // PR vượt mức này cần thêm Giám đốc duyệt
  "PoApprovalThreshold": 100000000,  // PO vượt mức này cần Giám đốc duyệt trước khi gửi NCC
  "MinVendorsPerRfq": 3,             // số NCC tối thiểu mời báo giá
  "MinValidQuotes": 3,               // số báo giá hợp lệ tối thiểu; ít hơn → kết quả RFQ phải trình Giám đốc duyệt
  "DefaultDeliveryAddress": "Kho công ty – Tầng 1, tòa nhà văn phòng",
  "CompetitiveQuotationMax": 5000000000,   // dự toán ≤ mức này gợi ý Chào hàng cạnh tranh, vượt → Đấu thầu rộng rãi
  "DirectPurchaseMonths": 12,              // Mua sắm trực tiếp: PO tham chiếu đặt trong vòng N tháng
  "DirectPurchaseMaxQuantityPercent": 130, // Mua sắm trực tiếp: tổng khối lượng ≤ % khối lượng PO tham chiếu
  "DirectAwardMax": 100000000,             // Chỉ định thầu: giá trị sau thương thảo tối đa (vượt chỉ khi khẩn cấp)
  "DirectAwardSplitWindowDays": 90,        // Chỉ định thầu: cộng dồn tờ trình cùng NCC / nhóm hàng trong N ngày để cảnh báo chia nhỏ gói
  "MaxAttachmentMb": 10,                   // Dung lượng tối đa mỗi file đính kèm
  "TenderMinBiddingDays": 10,              // Đấu thầu: từ phát hành HSMT đến đóng thầu tối thiểu N ngày
  "TenderAmendmentMinDays": 3,             // Đấu thầu: sửa đổi HSMT sát hạn hơn N ngày thì bắt buộc gia hạn đóng thầu
  "TenderQuestionMinDays": 3,              // Đấu thầu: nhà thầu gửi đề nghị làm rõ HSMT qua cổng chậm nhất N ngày trước đóng thầu
  "TenderAppraisalRequired": true,         // Đấu thầu: bắt buộc báo cáo thẩm định (thống nhất) trước khi Giám đốc phê duyệt kết quả LCNT
  "StandardPriceCeilingPercent": 10,       // Bảng giá tiêu chuẩn: giá trần mặc định = giá tiêu chuẩn + N%
  "TenderMinCommittee": 3,                 // Đấu thầu: số thành viên tổ chuyên gia tối thiểu
  "TenderComplaintDays": 5,                // Đấu thầu: thời gian nhà thầu kiến nghị kết quả trước khi ký hợp đồng
  "BudgetControl": "Warn",                 // Off / Warn (duyệt vượt phải ghi lý do) / Block (chặn)
  "BudgetWarnPercent": 90,                 // cảnh báo khi ngân sách đã dùng từ mức này
  "RequireSignature": true                 // quyết định duyệt / từ chối phải nhập lại mật khẩu (ký xác nhận)
},
"Notifications": {
  "IntervalMinutes": 30,                   // chu kỳ quét nhắc hạn + gửi email
  "StaleApprovalDays": 2, "ExpiryWarningDays": 30,
  "BaseUrl": "http://localhost:8110",      // link trong email
  "Email": { "Enabled": false, "Host": "", "Port": 587, "EnableSsl": true, "UserName": "", "Password": "", "From": "eprocurement@company.local" }
}
```
Hạn mức duyệt theo phòng ban (ghi đè `PrDirectorThreshold` / `PoApprovalThreshold`) cấu hình trên trang **Hạn mức & ủy quyền**.
Trong Docker: `Procurement__PoApprovalThreshold: "200000000"` trong phần `environment`.

## 2. Cấu trúc (MVC)

```
EProcurement.sln
src/EProcurement.Web/
├── Controllers/  Account, Home, Approvals, PurchaseRequests, Items, Vendors, Rfqs,
│                 PurchaseOrders, GoodsReceipts, Users  (+ ProcControllerBase)
├── Models/Entities/  AppUser, Category, Item, Vendor, VendorCategory, PurchaseRequest(+Line),
│                     Rfq, RfqLine, RfqVendor, VendorQuoteLine, PurchaseOrder(+Line),
│                     GoodsReceipt(+Line), VendorEvaluation, ApprovalHistory
├── ViewModels/  Data/ (EProcDbContext, DbSeeder, DocNumbers)  Infrastructure/ (ProcurementSettings, helpers)
├── Views/       Razor + Bootstrap 5; _LineEditor dùng chung cho PR/RFQ/PO; _History nhật ký duyệt
└── wwwroot/js/site.js   bộ soạn dòng chứng từ (thêm/xóa dòng, chọn hàng, tính tiền)
Dockerfile, docker-compose.yml (PA A), docker-compose.shared-db.yml (PA B)
deploy-eproc-shared-db.ps1 + run-eproc-B.cmd
```

Số chứng từ tự sinh: `PR-2026-0001`, `RFQ-…`, `PO-…`, `GRN-…`. Mọi thao tác duyệt / từ chối / gửi / nhận được ghi vào `ApprovalHistories`.

## 3. Chạy

**Visual Studio / dotnet:** sửa `ConnectionStrings:EProcConnection` trong `appsettings.json` → `cd src/EProcurement.Web && dotnet run`. Lần đầu tự tạo database `EPROC_DB` + dữ liệu mẫu (7 người dùng, 6 nhóm hàng, 14 mặt hàng, 8 NCC, 7 PR ở đủ trạng thái, 2 RFQ, 6 PO, 5 phiếu nhận hàng).

**Docker – dùng chung SQL Server (giống HRM, CRM):** double-click `run-eproc-B.cmd`. Script tự tìm SQL Server đang chạy, tạo login riêng `eproc_app`, chọn port trống từ 8110, build & chạy `eproc-web`.

**Docker – SQL Server riêng:** `copy .env.example .env` → `docker compose up -d --build` → `http://localhost:8110` (SQL: `localhost,1436`).

| Tài khoản (mật khẩu `Abc@123`) | Vai trò | Phòng ban |
|---|---|---|
| admin | Quản trị hệ thống | Phòng CNTT |
| director | Ban Giám đốc | Ban Giám đốc |
| approver | Trưởng bộ phận | Phòng CNTT |
| approver2 | Trưởng bộ phận | Phòng Hành chính |
| requester | Người đề nghị | Phòng CNTT |
| requester2 | Người đề nghị | Phòng Hành chính |
| buyer | Nhân viên mua hàng | Phòng Mua hàng |

**Kịch bản demo:** `requester` tạo PR laptop (> 50 triệu) → `approver` duyệt ở "Chờ tôi duyệt" → `director` duyệt → `buyer` mở PR → Lập RFQ → Nhập báo giá 3 NCC → So sánh báo giá → Chọn NCC → PO → Duyệt / Gửi NCC → Nhận hàng → Đánh giá NCC.

## 4. Ghi chú kỹ thuật

- `EnsureCreated()` cho chạy thử; môi trường thật nên dùng EF Migrations (`dotnet ef migrations add InitialCreate`, đổi sang `MigrateAsync()`).
- Tắt dữ liệu mẫu: `Database__SeedOnStartup=false`.
- Khóa ngoại `ON DELETE NO ACTION` — chứng từ không xóa, chỉ hủy.
- Tên trùng với HRM/CRM được tách: project `eproc`, container `eproc-web` / `eproc-sqlserver`, DB `EPROC_DB`, port 8110 / 1436.

- Báo giá / HSDT online được mã hóa bằng ASP.NET Data Protection; khóa lưu ở volume `/app/keys` (`DataProtection__KeysPath`) – **phải sao lưu volume này**, mất khóa thì không mở được hồ sơ đang niêm phong.
- REST API: header `X-Api-Key`, tối đa 120 request / phút / key; tài liệu trên trang Tích hợp ERP.

## 5. Kiểm thử

```
dotnet test tests/EProcurement.Tests
```
35 test cho quy tắc nghiệp vụ: gợi ý hình thức, đánh giá báo giá / thầu, đối chiếu 3 bên, phạt & khấu trừ tạm ứng, ngân sách, hạn mức duyệt, khóa tài khoản, mã băm niêm phong.

## 6. Việc còn lại

- Chuyển `EnsureCreated` + script nâng cấp (`DbSeeder.UpgradeSchemaAsync`) sang EF Migrations (cần `dotnet tool install --global dotnet-ef`, tạo migration gốc khớp schema hiện tại rồi đánh dấu đã áp dụng trên database đang chạy).
- Chữ ký số CA (USB token / ký số từ xa) thay cho ký xác nhận bằng mật khẩu; kết nối trực tiếp Hệ thống mạng đấu thầu quốc gia (hiện xuất file dữ liệu đăng tải).
