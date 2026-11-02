<#
.SYNOPSIS
	Generates a self-signed code-signing certificate for signing the TermKit MSIX package,
	and exports it as a base64-encoded PFX string suitable for a GitHub Actions secret.

.DESCRIPTION
	Run this ONCE to create the signing certificate used by the GitHub Actions release workflow.
	The certificate Subject must match the Publisher value in Package.appxmanifest (CN=A253761).

	After running this script:
	  1. Copy the contents of "signing-cert-base64.txt" and save it as a GitHub Actions secret
		 named PACKAGE_CERTIFICATE_BASE64 (Settings > Secrets and variables > Actions).
	  2. Save the password you choose below as a second secret named PACKAGE_CERTIFICATE_PASSWORD.
	  3. Keep "TermKitSigning.pfx" somewhere safe (or delete it once the secrets are saved -
		 it is not needed locally afterward).
	  4. Distribute "TermKitSigning.cer" (the public certificate, no private key) to anyone who
		 needs to install the MSIX, so they can add it to their Trusted People / Trusted Root store.

.NOTES
	This certificate is for internal/sideload distribution only. It will show a warning the first
	time a user installs the MSIX unless the .cer has been imported into their certificate store.
#>

param(
	[string]$Subject = "CN=A253761",
	[string]$OutputDirectory = "$PSScriptRoot\..\signing",
	[SecureString]$Password
)

if (-not $Password) {
	$Password = Read-Host -AsSecureString -Prompt "Enter a password to protect the PFX file"
}

Write-Host "Creating self-signed certificate for $Subject..." -ForegroundColor Cyan

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$cert = New-SelfSignedCertificate `
	-Type Custom `
	-Subject $Subject `
	-KeyUsage DigitalSignature `
	-FriendlyName "TermKit MSIX Signing Certificate" `
	-CertStoreLocation "Cert:\CurrentUser\My" `
	-TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
	-NotAfter (Get-Date).AddYears(5)

$pfxPath = Join-Path $OutputDirectory "TermKitSigning.pfx"
$cerPath = Join-Path $OutputDirectory "TermKitSigning.cer"

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $Password | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath))
$base64Path = Join-Path $OutputDirectory "signing-cert-base64.txt"
Set-Content -Path $base64Path -Value $base64 -NoNewline

Write-Host ""
Write-Host "Certificate created:" -ForegroundColor Green
Write-Host "  PFX (private key): $pfxPath"
Write-Host "  CER (public only): $cerPath"
Write-Host "  Base64 for secret: $base64Path"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Add GitHub secret PACKAGE_CERTIFICATE_BASE64 with the contents of $base64Path"
Write-Host "  2. Add GitHub secret PACKAGE_CERTIFICATE_PASSWORD with the password you supplied"
Write-Host "  3. Distribute $cerPath to users so they can trust the certificate before installing"
