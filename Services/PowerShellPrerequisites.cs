namespace TermKit.Services;

internal static class PowerShellPrerequisites
{
    internal const string Script = """
        param([string]$ModuleRoot, [string]$DocumentsPath, [string]$LocalDataPath)
        $ErrorActionPreference = 'Stop'
        Write-Host "TermKit embedded PowerShell: $($PSVersionTable.PSVersion); PSHOME: $PSHOME"
        if ($PSVersionTable.PSVersion -lt [version]'7.6.0') { throw "Update TermKit: EXO 3.10.1 requires embedded PowerShell 7.6 or later; this application is using $($PSVersionTable.PSVersion). Updating standalone PowerShell does not update the application's embedded engine." }
        $documents = if ($DocumentsPath) { $DocumentsPath } else { [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments) }
        $userModules = if ($documents) { Join-Path $documents 'PowerShell\Modules' }
        if ($ModuleRoot) { $userModules = $ModuleRoot }
        $writeProbe = {
            param($directory)
            [IO.Directory]::CreateDirectory($directory) | Out-Null
            $probe = Join-Path $directory ('.termkit-write-test-' + [guid]::NewGuid().ToString('N'))
            [IO.File]::WriteAllText($probe, 'test')
            Remove-Item -LiteralPath $probe -Force
        }
        $writeError = if (-not $userModules) { 'The Documents folder is unavailable.' } else { try { & $writeProbe $userModules } catch { $_.Exception.Message } }
        if ($writeError) {
            $local = if ($LocalDataPath) { $LocalDataPath } else { [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData) }
            $fallback = if ($local) { Join-Path $local 'TermKit\Modules' }
            $fallbackError = if (-not $fallback) { 'The LocalAppData folder is unavailable.' } else { try { & $writeProbe $fallback } catch { $_.Exception.Message } }
            if ($fallbackError) { throw "TermKit cannot write PowerShell modules to '$userModules' ($writeError) or '$fallback' ($fallbackError). Security software such as Windows Security Controlled Folder Access or a device policy may be blocking it. Allow TermKit to write to one of these folders and restart the app." }
            Write-Warning "Cannot write modules to '$userModules' ($writeError). Using '$fallback' instead."
            $userModules = $fallback
        }
        if (($env:PSModulePath -split [IO.Path]::PathSeparator) -notcontains $userModules) {
            $env:PSModulePath = $userModules + [IO.Path]::PathSeparator + $env:PSModulePath
        }
        $managerErrors = [Collections.Generic.List[string]]::new()
        $managerLoaded = $false
        $loadManager = {
            param($candidate)
            if (-not $candidate -or $candidate.Version -lt [version]'1.2.0') { return $false }
            try {
                Import-Module $candidate.Path
                Write-Host "PSResourceGet $($candidate.Version): $($candidate.Path)"
                return $true
            } catch {
                $managerErrors.Add("$($candidate.Path): $($_.Exception.Message)")
                return $false
            }
        }
        foreach ($candidate in @(Get-Module -ListAvailable Microsoft.PowerShell.PSResourceGet | Sort-Object Version -Descending)) {
            if (& $loadManager $candidate) { $managerLoaded = $true; break }
        }
        if (-not $managerLoaded) {
            $pwshPaths = @(
                (Get-Command pwsh.exe -CommandType Application -All -ErrorAction SilentlyContinue).Source
                foreach ($programFiles in @($env:ProgramW6432, $env:ProgramFiles)) {
                    if ($programFiles) {
                        $installedPwsh = Join-Path $programFiles 'PowerShell\7\pwsh.exe'
                        if (Test-Path -LiteralPath $installedPwsh -PathType Leaf) { $installedPwsh }
                    }
                }
            ) | Where-Object { $_ } | Select-Object -Unique
            $discoveryErrors = [Collections.Generic.List[string]]::new()
            foreach ($pwshPath in $pwshPaths) {
                $process = $null
                try {
                    $manifest = Join-Path (Split-Path $pwshPath) 'Modules\Microsoft.PowerShell.PSResourceGet\Microsoft.PowerShell.PSResourceGet.psd1'
                    $candidate = $null
                    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
                        $candidate = Get-Module -ListAvailable $manifest | Select-Object -First 1
                    }
                    if (-not $candidate) {
                        $process = [Diagnostics.Process]::new()
                        $process.StartInfo.FileName = $pwshPath
                        $process.StartInfo.UseShellExecute = $false
                        $process.StartInfo.CreateNoWindow = $true
                        $process.StartInfo.RedirectStandardOutput = $true
                        $process.StartInfo.RedirectStandardError = $true
                        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', '$PSHOME')) {
                            $process.StartInfo.ArgumentList.Add($argument)
                        }
                        $process.Start() | Out-Null
                        $stdout = $process.StandardOutput.ReadToEndAsync()
                        $stderr = $process.StandardError.ReadToEndAsync()
                        if (-not $process.WaitForExit(10000)) {
                            $process.Kill($true)
                            throw 'PowerShell home discovery timed out after 10 seconds.'
                        }
                        $powerShellHome = $stdout.GetAwaiter().GetResult().Trim()
                        $processError = $stderr.GetAwaiter().GetResult().Trim()
                        if ($process.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($powerShellHome)) {
                            throw "PowerShell home discovery failed (exit $($process.ExitCode)): $processError"
                        }
                        $manifest = Join-Path $powerShellHome 'Modules\Microsoft.PowerShell.PSResourceGet\Microsoft.PowerShell.PSResourceGet.psd1'
                        if (Test-Path -LiteralPath $manifest -PathType Leaf) {
                            $candidate = Get-Module -ListAvailable $manifest | Select-Object -First 1
                        }
                    }
                    if (& $loadManager $candidate) { $managerLoaded = $true; break }
                    if (-not $candidate) { $discoveryErrors.Add("$pwshPath : PSResourceGet was not found.") }
                } catch {
                    $discoveryErrors.Add("$pwshPath : $($_.Exception.Message)")
                } finally {
                    if ($process) { $process.Dispose() }
                }
            }
        }
        if (-not $managerLoaded) {
            # Microsoft Store PowerShell keeps its modules in a package folder that other apps cannot load from.
            $privateBase = if ($LocalDataPath) { $LocalDataPath } else { [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData) }
            $privateRoot = Join-Path $privateBase 'TermKit\PSResourceGet'
            $privateManifest = Join-Path $privateRoot '1.2.0\Microsoft.PowerShell.PSResourceGet.psd1'
            try {
                if (-not (Test-Path -LiteralPath $privateManifest -PathType Leaf)) {
                    Write-Host 'Downloading PSResourceGet 1.2.0 from the PowerShell Gallery...'
                    Add-Type -AssemblyName System.IO.Compression.FileSystem
                    $temp = Join-Path ([IO.Path]::GetTempPath()) ('termkit-psrg-' + [guid]::NewGuid().ToString('N'))
                    [IO.Directory]::CreateDirectory($temp) | Out-Null
                    try {
                        $package = Join-Path $temp 'psresourceget.nupkg'
                        Invoke-WebRequest -Uri 'https://www.powershellgallery.com/api/v2/package/Microsoft.PowerShell.PSResourceGet/1.2.0' -OutFile $package -MaximumRedirection 5 -UseBasicParsing
                        $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
                        if ($hash -ne '90E4C97B2F5ECF8C7D4730CA3B7028739E2B7665DED27249390483F73BE71944') { throw "Downloaded PSResourceGet failed integrity verification (SHA256 $hash)." }
                        $stage = Join-Path $privateRoot ('1.2.0.' + [guid]::NewGuid().ToString('N'))
                        [IO.Directory]::CreateDirectory($privateRoot) | Out-Null
                        [IO.Compression.ZipFile]::ExtractToDirectory($package, $stage)
                        try { [IO.Directory]::Move($stage, (Join-Path $privateRoot '1.2.0')) }
                        catch { if (-not (Test-Path -LiteralPath $privateManifest -PathType Leaf)) { throw } }
                        finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force } }
                    } finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
                }
                $candidate = Get-Module -ListAvailable $privateManifest | Select-Object -First 1
                if (& $loadManager $candidate) { $managerLoaded = $true }
            } catch { $managerErrors.Add("Private PSResourceGet download: $($_.Exception.Message)") }
        }
        if (-not $managerLoaded) { throw "TermKit cannot load Microsoft.PowerShell.PSResourceGet 1.2.0 or later (embedded PowerShell $($PSVersionTable.PSVersion)). Check internet access to www.powershellgallery.com, or in standalone PowerShell 7 run 'Install-Module Microsoft.PowerShell.PSResourceGet -MinimumVersion 1.2.0 -Scope CurrentUser -Repository PSGallery', then restart TermKit. Details: $((@($managerErrors) + @($discoveryErrors)) -join ' | ')" }
        $repository = Get-PSResourceRepository PSGallery -ErrorAction SilentlyContinue
        if (-not $repository) { Register-PSResourceRepository -PSGallery; $repository = Get-PSResourceRepository PSGallery }
        if ($repository.Uri.ToString().TrimEnd('/') -ne 'https://www.powershellgallery.com/api/v2') { throw 'PSGallery must point to https://www.powershellgallery.com/api/v2.' }
        $requirements = [ordered]@{ PackageManagement = [version]'1.4.8.1'; PowerShellGet = [version]'2.2.5'; ExchangeOnlineManagement = [version]'3.10.1' }
        foreach ($name in $requirements.Keys) {
            $installed = Get-Module -ListAvailable $name | Where-Object { -not $_.PrivateData.PSData.Prerelease } |
                Sort-Object Version -Descending | Select-Object -First 1
            $version = $requirements[$name]
            if ($installed -and $installed.Version -gt $version) { $version = $installed.Version }
            try {
                $latest = Find-PSResource $name -Repository PSGallery
                if ($latest -and [version]$latest.Version -gt $version) { $version = [version]$latest.Version }
            } catch {
                if (-not $installed -or $installed.Version -lt $requirements[$name]) { throw }
                Write-Warning "Cannot check updates for $name. Using installed $($installed.Version)."
            }
            $loaded = Get-Module $name
            if ($loaded -and @($loaded | Where-Object Version -NE $version).Count) { throw "$name has an older version loaded. Restart the application before updating modules." }
            if (-not $installed -or $installed.Version -ne $version) {
                Write-Host "Installing $name $version for CurrentUser..."
                [IO.Directory]::CreateDirectory($userModules) | Out-Null
                try { Save-PSResource $name -Version $version.ToString() -Repository PSGallery -Path $userModules -IncludeXml -TrustRepository -Quiet }
                catch {
                    if ($_.Exception -is [UnauthorizedAccessException] -or $_.Exception.Message -match 'denied|not have permission|unauthorized') {
                        throw "Access was denied while saving $name $version to '$userModules'. Security software such as Windows Security Controlled Folder Access may be blocking TermKit. Allow TermKit to write there, or install the module from standalone PowerShell 7 with: Install-PSResource $name -Scope CurrentUser. Details: $($_.Exception.Message)"
                    }
                    throw
                }
                $modulePath = Join-Path $userModules "$name\$version\$name.psd1"
            } else {
                $modulePath = $installed.Path
            }
            if (-not (Test-Path -LiteralPath $modulePath)) { throw "$name $version was not saved at $modulePath. Module preparation is incomplete." }
            Import-Module $modulePath -RequiredVersion $version -Global
            Write-Host "Ready: $name $version"
        }
        foreach ($name in @('Connect-ExchangeOnline','Disconnect-ExchangeOnline','Get-ConnectionInformation','Get-EXOMailbox','Update-ModuleManifest')) {
            if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { throw "Required cmdlet missing: $name. Repair the module and restart the application." }
        }
        """;
}