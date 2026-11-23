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
| Người dùng | 5 vai trò: Người đề nghị, Trưởng bộ phận, Ban Giám đốc, Nhân viên mua hàng, Quản trị; gán phòng ban |

Ngưỡng duyệt cấu hình trong `appsettings.json`:

```json
"Procurement": {
  "PrDirectorThreshold": 50000000,   // PR vượt mức này cần thêm Giám đốc duyệt
  "PoApprovalThreshold": 100000000,  // PO vượt mức này cần Giám đốc duyệt trước khi gửi NCC
  "MinVendorsPerRfq": 3,             // số NCC tối thiểu mời báo giá
  "DefaultDeliveryAddress": "Kho công ty – Tầng 1, tòa nhà văn phòng"
}
```
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

## 5. Hướng mở rộng

Cổng NCC tự nộp báo giá (vendor portal), đấu thầu kín / mở theo thời điểm, ngân sách theo phòng ban, hợp đồng khung, đối chiếu 3 bên PO – GRN – hóa đơn, email thông báo duyệt, đính kèm file báo giá, tích hợp kế toán / ERP và kho.
