$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
$references = @(
    "/reference:$wpf\WindowsBase.dll"
    "/reference:$wpf\PresentationCore.dll"
    "/reference:$wpf\PresentationFramework.dll"
    '/reference:System.Xaml.dll'
)
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:ROTH.exe @references RothApp.cs
    if ($LASTEXITCODE -ne 0) { throw 'ROTH build failed.' }
}
finally {
    Pop-Location
}
