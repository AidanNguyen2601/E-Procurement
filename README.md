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
| Chờ tôi duyệt | Hộp duyệt tập trung: Trưởng bộ phận duyệt PR phòng mình; Giám đốc duyệt PR & PO vượt ngưỡng ngay trên danh sách |
| Danh mục hàng hóa | Mã, nhóm hàng, đơn vị, giá tham khảo, quy cách |
| Nhà cung cấp | Hồ sơ NCC, MST không trùng, nhóm hàng cung cấp, trạng thái (chờ thẩm định / đã duyệt / tạm ngưng / danh sách đen), điểm đánh giá, lịch sử PO và tham gia báo giá, tỷ lệ trúng thầu |
| Yêu cầu báo giá (RFQ) | Lập từ PR đã duyệt; gợi ý NCC đã phê duyệt theo nhóm hàng (sắp theo điểm); bắt buộc tối thiểu N NCC; in thư mời báo giá; nhập báo giá từng NCC (đơn giá, VAT, thời gian giao, bảo hành, thanh toán) |
| So sánh báo giá | Ma trận dòng hàng × NCC, tô xanh giá thấp nhất từng dòng, khung NCC có tổng thấp nhất, % so với dự toán; chọn NCC kèm lý do (ghi nhật ký nếu không chọn giá thấp nhất) → tự sinh PO nháp theo giá đã báo |
| Đơn mua hàng (PO) | Lập từ RFQ, từ PR hoặc trực tiếp; chặn đặt hàng NCC chưa duyệt / danh sách đen; duyệt theo ngưỡng; gửi NCC; in đơn đặt hàng; hủy (khi chưa nhận hàng); cảnh báo trễ hạn |
| Nhận hàng (GRN) | Nhận nhiều đợt, số lượng đạt / không đạt, không cho nhận vượt số đặt; tự cập nhật trạng thái PO (nhận một phần / đủ) và hoàn tất PR; in phiếu nhận |
| Đánh giá NCC | 4 tiêu chí (chất lượng, tiến độ, giá, dịch vụ) sau khi nhận đủ → cập nhật điểm trung bình NCC |
| Người dùng | 6 vai trò: Người đề nghị, Trưởng bộ phận, Ban Giám đốc, Nhân viên mua hàng, Kế toán, Quản trị; gán phòng ban (theo danh mục) |
| Chỉ định thầu / Đấu thầu rộng rãi / Hợp đồng | Tờ trình chỉ định (căn cứ, chứng minh, cảnh báo chia nhỏ gói); gói thầu 1 túi / 2 túi, tổ chuyên gia, làm rõ – sửa đổi HSMT, kiến nghị; hợp đồng, hợp đồng khung đặt hàng nhiều đợt; xuất dữ liệu đăng tải gói thầu |
| Báo cáo | 10 báo cáo chuẩn (chi tiêu, hình thức, tiến độ PR, giao hàng, NCC, cạnh tranh, hợp đồng, ngân sách, phân tích giá, cảnh báo rủi ro) – lọc, in, Excel; báo cáo động xuất SQL / thiết kế |

### Tính năng theo lộ trình mở rộng

| Giai đoạn | Tính năng |
|---|---|
| 1. Vận hành | **Thông báo** trong hệ thống + email (SMTP), nhắc hạn tự động · **Ngân sách** phòng ban × nhóm hàng × năm, kiểm soát khi gửi / duyệt PR · **Hạn mức duyệt** theo phòng ban, **ủy quyền** duyệt khi vắng mặt |
| 2. Cổng NCC (`/PortalAccount/Login`) | NCC tự đăng ký, hồ sơ pháp lý (xác minh, cảnh báo hết hạn) · **báo giá / HSDT online niêm phong** (mã hóa đến khi mở, biên nhận SHA-256) · xác nhận PO, báo giao hàng, gửi hóa đơn · khóa tài khoản khi sai mật khẩu nhiều lần |
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
