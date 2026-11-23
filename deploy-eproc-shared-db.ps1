<#
  Trien khai E-Procurement theo PHUONG AN B: dung chung SQL Server cua app Healthcare.
  Chay tai thu muc E-Procurement:   powershell -ExecutionPolicy Bypass -File .\deploy-eproc-shared-db.ps1
  Script KHONG khoi dong lai / sua database cua Healthcare. Chi them login 'eproc_app'
  (quyen dbcreator) de E-Procurement tu tao database rieng EPROC_DB.
#>
param(
    [string]$SqlContainer = "",     # bo trong de script tu tim
    [int]$WebPort = 8110
)
$ErrorActionPreference = "Continue"   # PS 5.1: tranh loi khi chuyen huong stderr cua lenh docker
Set-Location -Path $PSScriptRoot
function Info($m) { Write-Host "[EPROC] $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "[OK]  $m" -ForegroundColor Green }
function Fail($m) { Write-Host "[LỖI] $m" -ForegroundColor Red; exit 1 }

# 1. Docker
Info "Kiểm tra Docker..."
docker version --format '{{.Server.Version}}' *> $null
if ($LASTEXITCODE -ne 0) { Fail "Docker chưa chạy. Mở Docker Desktop rồi chạy lại script." }
Ok "Docker đang chạy"

# 2. Tim SQL Server cua Healthcare (doi toi da 60 giay neu Docker / container vua khoi dong)
$exclude = @('hrm-sqlserver', 'crm-sqlserver', 'eproc-sqlserver')
function Find-Sql([bool]$all) {
    if ($all) { $lines = docker ps -a --format '{{.Names}}|{{.Image}}|{{.Status}}|{{.Ports}}' }
    else { $lines = docker ps --format '{{.Names}}|{{.Image}}|{{.Status}}|{{.Ports}}' }
    # Nhận diện SQL Server theo image gốc trong cấu hình container (docker ps có thể chỉ hiện mã ID khi image đã được tải bản mới)
    @($lines | Where-Object { $_ } | Where-Object {
        $n = $_.Split('|')[0]
        if ($n -in $exclude) { return $false }
        $cfgImage = docker inspect -f '{{.Config.Image}}' $n 2>$null
        ("$_|$cfgImage" -match 'mssql|sqlserver|azure-sql-edge|1433->')
    })
}
if (-not $SqlContainer) {
    $running = @()
    for ($try = 1; $try -le 12; $try++) {
        $running = Find-Sql $false
        if ($running.Count -gt 0) { break }
        if ($try -eq 1) {
            $stopped = Find-Sql $true
            if ($stopped.Count -gt 0) {
                $name = $stopped[0].Split('|')[0]
                Write-Host "Container SQL Server '$name' đang DỪNG ($($stopped[0].Split('|')[2]))." -ForegroundColor Yellow
                $ans = Read-Host "Khởi động container '$name'? (Y/n)"
                if ($ans -eq '' -or $ans -match '^[YyCc]') { docker start $name | Out-Null; Ok "Đã gửi lệnh khởi động $name" }
            } else {
                Write-Host "Không có container SQL Server nào (kể cả đang dừng). Các container hiện có:" -ForegroundColor Yellow
                docker ps -a --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"
            }
        }
        Info "Chưa thấy SQL Server đang chạy, đợi 5 giây... ($try/12)"
        Start-Sleep -Seconds 5
    }
    $candidates = @($running | ForEach-Object { $_.Split('|')[0] })
    if ($candidates.Count -eq 0) {
        Write-Host "Danh sách container hiện có:"
        docker ps -a --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"
        Fail "Không tìm thấy container SQL Server đang chạy. Mở Docker Desktop, bấm Start container SQL của Healthcare rồi chạy lại; hoặc chỉ định tên: .\deploy-eproc-shared-db.ps1 -SqlContainer <tên container>"
    }
    if ($candidates.Count -gt 1) {
        Write-Host "Tìm thấy nhiều container SQL Server:"
        for ($i = 0; $i -lt $candidates.Count; $i++) { Write-Host "  [$i] $($candidates[$i])" }
        $idx = Read-Host "Chọn số thứ tự container SQL của Healthcare"
        $SqlContainer = $candidates[[int]$idx]
    } else { $SqlContainer = $candidates[0] }
}
Ok "SQL Server của Healthcare: $SqlContainer"

# 3. Network
$networks = @((docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' $SqlContainer).Trim().Split(' ') | Where-Object { $_ })
$network = $networks | Where-Object { $_ -notin @('bridge', 'host', 'none') } | Select-Object -First 1
if (-not $network) {
    $network = "hrm-shared"
    Info "SQL Server đang ở network mặc định 'bridge' -> tạo network '$network' và nối thêm SQL Server vào (không khởi động lại container)."
    docker network inspect $network *> $null
    if ($LASTEXITCODE -ne 0) { docker network create $network | Out-Null }
    docker network connect $network $SqlContainer 2>$null
}
Ok "Network dùng chung: $network"

# 4. Mat khau SA cua Healthcare (doc tu bien moi truong container)
$envs = docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' $SqlContainer
$saLine = $envs | Where-Object { $_ -match '^(MSSQL_SA_PASSWORD|SA_PASSWORD)=' } | Select-Object -First 1
if ($saLine) { $saPwd = $saLine.Substring($saLine.IndexOf('=') + 1); Ok "Đã đọc mật khẩu SA từ cấu hình container" }
else {
    $sec = Read-Host "Nhập mật khẩu SA của SQL Server Healthcare" -AsSecureString
    $saPwd = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}

# 5. sqlcmd trong container
docker exec $SqlContainer sh -c "test -x /opt/mssql-tools18/bin/sqlcmd" *> $null
if ($LASTEXITCODE -eq 0) { $sqlcmd = "/opt/mssql-tools18/bin/sqlcmd"; $extra = @("-C") }
else {
    docker exec $SqlContainer sh -c "test -x /opt/mssql-tools/bin/sqlcmd" *> $null
    if ($LASTEXITCODE -ne 0) { Fail "Không tìm thấy sqlcmd trong container $SqlContainer. Tạo login 'eproc_app' thủ công bằng SSMS (xem README)." }
    $sqlcmd = "/opt/mssql-tools/bin/sqlcmd"; $extra = @()
}

# 6. Tao / cap nhat login eproc_app
$chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
$rand = -join (1..12 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
$eprocPwd = "Eproc@$rand" + "9x"
$q = "IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'eproc_app') CREATE LOGIN eproc_app WITH PASSWORD = '$eprocPwd', CHECK_POLICY = ON " +
     "ELSE ALTER LOGIN eproc_app WITH PASSWORD = '$eprocPwd'; ALTER SERVER ROLE dbcreator ADD MEMBER eproc_app; " +
     "IF DB_ID('EPROC_DB') IS NOT NULL EXEC('USE EPROC_DB; IF USER_ID(''eproc_app'') IS NULL CREATE USER eproc_app FOR LOGIN eproc_app; ALTER ROLE db_owner ADD MEMBER eproc_app;');"
for ($try = 1; $try -le 12; $try++) {
    docker exec $SqlContainer $sqlcmd -S localhost -U sa -P $saPwd @extra -b -Q $q
    if ($LASTEXITCODE -eq 0) { break }
    if ($try -lt 12) { Info "SQL Server chưa sẵn sàng, thử lại sau 5 giây... ($try/12)"; Start-Sleep -Seconds 5 }
}
if ($LASTEXITCODE -ne 0) { Fail "Không tạo được login eproc_app (sai mật khẩu SA?)." }
Ok "Login 'eproc_app' sẵn sàng (chỉ có quyền tạo database, không truy cập DB của Healthcare)"

# 7. Port web con trong
function PortFree($p) {
    try { $l = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, $p); $l.Start(); $l.Stop(); return $true } catch { return $false }
}
$running = docker ps --filter "name=^eproc-web$" -q
if (-not $running) { while (-not (PortFree $WebPort)) { $WebPort++ } }
Ok "Port web E-Procurement: $WebPort"

# 8. Ghi .env (UTF-8 khong BOM)
$envText = @"
EPROC_WEB_PORT=$WebPort
EPROC_DB_PORT=1436
EPROC_SA_PASSWORD=NotUsedInPlanB@1
SHARED_NETWORK=$network
SHARED_DB_HOST=$SqlContainer
SHARED_DB_USER=eproc_app
SHARED_DB_PASSWORD=$eprocPwd
"@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot ".env"), $envText.Replace("`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
Ok "Đã ghi file .env"

# 9. Build & chay
Info "Build và khởi động eproc-web (lần đầu mất vài phút)..."
docker compose -f docker-compose.shared-db.yml up -d --build
if ($LASTEXITCODE -ne 0) { Fail "docker compose lỗi. Xem thông báo phía trên." }

# 10. Cho web san sang
Info "Đợi ứng dụng khởi tạo database và dữ liệu mẫu..."
$url = "http://localhost:$WebPort/Account/Login"
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 5
    try { $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5; if ($r.StatusCode -eq 200) { $ready = $true; break } } catch { }
}
if (-not $ready) {
    docker compose -f docker-compose.shared-db.yml logs --tail 40 eproc-web
    Fail "Ứng dụng chưa phản hồi sau 5 phút. Xem log phía trên."
}
Ok "E-Procurement đã chạy: http://localhost:$WebPort  (admin / Abc@123)"
docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
Start-Process "http://localhost:$WebPort"
