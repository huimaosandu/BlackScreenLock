# BlackScreenLock 构建脚本（Windows PowerShell / pwsh 均可）
# 目标：单个 exe，不依赖任何第三方库与安装包，靠系统自带 .NET Framework 4.x 运行。
#
# 用法：
#   .\build.ps1                仅编译
#   .\build.ps1 -RunSelfTest   编译后跑自检并把结果打印出来

param(
    [switch]$RunSelfTest
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $csc)) {
    throw "找不到 .NET Framework 编译器: $csc（请确认系统已安装 .NET Framework 4.x）"
}
if (-not (Test-Path -LiteralPath $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

# 源文件统一带 UTF-8 BOM，保证 csc 正确解析中文注释与界面文字
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
Get-ChildItem -LiteralPath $srcDir -Filter *.cs | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if (-not $hasBom) {
        $text = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
        [System.IO.File]::WriteAllText($_.FullName, $text, $utf8Bom)
    }
}

$sources = @(Get-ChildItem -LiteralPath $srcDir -Filter *.cs | Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) { throw "src 目录下没有 .cs 源文件" }

$outExe = Join-Path $distDir 'BlackScreenLock.exe'

Write-Host "编译中... ($($sources.Count) 个源文件)"
$ErrorActionPreference = 'Continue'
$output = & $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    "/out:$outExe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Management.dll `
    $sources 2>&1
$exitCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($output) { $output | ForEach-Object { Write-Host $_ } }
if ($exitCode -ne 0) { throw "编译失败，退出码 $exitCode" }

$exeSize = [math]::Round((Get-Item -LiteralPath $outExe).Length / 1KB, 0)
Write-Host "编译成功: $outExe ($exeSize KB)"

if ($RunSelfTest) {
    Write-Host "`n运行自检..."
    $proc = Start-Process -FilePath $outExe -ArgumentList '--selftest' -Wait -PassThru
    $logPath = Join-Path $distDir 'selftest.log'
    if (Test-Path -LiteralPath $logPath) {
        Get-Content -LiteralPath $logPath -Encoding UTF8
    } else {
        Write-Host "未生成 selftest.log，退出码 $($proc.ExitCode)"
    }
    Write-Host "自检退出码: $($proc.ExitCode)"
}
