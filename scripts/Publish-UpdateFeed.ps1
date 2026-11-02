param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][string]$FeedUrl,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$expectedFeedUrl = "https://$($Repository.Split('/')[0]).github.io/$($Repository.Split('/')[1])/TermKit.appinstaller"
if ($FeedUrl -cne $expectedFeedUrl) { throw "Feed URL must match the project Pages URL: $expectedFeedUrl" }
if (Test-Path -LiteralPath $OutputDirectory) {
    if (Get-ChildItem -LiteralPath $OutputDirectory -Force) { throw 'Feed output directory must be empty.' }
}

$releaseJson = gh api "repos/$Repository/releases/latest"
if ($LASTEXITCODE -ne 0) { throw 'Could not read the latest published release.' }
$release = $releaseJson | ConvertFrom-Json
$tag = $release.tag_name
if ($release.draft -or $release.prerelease -or $tag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'The latest release must be a stable vMajor.Minor.Build release.'
}
$version = [version]($tag.Substring(1) + '.0')
$packageName = "TermKit-$tag-x64.msix"
$packageUrl = "https://github.com/$Repository/releases/download/$tag/$packageName"
$assets = @($release.assets | Where-Object { $_.name -ceq $packageName -and $_.browser_download_url -ceq $packageUrl })
if ($assets.Count -ne 1) { throw 'The latest release does not contain the expected x64 MSIX.' }
$trustName = "TermKit-Trust-Bundle-$tag.zip"
$trustUrl = "https://github.com/$Repository/releases/download/$tag/$trustName"
if (@($release.assets | Where-Object { $_.name -ceq $trustName -and $_.browser_download_url -ceq $trustUrl }).Count -ne 1) {
    throw 'The latest release does not contain the expected certificate trust bundle.'
}
[xml]$expectedManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Package.appxmanifest') -Raw
$expectedIdentity = $expectedManifest.Package.Identity

$response = Invoke-WebRequest -Uri $FeedUrl -SkipHttpErrorCheck -TimeoutSec 30
if ($response.StatusCode -eq 200) {
    $currentFeed = [xml]::new()
    if ($response.Content -is [byte[]]) {
        $stream = [IO.MemoryStream]::new($response.Content)
        try {
            $currentFeed.Load($stream)
        } finally {
            $stream.Dispose()
        }
    } else {
        $currentFeed.LoadXml($response.Content)
    }
    $currentPackage = $currentFeed.AppInstaller.MainPackage
    if ($currentPackage.Name -cne $expectedIdentity.Name -or $currentPackage.Publisher -cne $expectedIdentity.Publisher) {
        throw 'The existing feed belongs to a different package family.'
    }
    if ([version]$currentPackage.Version -gt $version -or [version]$currentFeed.AppInstaller.Version -gt $version) {
        throw 'Refusing to downgrade the published update feed.'
    }
} elseif ($response.StatusCode -ne 404) {
    throw "Could not check the existing feed (HTTP $($response.StatusCode))."
}

$workDirectory = Join-Path ([IO.Path]::GetTempPath()) ('termkit-feed-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDirectory | Out-Null
try {
    gh release download $tag --repo $Repository --pattern $packageName --dir $workDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the published MSIX.' }
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $workDirectory $packageName))
    $manifestPath = Join-Path $workDirectory 'AppxManifest.xml'
    try {
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if (-not $manifestEntry -or -not $archive.GetEntry('AppxSignature.p7x')) {
            throw 'The release asset must contain a manifest and package signature.'
        }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($manifestEntry, $manifestPath)
    } finally {
        $archive.Dispose()
    }
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $identity = $manifest.Package.Identity
    if ($identity.Name -cne $expectedIdentity.Name -or $identity.Publisher -cne $expectedIdentity.Publisher -or
        [version]$identity.Version -ne $version -or $identity.ProcessorArchitecture -ne 'x64') {
        throw 'The published MSIX identity does not match the expected package family, version, and architecture.'
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    & (Join-Path $PSScriptRoot 'Prepare-Release.ps1') -VersionTag $tag -ManifestPath $manifestPath `
        -FeedUrl $FeedUrl -PackageUrl $packageUrl -OutputPath (Join-Path $OutputDirectory 'TermKit.appinstaller')
    $page = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../site/index.html') -Raw
    $pageValues = @{
        '{{TAG}}' = $tag
        '{{REPOSITORY}}' = "https://github.com/$Repository"
        '{{PACKAGE}}' = $packageUrl
        '{{TRUST}}' = $trustUrl
        '{{ICON}}' = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../Assets/Square150x150Logo.scale-200.png')))
    }
    foreach ($placeholder in $pageValues.Keys) {
        $page = $page.Replace($placeholder, [System.Net.WebUtility]::HtmlEncode($pageValues[$placeholder]))
    }
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'index.html') -Value $page -Encoding utf8
    Write-Output "Prepared update feed for $tag ($($identity.Publisher))."
} finally {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force
}