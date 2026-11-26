<#
  大肥鱼 · 强制删除工具 —— 构建脚本
  用 Windows 自带的 csc.exe，把 src\ 下的脚本、界面、图标、背景图打包成一个 exe。
  用法：powershell -ExecutionPolicy Bypass -File build.ps1
  产物：dist\大肥鱼-强制删除工具.exe（单文件，Win10 / 11 可直接双击运行）
#>
param(
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$dist = if ($OutDir) { $OutDir } else { Join-Path $root 'dist' }
$outExe = Join-Path $dist '大肥鱼-强制删除工具.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw "找不到 C# 编译器：$csc" }

$null = New-Item -ItemType Directory -Path $dist -Force
if (Test-Path -LiteralPath $outExe) { Remove-Item -LiteralPath $outExe -Force }

# 源文件自带 UTF-8 BOM；没有 BOM 时 PowerShell 5.1 会把中文按 GBK 读坏
$ps1 = Join-Path $src 'DeepSeek.ps1'
$head = [System.IO.File]::ReadAllBytes($ps1)
if ($head.Length -lt 3 -or $head[0] -ne 0xEF -or $head[1] -ne 0xBB -or $head[2] -ne 0xBF) {
    throw 'DeepSeek.ps1 缺少 UTF-8 BOM，直接运行脚本时中文会乱码，请先修正编码'
}

$argList = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    "/out:$outExe"
    "/win32icon:$(Join-Path $src 'logo.ico')"
    "/win32manifest:$(Join-Path $src 'app.manifest')"
    '/r:System.Windows.Forms.dll'
    '/r:System.Drawing.dll'
    "/resource:$ps1,ps1"
    "/resource:$(Join-Path $src 'DeepSeek.Ui.cs'),ui"
    "/resource:$(Join-Path $src 'bg.png'),bg"
    "/resource:$(Join-Path $src 'logo.ico'),ico"
    (Join-Path $src 'launcher.cs')
)

Write-Host '编译中...'
& $csc $argList
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

# 校验：子系统必须是 GUI（2），否则启动会闪黑窗
$b = [System.IO.File]::ReadAllBytes($outExe)
$peOff  = [BitConverter]::ToInt32($b, 0x3C)
$subsys = [BitConverter]::ToUInt16($b, $peOff + 24 + 68)
if ($subsys -ne 2) { throw "子系统是 $subsys，不是 GUI（2），启动会闪黑窗" }

# 校验：四个资源都已内嵌
$asm = [System.Reflection.Assembly]::LoadFile($outExe)
$names = $asm.GetManifestResourceNames()
foreach ($n in @('ps1', 'ui', 'bg', 'ico')) {
    if ($names -notcontains $n) { throw "缺少内嵌资源：$n" }
}

$fi = Get-Item -LiteralPath $outExe
Write-Host ("完成：{0}（{1:N0} 字节）" -f $fi.FullName, $fi.Length)
