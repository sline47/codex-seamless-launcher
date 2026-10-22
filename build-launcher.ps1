$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $root 'CodexSeamlessLauncher.exe'
$candidate = Join-Path $root 'launcher-build-candidate.exe'
$animation = Join-Path $root 'intro-convergence.zip'
$icon = Join-Path $root 'assets\codex-black-transparent.ico'
$arguments = @(
    '/nologo','/target:winexe',('/out:'+$candidate),('/win32icon:'+$icon),
    '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.IO.Compression.dll',
    ('/resource:'+$animation+',intro.zip'),('/resource:'+$icon+',codex.ico'),
    ('/resource:'+(Join-Path $root 'assets\reze-motion-keyframe.png')+',motion.png'),
    ('/resource:'+(Join-Path $root 'RezeStartSkin.ps1')+',start-skin.ps1'),
    (Join-Path $root 'RezeTransitionLauncher.cs')
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw '启动器编译失败。' }
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($candidate))
$stream = $assembly.GetManifestResourceStream('intro.zip')
$sha = [Security.Cryptography.SHA256]::Create()
try { $embedded = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','') }
finally { $stream.Dispose(); $sha.Dispose() }
if ($embedded -ne (Get-FileHash -LiteralPath $animation).Hash) { throw '打包动画与汇聚版不一致，未替换启动器。' }
Copy-Item -LiteralPath $candidate -Destination $output -Force
Write-Output 'Build verified: correct convergence animation embedded.'
