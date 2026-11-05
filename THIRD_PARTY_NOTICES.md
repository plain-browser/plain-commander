# Third-party notices

Plain Commander itself is licensed under the [Mozilla Public License 2.0](LICENSE). It uses the following open-source
components under their own licenses. The versions are the ones pinned in `Directory.Packages.props`; the license of each
package is the one it declares on NuGet.

## Bundled library

| Component | License | Notes |
|---|---|---|
| **7-Zip** `7z.dll` 26.05 (© 1999-2026 Igor Pavlov), <https://www.7-zip.org/> | GNU LGPL 2.1 or later, with the "unRAR license restriction" for some code; BSD 3-clause and BSD 2-clause for parts | Unmodified, in `third_party/7-Zip` (x64 and arm64) with its license text (`License.txt`, installed next to the program as `7-Zip-License.txt`). Source code: <https://www.7-zip.org/>. The unRAR restriction forbids using the unRAR code to re-create the RAR compression algorithm; Plain Commander only reads rar archives and never writes them. |

## NuGet packages

| Package | Version | License |
|---|---|---|
| [AWSSDK.S3](https://github.com/aws/aws-sdk-net/) and AWSSDK.Core | 4.0.104.4, 4.0.102.11 | Apache-2.0 |
| [BouncyCastle.Cryptography](https://www.bouncycastle.org/) | 2.7.0 | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT |
| [CommunityToolkit.WinUI.Extensions](https://github.com/CommunityToolkit/Windows) and CommunityToolkit.WinUI.Controls.SettingsControls | 8.2.251219 | MIT |
| [FluentFTP](https://github.com/robinrodricks/FluentFTP) | 55.1.0 | MIT |
| [FluentFTP.GnuTLS](https://github.com/robinrodricks/FluentFTP) | 1.0.40 | LGPL-2.1-only (bundles GnuTLS, <https://www.gnutls.org/>, whose source code is available there) |
| [Konscious.Security.Cryptography.Argon2](https://github.com/kmaragon/Konscious.Security.Cryptography) and .Blake2 | 1.3.1, 1.1.1 | MIT |
| [Microsoft.Extensions.DependencyInjection](https://dot.net/) and Abstractions; Microsoft.Extensions.Logging.Abstractions | 10.0.14, 8.0.2, 8.0.3 | MIT |
| [Microsoft.Windows.CsWin32](https://github.com/microsoft/CsWin32) (build time only) | 0.3.367 | MIT |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) (WinUI 3, runtime) | 2.6.4 | Microsoft Windows App SDK license (redistributable runtime; see the license file in the package) |
| [Serilog](https://serilog.net/) and Serilog.Sinks.File | 4.4.0, 7.0.0 | Apache-2.0 |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 1.0.0 | MIT |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | 2026.0.0 | MIT |

## Test-only packages (not shipped)

xunit v3 (Apache-2.0), AwesomeAssertions (Apache-2.0).

## Cryptography

The `.pce` container uses the AES-GCM and ChaCha20-Poly1305 implementations of the .NET runtime and the Argon2id
implementation of Konscious.Security.Cryptography. No cryptographic algorithm is implemented in this repository.
