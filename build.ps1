<#
.SYNOPSIS
    FServer FTP 完整编译 / 发布 / 打包脚本。

.DETAILS
    顺序执行：
      1) 清理残留 FServer.App.exe（避免 MSB3027 无法复制）
      2) (可选) cargo clean + dotnet clean
      3) Rust ftp_core —— cargo build
      4) .NET WPF      —— dotnet publish（自动带上 FServer.Icons.dll）
      5) 验证关键产物（exe / Icons.dll / ftp_core.dll）
      6) (默认) 剔除 .pdb → Compress-Archive 生成 zip

.PARAMETER Configuration
    Release（默认）或 Debug。与 -Dev 互斥。

.PARAMETER Dev
    等价 -Configuration Debug。

.PARAMETER Clean
    先全量清理（cargo clean + dotnet clean）。

.PARAMETER SkipRust
    跳过 cargo build（假设 ftp_core.dll 已是最新）。

.PARAMETER SkipDotnet
    只编 Rust，不编 .NET。

.PARAMETER NoZip
    不生成最终 zip（只 publish 目录）。

.PARAMETER ZipOnly
    跳过编译，只对现有 publish 目录生成 zip。

.EXAMPLE
    .\build.ps1                  # Release 全量 + zip
    .\build.ps1 -Dev             # Debug 开发版
    .\build.ps1 -NoZip           # Release + publish，但不打包 zip
    .\build.ps1 -SkipRust        # 只用已有 ftp_core.dll，编 .NET + zip
    .\build.ps1 -ZipOnly         # 不编译，仅 zip 现有 publish 目录
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Dev,
    [switch]$Clean,
    [switch]$SkipRust,
    [switch]$SkipDotnet,
    [switch]$NoZip,
    [switch]$ZipOnly
)

if ($Dev.IsPresent) { $Configuration = 'Debug' }

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version Latest

# ─── 工具函数 ───

function Write-Step([string]$Text)        { Write-Host "`n==== $Text ====" -ForegroundColor Cyan }
function Write-Ok([string]$Text)          { Write-Host "[OK]     $Text"   -ForegroundColor Green }
function Write-Warn([string]$Text)         { Write-Host "[WARN]   $Text"   -ForegroundColor Yellow }
function Write-Fail([string]$Text)         { Write-Host "[FAIL]   $Text"   -ForegroundColor Red   }
function Write-Info([string]$Text)         { Write-Host "[INFO]   $Text"   -ForegroundColor Gray  }

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [scriptblock]$Script,
        [Parameter(Mandatory)] [string]     $Desc
    )
    & $Script 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "$Desc (exit $LASTEXITCODE)"; throw "构建中断：$Desc 失败"
    }
    Write-Ok $Desc
}

function Show-FileInfo([string]$Path) {
    if (Test-Path $Path) {
        $size = (Get-Item $Path).Length
        Write-Ok "$([IO.Path]::GetFileName($Path))  $([math]::Round($size/1KB,1)) KB  ->  $Path"
        return $true
    } else {
        Write-Fail "缺少产物: $Path"; return $false
    }
}

function DoZip {
    Write-Step "打包 zip"
    Remove-Item -Recurse -Force $ZipStage -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $ZipStage | Out-Null

    # 剔除 .pdb 调试符号（用户不需要）
    Get-ChildItem $PublishDir -File | Where-Object { $_.Extension -ne '.pdb' } |
        Copy-Item -Destination $ZipStage

    Remove-Item -Force $ZipFile -ErrorAction SilentlyContinue
    Compress-Archive -Path "$ZipStage\*" -DestinationPath $ZipFile -CompressionLevel Optimal

    $size = [math]::Round((Get-Item $ZipFile).Length / 1KB, 1)
    Write-Ok "ZIP -> $ZipFile ($size KB)"
    Remove-Item -Recurse -Force $ZipStage

    # Top 大文件概览
    Write-Host ""
    Write-Host "  Top 大文件:" -ForegroundColor Gray
    Get-ChildItem $PublishDir -File | Where-Object { $_.Extension -ne '.pdb' } |
        Sort-Object Length -Descending | Select-Object -First 6 |
        ForEach-Object { Write-Host "    $($_.Name.PadRight(40))  $([math]::Round($_.Length/1KB,1)) KB" -ForegroundColor Gray }
}

# ─── 路径 ───

$Root       = $PSScriptRoot
$RustDir    = Join-Path $Root 'rust\ftp_core'
$Csproj     = Join-Path $Root 'src\FServer.App\FServer.App.csproj'
$Solution   = Join-Path $Root 'FServer.slnx'
$Target     = 'x86_64-pc-windows-msvc'

$FtpCoreDll = Join-Path $RustDir "target\$Target\$Configuration\ftp_core.dll"

$PublishDir = Join-Path $Root "publish\FServer.App\$Configuration"
$ZipStage   = Join-Path $Root 'publish\_zip_stage'
$ZipFile    = Join-Path $Root "publish\FServer.FTP.$Configuration.zip"

Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "  FServer FTP Build Script" -ForegroundColor Cyan
Write-Host "  Config  : $Configuration" -ForegroundColor Gray
Write-Host "  Clean   : $Clean"    -ForegroundColor Gray
Write-Host "  SkipZip : $NoZip"    -ForegroundColor Gray
Write-Host "  ZipOnly : $ZipOnly"  -ForegroundColor Gray
Write-Host "==============================================" -ForegroundColor Cyan

# ─── 预检查：ZipOnly 模式 ───

if ($ZipOnly) {
    Write-Info "ZipOnly — 跳过编译，直接打包现有 $PublishDir"
    if (-not (Test-Path $PublishDir)) { Write-Fail "找不到 publish 目录: $PublishDir"; exit 1 }
    DoZip; exit 0
}

# ─── 1. 环境检查 ───

Write-Step "检查环境"
$cargoOk  = [bool](Get-Command cargo  -ErrorAction SilentlyContinue)
$dotnetOk = [bool](Get-Command dotnet -ErrorAction SilentlyContinue)
Write-Host "  cargo : $(cargo --version 2>$null)"   -ForegroundColor Gray
Write-Host "  dotnet: $(dotnet --version 2>$null)" -ForegroundColor Gray
if (-not $cargoOk)  { Write-Fail "未找到 cargo"; exit 1 }
if (-not $dotnetOk) { Write-Fail "未找到 dotnet"; exit 1 }
foreach ($p in @($Csproj, $RustDir)) {
    if (-not (Test-Path $p)) { Write-Fail "找不到: $p"; exit 1 }
}

# ─── 2. 清僵尸进程 ───

Write-Step "清理残留进程"
$proc = Get-Process FServer.App -ErrorAction SilentlyContinue
if ($proc) {
    $proc | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Write-Ok "已停止进程 (PID $($proc.Id -join ', '))"
} else { Write-Info "无残留进程" }
if (Get-Process FServer.App -ErrorAction SilentlyContinue) {
    Write-Fail "进程仍在运行"; exit 1
}

# ─── 3. Clean ───

if ($Clean) {
    Write-Step "Clean"
    if (Test-Path (Join-Path $RustDir 'target')) {
        Push-Location $RustDir; Invoke-Checked -Desc "cargo clean" -Script { cargo clean }; Pop-Location
    }
    Push-Location $Root; Invoke-Checked -Desc "dotnet clean" -Script { dotnet clean $Solution -c $Configuration -v minimal }; Pop-Location
}

# ─── 4. Rust ftp_core ───

if (-not $SkipRust) {
    Write-Step "Rust ftp_core ($Configuration)"
    Push-Location $RustDir
    if ($Configuration -eq 'Release') {
        Invoke-Checked -Desc "cargo build --release --target $Target" -Script { cargo build --release --target $Target }
    } else {
        Invoke-Checked -Desc "cargo build --target $Target (debug)" -Script { cargo build --target $Target }
    }
    Pop-Location
    Show-FileInfo $FtpCoreDll | Out-Null
} else {
    Write-Info "SkipRust — 跳过 cargo build"
    if (-not (Test-Path $FtpCoreDll)) { Write-Warn "SkipRust 但 $FtpCoreDll 不存在" }
}

# ─── 5. .NET ───

if (-not $SkipDotnet) {
    Write-Step ".NET (Icons + App)"

    Push-Location $Root
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir; Write-Info "已清理旧 publish 目录" }
    New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

    Invoke-Checked -Desc "dotnet publish -> $PublishDir" -Script {
        dotnet publish $Csproj -c $Configuration -o $PublishDir -v minimal
    }
    Pop-Location

    # 关键产物验证：exe / Icons.dll / ftp_core.dll
    $ok = $true
    foreach ($f in @(
        (Join-Path $PublishDir 'FServer.App.exe'),
        (Join-Path $PublishDir 'FServer.Icons.dll'),
        (Join-Path $PublishDir 'ftp_core.dll')
    )) { if (-not (Show-FileInfo $f)) { $ok = $false } }
    if (-not $ok) { exit 1 }
} else {
    Write-Info "SkipDotnet — 跳过 .NET 编译"
}

# ─── 6. 总结 + Zip ───

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host "  构建完成 ✔" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
Write-Host "  PublishDir : $PublishDir" -ForegroundColor White

if (-not $NoZip -and -not $SkipDotnet) {
    DoZip
} else {
    Write-Host "  Zip       : 跳过（NoZip=$NoZip / SkipDotnet=$SkipDotnet）" -ForegroundColor Gray
}

Write-Host ""
