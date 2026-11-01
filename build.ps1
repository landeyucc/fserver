<#
.SYNOPSIS
    FServer FTP 项目完整编译 / 发布脚本（Release 正式版）。

.DESCRIPTION
    顺序执行：
      1) 清理残留的 FServer.App.exe 僵尸进程   （避免 MSB3027 无法复制）
      2) (可选) 清理 cargo target / bin / obj
      3) Rust ftp_core —— cargo build --release
      4) .NET WPF      —— dotnet publish Release x64
      5) 验证产物关键文件存在
      6) （可选）把产物 + ftp_core.dll 汇总到 publish\ 目录

.PARAMETER Configuration
    构建配置：Release（默认，正式版）或 Debug。

.PARAMETER Clean
    先清 bin / obj / cargo target，再全量编译。

.PARAMETER SkipRust
    跳过 cargo build（假设 ftp_core.dll 已是最新）。

.PARAMETER SkipDotnet
    只编 Rust，不编 .NET。

.PARAMETER NoPublish
    只 dotnet build，不 publish 到独立目录。默认 dotnet publish 出到
    publish\FServer.App\{Configuration}\ 下，便于打包分发。

.EXAMPLE
    .\build.ps1                          # 默认 Release 完整编译 + publish
    .\build.ps1 -Clean                   # 全量重建（cargo clean + dotnet clean + publish）
    .\build.ps1 -Configuration Debug     # Debug 版
    .\build.ps1 -SkipDotnet              # 只编 Rust dll
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Clean,
    [switch]$SkipRust,
    [switch]$SkipDotnet,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version Latest
# 注意：NativeCommandError（cargo/dotnet 往 stderr 打 warning）不中断，
# 真正的失败由 Invoke-Checked 里的 $LASTEXITCODE 判定

# ───────────────────── 工具函数 ─────────────────────

function Write-Step {
    param([string]$Text)
    Write-Host ""
    Write-Host "==== $Text ====" -ForegroundColor Cyan
}

function Write-Ok([string]$Text)    { Write-Host "[OK]     $Text" -ForegroundColor Green }
function Write-Warn([string]$Text)  { Write-Host "[WARN]   $Text" -ForegroundColor Yellow }
function Write-Fail([string]$Text)  { Write-Host "[FAIL]   $Text" -ForegroundColor Red   }
function Write-Info([string]$Text)  { Write-Host "[INFO]   $Text" -ForegroundColor Gray  }

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [scriptblock]$Script,
        [Parameter(Mandatory)] [string]     $Desc
    )
    # 2>&1 把 stderr 并入 stdout，让 cargo/dotnet 的 warning 不再触发 NativeCommandError
    & $Script 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "$Desc (exit code $LASTEXITCODE)"
        throw "构建中断：$Desc 失败"
    }
    Write-Ok $Desc
}

# ───────────────────── 路径 ─────────────────────

$Root      = $PSScriptRoot
$RustDir   = Join-Path $Root 'rust\ftp_core'
$Csproj    = Join-Path $Root 'src\FServer.App\FServer.App.csproj'
$Solution  = Join-Path $Root 'FServer.slnx'
$Target    = 'x86_64-pc-windows-msvc'

$FtpCoreDll = Join-Path $RustDir "target\$Target\$Configuration\ftp_core.dll"

$PublishRoot = Join-Path $Root 'publish'
$PublishDir  = Join-Path $PublishRoot "FServer.App\$Configuration"

Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "  FServer FTP Build Script" -ForegroundColor Cyan
Write-Host "  Configuration : $Configuration" -ForegroundColor Gray
Write-Host "  Clean         : $Clean"         -ForegroundColor Gray
Write-Host "  SkipRust      : $SkipRust"      -ForegroundColor Gray
Write-Host "  SkipDotnet    : $SkipDotnet"    -ForegroundColor Gray
Write-Host "  NoPublish     : $NoPublish"     -ForegroundColor Gray
Write-Host "  Root          : $Root"          -ForegroundColor Gray
Write-Host "==============================================" -ForegroundColor Cyan

# ───────────────────── 1. 环境检查 ─────────────────────

Write-Step "检查环境"

$cargoOk = [bool](Get-Command cargo -ErrorAction SilentlyContinue)
$dotnetOk = [bool](Get-Command dotnet -ErrorAction SilentlyContinue)

Write-Host "  cargo : $(cargo --version 2>$null)"   -ForegroundColor Gray
Write-Host "  dotnet: $(dotnet --version 2>$null)" -ForegroundColor Gray
if (-not $cargoOk)  { Write-Fail "未找到 cargo，请先安装 Rust 工具链"; exit 1 }
if (-not $dotnetOk) { Write-Fail "未找到 dotnet，请先安装 .NET SDK (net48 需要 .NET Framework Developer Pack + .NET SDK)"; exit 1 }

if (-not (Test-Path $Csproj))    { Write-Fail "找不到 csproj: $Csproj";      exit 1 }
if (-not (Test-Path $RustDir))   { Write-Fail "找不到 rust 目录: $RustDir";  exit 1 }

# ───────────────────── 2. 清僵尸进程 ─────────────────────

Write-Step "清理残留进程 & 旧产物"

$proc = Get-Process FServer.App -ErrorAction SilentlyContinue
if ($proc) {
    $proc | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Write-Ok "已停止 FServer.App 进程 (PID $($proc.Id -join ', '))"
} else {
    Write-Info "无残留 FServer.App 进程"
}

$dotnetLock = Get-Process FServer.App -ErrorAction SilentlyContinue
if ($dotnetLock) {
    Write-Fail "FServer.App.exe 仍在运行，无法继续"
    exit 1
}

# ───────────────────── 3. Clean（可选） ─────────────────────

if ($Clean) {
    Write-Step "Clean 清理"

    if (Test-Path (Join-Path $RustDir 'target')) {
        Push-Location $RustDir
        Invoke-Checked -Desc "cargo clean" -Script { cargo clean }
        Pop-Location
    } else {
        Write-Info "cargo target 不存在，跳过"
    }

    Push-Location $Root
    Invoke-Checked -Desc "dotnet clean" -Script { dotnet clean $Solution -c $Configuration -v minimal }
    Pop-Location
}

# ───────────────────── 4. Rust ftp_core ─────────────────────

if (-not $SkipRust) {
    Write-Step "Rust ftp_core ($Configuration)"

    Push-Location $RustDir
    if ($Configuration -eq 'Release') {
        Invoke-Checked -Desc "cargo build --release --target $Target" `
            -Script { cargo build --release --target $Target }
    } else {
        Invoke-Checked -Desc "cargo build --target $Target (debug)" `
            -Script { cargo build --target $Target }
    }
    Pop-Location

    if (-not (Test-Path $FtpCoreDll)) {
        Write-Fail "预期产物不存在: $FtpCoreDll"
        exit 1
    }
    $size = (Get-Item $FtpCoreDll).Length
    Write-Ok "ftp_core.dll  $([math]::Round($size/1KB,1)) KB  ->  $FtpCoreDll"
} else {
    Write-Info "SkipRust — 跳过 cargo build"
    if (-not (Test-Path $FtpCoreDll)) {
        Write-Warn "SkipRust 但 ftp_core.dll 不存在: $FtpCoreDll （可能 dotnet 会因缺少 DLL 报 MSB3030）"
    }
}

# ───────────────────── 5. .NET WPF ─────────────────────

if (-not $SkipDotnet) {
    Write-Step ".NET FServer.App ($Configuration, x64)"

    Push-Location $Root

    if (-not $NoPublish) {
        # dotnet publish 到独立目录；PlatformTarget=x64 已在 csproj
        if (Test-Path $PublishDir) {
            Remove-Item -Recurse -Force $PublishDir
            Write-Info "已清理旧 publish 目录"
        }
        New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

        Invoke-Checked -Desc "dotnet publish -> $PublishDir" `
            -Script { dotnet publish $Csproj `
                -c $Configuration `
                -p:PlatformTarget=x64 `
                -p:Prefer32Bit=false `
                -o $PublishDir `
                -v minimal }
    } else {
        Invoke-Checked -Desc "dotnet build" `
            -Script { dotnet build $Csproj -c $Configuration -v minimal }
    }

    Pop-Location

    # 关键文件验证
    $exe = Join-Path $PublishDir 'FServer.App.exe'
    $dll = Join-Path $PublishDir 'ftp_core.dll'
    $ok = $true
    foreach ($f in @($exe, $dll)) {
        if (Test-Path $f) {
            $size = (Get-Item $f).Length
            Write-Ok "$([IO.Path]::GetFileName($f))  $([math]::Round($size/1KB,1)) KB  ->  $f"
        } else {
            Write-Fail "缺少关键产物: $f"
            $ok = $false
        }
    }
    if (-not $ok) { exit 1 }
} else {
    Write-Info "SkipDotnet — 跳过 dotnet 编译 / 发布"
}

# ───────────────────── 6. 总结 ─────────────────────

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host "  构建完成 ✔" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green

if (-not $SkipDotnet -and -not $NoPublish) {
    Write-Host "  正式版目录 : $PublishDir" -ForegroundColor White
    Write-Host "  可分发文件 : FServer.App.exe + ftp_core.dll + 所有依赖 dll" -ForegroundColor Gray
    Write-Host "  运行前提   : 目标机器已安装 .NET Framework 4.8 x64" -ForegroundColor Gray
    Write-Host "  首次运行   : fserver.db 会自动在 exe 同目录创建" -ForegroundColor Gray
}

Write-Host ""
