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
| Quản lý nguồn cung | **Sơ tuyển NCC** theo nhóm hàng (tiêu chí bắt buộc + chấm điểm, NCC nộp hồ sơ trên cổng, chấm, Giám đốc ký duyệt; NCC đạt được ưu tiên mời chào giá, NCC chờ thẩm định chuyển Đã duyệt) + danh sách NCC tự đăng ký chờ thẩm định · **Đánh giá NCC**: bảng xếp loại A/B/C/D từ phiếu đánh giá + % đúng hạn + % hàng không đạt, đánh giá định kỳ theo quý có gợi ý điểm · **Ngân hàng giá**: đơn giá từ đơn hàng, báo giá, HSDT, chỉ định, hợp đồng và giá khảo sát nhập tay · **Bảng giá tiêu chuẩn**: giá chuẩn + giá trần theo thời kỳ, đề xuất từ ngân hàng giá, Giám đốc ký duyệt, thay giá tham khảo; PO vượt giá trần bắt buộc lý do + Giám đốc duyệt · 3 báo cáo nguồn cung |
| Chờ tôi duyệt | Hộp duyệt tập trung: Trưởng bộ phận duyệt PR phòng mình; Giám đốc duyệt PR & PO vượt ngưỡng ngay trên danh sách |
| Danh mục hàng hóa | Mã, nhóm hàng, đơn vị, giá tham khảo, quy cách, thương hiệu, xuất xứ |
| Danh mục dùng chung | 16 loại: phòng ban, đơn vị tính, điều khoản thanh toán, loại hợp đồng, địa điểm giao hàng, ngân hàng, thời hạn bảo hành, đơn vị tiền tệ, nguồn vốn, loại đơn hàng, tổ triển khai, quyết định (chèn nhanh làm căn cứ KHLCNT / bảng giá), lĩnh vực kinh doanh (NCC), xếp loại NCC (thang điểm dùng cho Đánh giá NCC), xuất xứ, thương hiệu; kèm lối tắt tới hàng hóa, nhóm hàng, NCC, nhân viên; đổi tên một giá trị thì dữ liệu gốc đang dùng tên cũ cập nhật theo |
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

Trang quản trị: Danh mục dùng chung, Hạn mức & ủy quyền, Tích hợp ERP (API key + tài liệu endpoint), Dữ liệu mẫu (nạp / gỡ bộ dữ liệu 15 tháng).

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
