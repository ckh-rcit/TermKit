param(
    [Parameter(Mandatory)][string]$VersionTag,
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\Package.appxmanifest'),
    [string]$FeedUrl,
    [string]$PackageUrl,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\TermKit.appinstaller')
)

$ErrorActionPreference = 'Stop'
if ($VersionTag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'Release tags must be vMajor.Minor.Build, with no prerelease suffix.'
}
$version = [version]($VersionTag.Substring(1) + '.0')
if ($version.Major -eq 0 -or $version.Major -gt 65535 -or $version.Minor -gt 65535 -or $version.Build -gt 65535) {
    throw 'MSIX version components must fit the supported 16-bit range; major must be nonzero.'
}
[xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
if ($version -lt [version]$manifest.Package.Identity.Version) { throw 'Release tag would downgrade the manifest version.' }
if ($FeedUrl) {
    foreach ($endpoint in @($FeedUrl, $PackageUrl)) {
        $uri = $null
        if (-not [uri]::TryCreate($endpoint, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo) {
            throw 'Feed and package URLs must use HTTPS without credentials.'
        }
    }
}
$manifest.Package.Identity.Version = $version.ToString()
$manifest.Save([IO.Path]::GetFullPath($ManifestPath))
if (-not $FeedUrl) {
    Write-Warning 'APPINSTALLER_FEED_URL is unset; this release will not enroll automatic updates.'
    return
}

$namespace = 'http://schemas.microsoft.com/appx/appinstaller/2018'
$document = [xml]'<?xml version="1.0" encoding="utf-8"?><AppInstaller />'
$root = $document.CreateElement('AppInstaller', $namespace)
$document.ReplaceChild($root, $document.DocumentElement) | Out-Null
$root.SetAttribute('Uri', $FeedUrl)
$root.SetAttribute('Version', $version.ToString())
$package = $document.CreateElement('MainPackage', $namespace)
$package.SetAttribute('Name', $manifest.Package.Identity.Name)
$package.SetAttribute('Publisher', $manifest.Package.Identity.Publisher)
$package.SetAttribute('Version', $version.ToString())
$package.SetAttribute('ProcessorArchitecture', 'x64')
$package.SetAttribute('Uri', $PackageUrl)
$root.AppendChild($package) | Out-Null
$settings = $document.CreateElement('UpdateSettings', $namespace)
$onLaunch = $document.CreateElement('OnLaunch', $namespace)
$onLaunch.SetAttribute('HoursBetweenUpdateChecks', '0')
$onLaunch.SetAttribute('ShowPrompt', 'true')
$onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
$settings.AppendChild($onLaunch) | Out-Null
$settings.AppendChild($document.CreateElement('AutomaticBackgroundTask', $namespace)) | Out-Null
$root.AppendChild($settings) | Out-Null
$document.Save([IO.Path]::GetFullPath($OutputPath))