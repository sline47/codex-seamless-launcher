param([switch]$ProbeOnly)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules') + ';' + $env:PSModulePath
$stateRoot = Join-Path $env:LOCALAPPDATA 'CodexDreamSkin'
$engine = Join-Path $stateRoot 'engine'
try {
    . (Join-Path $engine 'scripts\common-windows.ps1')
    . (Join-Path $engine 'scripts\theme-windows.ps1')
    function Get-RezeVisibleSessionProcess {
        param([int]$Port)
        foreach ($listener in @(Get-DreamSkinPortListeners -Port $Port)) {
            try {
                $process = Get-Process -Id $listener.OwningProcess -ErrorAction Stop
                $process.Refresh()
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) { return $process.Id }
            } catch { }
        }
        return 0
    }
    $reusable = $false
    $windowPid = 0
    $operation = Enter-DreamSkinOperationLock
    try {
        $state = Read-DreamSkinState -Path (Join-Path $stateRoot 'state.json')
        if ($state -and $state.port -and -not (Test-DreamSkinPaused -StateRoot $stateRoot)) {
            $codex = Get-DreamSkinCodexInstallFromState -State $state
            if ($codex) {
                $identity = Get-DreamSkinVerifiedCdpIdentity -Port $state.port -Codex $codex
                if ($identity -and $identity.BrowserId -ceq $state.browserId) {
                    $windowPid = Get-RezeVisibleSessionProcess -Port $state.port
                    $node = Get-DreamSkinNodeRuntime
                    $active = Read-DreamSkinTheme -ThemeDirectory (Join-Path $stateRoot 'active-theme')
                    $probe = Invoke-DreamSkinNative -FilePath $node.Path -ArgumentList @(
                        (Join-Path $engine 'scripts\injector.mjs'), '--verify', '--port', "$($state.port)",
                        '--browser-id', $identity.BrowserId, '--theme-dir', (Join-Path $stateRoot 'active-theme'), '--timeout-ms', '3000'
                    ) -SeparateStderr
                    $payload = ($probe.Output -join "`n") | ConvertFrom-Json
                    foreach ($target in @($payload.targets)) {
                        $r = $target.result
                        # A splash may occlude a healthy renderer. Reuse requires verified
                        # package/port ownership, current theme/revision and real UI structure.
                        if ($windowPid -gt 0 -and $r.installed -eq $true -and $r.stylePresent -eq $true -and
                            $r.themeId -ceq $active.Theme.id -and $r.revision -ceq $r.expectedRevision -and
                            $r.version -ceq $r.expectedVersion -and $r.readiness.structurePass -eq $true) {
                            $reusable = $true
                        }
                    }
                }
            }
        }
    } finally { Exit-DreamSkinOperationLock -Mutex $operation }
    if ($reusable) {
        Write-Output ('REZE_WINDOW_PID=' + $windowPid)
        Write-Output 'REZE_REUSED_VERIFIED_SESSION'
        exit 0
    }
    if ($state -and $windowPid -eq 0) { Write-Output 'REZE_BACKGROUND_SESSION_HAS_NO_WINDOW' }
    if ($ProbeOnly) { Write-Output 'REZE_NO_REUSABLE_SESSION'; exit 3 }
    $token = [guid]::NewGuid().ToString('N')
    $currentCodex = Get-DreamSkinCodexInstall
    $hasWindow = $false
    foreach ($candidate in @(Get-DreamSkinCodexProcesses -Codex $currentCodex)) {
        try {
            if ((Get-Process -Id $candidate.ProcessId -ErrorAction Stop).MainWindowHandle -ne [IntPtr]::Zero) { $hasWindow = $true }
        } catch { }
    }
    if ($hasWindow) {
        & (Join-Path $engine 'scripts\start-dream-skin.ps1') -PromptRestart -ResultToken $token
    } else {
        Write-Output 'REZE_RESTARTING_WINDOWLESS_BACKGROUND_SESSION'
        & (Join-Path $engine 'scripts\start-dream-skin.ps1') -RestartExisting -ResultToken $token
    }
    $result = Read-DreamSkinStartResult -StateRoot $stateRoot -Token $token
    if ($result.outcome -cne 'success') { throw ('Dream Skin did not complete: ' + $result.category) }
    $newState = Read-DreamSkinState -Path (Join-Path $stateRoot 'state.json')
    $windowPid = Get-RezeVisibleSessionProcess -Port $newState.port
    if ($windowPid -le 0) { throw 'Dream Skin returned without a chat window. Open Codex from the Dream Skin tray and retry.' }
    # A native window can still be displaying the logo. Wait for the actual
    # themed chat structure, independently of the helper process lifetime.
    $node = Get-DreamSkinNodeRuntime
    $active = Read-DreamSkinTheme -ThemeDirectory (Join-Path $stateRoot 'active-theme')
    $chatReady = $false
    $stableSamples = 0
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $structureReady = $false
        try {
            $probe = Invoke-DreamSkinNative -FilePath $node.Path -ArgumentList @(
                (Join-Path $engine 'scripts\injector.mjs'), '--verify', '--port', "$($newState.port)",
                '--browser-id', $newState.browserId, '--theme-dir', (Join-Path $stateRoot 'active-theme'), '--timeout-ms', '3000'
            ) -SeparateStderr
            $payload = ($probe.Output -join "`n") | ConvertFrom-Json
            foreach ($target in @($payload.targets)) {
                $r = $target.result
                if ($r.installed -eq $true -and $r.stylePresent -eq $true -and
                    $r.themeId -ceq $active.Theme.id -and $r.revision -ceq $r.expectedRevision -and
                    $r.version -ceq $r.expectedVersion -and $r.readiness.structurePass -eq $true -and
                    $r.readiness.documentPass -eq $true -and $r.readiness.viewportPass -eq $true) {
                    $structureReady = $true
                }
            }
        } catch { Write-Output 'REZE_WAITING_FOR_CHAT_STRUCTURE' }
        if ($structureReady) { $stableSamples++ } else { $stableSamples = 0 }
        if ($stableSamples -ge 2) { $chatReady = $true; break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $chatReady) { throw 'Chat interface is still loading; handoff was not performed.' }
    Write-Output 'REZE_CHAT_STRUCTURE_READY'
    Write-Output ('REZE_WINDOW_PID=' + $windowPid)
    Write-Output 'REZE_STARTED_VERIFIED_SESSION'
    exit 0
} catch {
    Write-Output ('REZE_START_FAILED: ' + $_.Exception.Message)
    exit 1
}
