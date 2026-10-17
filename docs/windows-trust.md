# Windows download trust

The initial Karmac preview is **not Authenticode-signed**. Windows may show an unknown-publisher/SmartScreen warning, and some policies can block it. Do not disable Defender, SmartScreen, or organizational protections to run it. Review the source or build it yourself if you cannot establish trust in the download.

Extract the complete ZIP. Compare its SHA-256 against `SHA256SUMS.txt` supplied with that same release, using a manifest obtained from the trusted release page:

```powershell
Get-FileHash .\Karmac-Windows-x64.zip -Algorithm SHA256
Get-FileHash .\Karmac.exe -Algorithm SHA256
Get-AuthenticodeSignature .\Karmac.exe | Select-Object Status, StatusMessage
```

The external manifest contains hashes for both the ZIP and the executable inside it. Unsigned binaries report `NotSigned`. Checksums provide integrity against the manifest, not proof of the author's identity. This project has not obtained a publisher certificate or configured a signing service.

## Maintainer signing path

1. Obtain an appropriate trusted code-signing certificate or supported signing service, and keep its credentials outside the repository and build artifacts.
2. Sign the final EXE using SHA-256 and an RFC 3161 timestamp through the provider's documented tooling. Use an existing certificate store/hardware-backed identity or secret store, never a committed PFX/password.
3. Verify with `signtool verify /pa /all /v Karmac.exe` and `Get-AuthenticodeSignature`. Confirm a valid trusted chain and the expected publisher.
4. Package those exact signed bytes with `package.ps1 -UseExistingBuild` after tests have passed, and regenerate all checksums. Do not rebuild or modify the EXE after signing.
5. Test the downloaded package on clean Windows systems. Signing does not guarantee the absence of SmartScreen warnings for a new build.

References: [Microsoft signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options), [SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).
