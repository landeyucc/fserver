; ============================================================================
;  FServer FTP (C# / WPF / .NET Framework 4.8) Inno Setup 安装脚本
;
;  兼容 Inno Setup 5.x（含 5.9.x）与 Inno Setup 6.x。
;
;  编译方式：
;    ISCC.exe setup-FServer.iss
;    ISCC.exe /DAppVersion=1.2.0 setup-FServer.iss
;    ISCC.exe /DAppDir="H:\GitNew\fserver\publish\FServer.App\Release" setup-FServer.iss
;    ISCC.exe /DOutputDir="H:\GitNew\fserver\release" setup-FServer.iss
;
;  安装特性：
;    - 默认装到 {autopf}（用户级，无需管理员；单用户使用的 FTP 服务无需跨机器共享）
;    - 首次运行静默启动 + 托盘后台，开机自启动用户可在 UI 开关
;    - 自带 .NET Framework 4.8 前置检测（Release >= 528040）
;    - 中文 / 英文双语
;    - 干净卸载：不留 ftp_core.dll 等原生文件；用户数据（fserver.db）提示保留
; ============================================================================

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif

; =============================================================
;  路径锚点
;   SourceDir=脚本所在目录  → ISCC 自动定位，不依赖工作目录
;   {scriptdir}\..\..     = SourceDir 自身  (ISCC 关键字)
;   ../../    = 从 script/ 上溯到仓库根
; =============================================================

#ifndef AppDir
  #define AppDir "{scriptdir}\..\..\publish\FServer.App\Release"
#endif

#ifndef OutputDir
  #define OutputDir "{scriptdir}\..\..\release"
#endif

#define MyAppName    "FServer FTP"
#define MyAppExeName "FServer.App.exe"
#define MyAppId      "{{D2DCC467-244C-403A-89CF-DAAA89B90D61}}"

[Setup]
; 固定 AppId，升级覆盖同一产品
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher=landeyucc

#if VER >= EncodeVer(6,0,0)
DefaultDirName={autopf}\FServer FTP
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
DefaultDirName={pf}\FServer FTP
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
#endif

; 升级沿用旧目录
UsePreviousAppDir=yes
DefaultGroupName={#MyAppName}

; FTP 服务监听端口 2121 / 被动端口 49152-65535（动态）
; 用户级安装默认不需要管理员；允许改目录 / 开始菜单
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=6.1sp1

OutputDir={#OutputDir}
OutputBaseFilename=FServer-FTP-setup-{#AppVersion}
SetupIconFile={scriptdir}\..\..\src\FServer.App\ftp.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#AppVersion}

Compression=lzma
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto

; 首页横幅（Modern 风格，WizardImageFile 164x314，WizardSmallImageFile 55x58，BMP 24 位）
; 没素材时可注释掉这两行（向导会显示默认灰底 + 产品名）
WizardImageFile=H:\Gitweb\Real-ESRGAN-GUI\exe\WizModernImage-tta.bmp
WizardSmallImageFile=H:\Gitweb\Real-ESRGAN-GUI\exe\WizModernSmallImage.bmp
WizardImageStretch=no
DisableWelcomePage=no

VersionInfoVersion=1.0
VersionInfoTextVersion=1.0
VersionInfoProductVersion=1.0
AppPublisherURL=https://fserver.coldsea.vip
AppSupportURL=https://coldsea.vip
AppUpdatesURL=https://fserver.coldsea.vip

[Languages]
#if VER >= EncodeVer(6,0,0)
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#else
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimp.isl"
#endif
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
BeveledLabel=Developed by Lande
WelcomeLabel1=欢迎安装 FServer FTP
WelcomeLabel2=本向导将引导你完成 FServer FTP 的安装。%n%n安装内容：WPF 控制面板 + Rust ftp_core 协议栈。%n%n运行前提：已安装 .NET Framework 4.8（Win10 1903+ 默认自带）。%n%n程序默认端口 2121。首次启动后在 UI 中点击「启动 FTP」即可开始服务。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; publish 目录整包安装（所有 dll + exe + config；.pdb 由构建脚本剔除）
Source: "{#AppDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,*.vshost.*,fserver.db"


[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; \
    Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent
; 注意：安装器是 32-bit，lowest 权限，直接 Run 即可；不需要 runas
; 升级若之前开着 FServer.App.exe，PrepareToInstall 里已先 kill 掉


[Code]
function TrimDir(const S: String): String;
begin
  Result := Trim(S);
  while (Length(Result) > 3) and (Result[Length(Result)] = '\') do
    Result := Copy(Result, 1, Length(Result) - 1);
end;

{ .NET Framework 4.8 是否已安装（Release >= 528040；Win10 1903+ 自带）}
function IsDotNet48Installed: Boolean;
var
  Release: Cardinal;
begin
  Release := 0;
  Result := RegQueryDWordValue(HKLM,
      'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)
    and (Release >= 528040);
end;

function InitializeSetup: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if not IsDotNet48Installed then
  begin
    if MsgBox('未检测到 .NET Framework 4.8，程序无法启动。' + #13#10#13#10 +
              '是否打开微软官方下载页面？', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet-framework/net48',
                '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    Result := False;
    Exit;
  end;
end;

procedure InitializeWizard;
begin
  with WizardForm do
  begin
    WelcomeLabel1.Font.Name := 'Microsoft YaHei';
    WelcomeLabel1.Font.Size := 16;
    WelcomeLabel1.Font.Style := [fsBold];

    WelcomeLabel2.Font.Name := 'Microsoft YaHei';
    WelcomeLabel2.Font.Size := 9;
  end;
end;

{ 安装前：若旧版在运行，先静默 kill，避免文件被占 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Exec('taskkill.exe', '/F /IM {#MyAppExeName} /T', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ 升级后：若有旧卸载条目（不同 AppId），顺手清理 }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { 已由 UsePreviousAppDir + 相同 AppId 处理升级覆盖，此处无需再管 }
  end;
end;

{ 卸载前：先关闭程序 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('taskkill.exe', '/F /IM {#MyAppExeName} /T', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

{ 卸载完成后：询问是否保留用户数据 fserver.db }
function UninstallNeedRestart: Boolean;
var
  DbPath: String;
  Res: Integer;
begin
  DbPath := ExpandConstant('{app}\fserver.db');
  Result := False;
  if FileExists(DbPath) then
  begin
    Res := MsgBox('检测到用户数据文件 fserver.db。' + #13#10#13#10 +
                  '是否保留？（若以后重装 FServer FTP，原配置和用户列表可恢复）',
                  mbConfirmation, MB_YESNO);
    if Res = IDNO then
      DeleteFile(DbPath);
  end;
end;
