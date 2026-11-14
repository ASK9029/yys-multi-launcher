$ErrorActionPreference = 'Stop'

# 用 .NET Framework 自带的 csc 编译，不需要安装 Visual Studio。
# 输出：dist\YysMulti.exe（winexe，无控制台窗口）

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "找不到 csc.exe，请确认已安装 .NET Framework 4.x"
}

$root = $PSScriptRoot
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $out -Force | Out-Null

& $csc /nologo /codepage:65001 /target:winexe /platform:anycpu `
    /out:"$out\YysMulti.exe" `
    /win32manifest:"$root\src\app.manifest" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "$root\src\YysMulti.cs"

if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

Get-Item "$out\YysMulti.exe" | Select-Object Name, Length, LastWriteTime | Format-List
Write-Host "完成：$out\YysMulti.exe" -ForegroundColor Green
