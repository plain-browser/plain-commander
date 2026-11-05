# 7-Zip 26.05 (7z.dll)

Plain Commander reads and writes archives through 7-Zip's `7z.dll` (7z, rar, iso, cab, msi, wim, vhd, …).
The files here are the unmodified libraries from the official 7-Zip 26.05 release (2026-10-21):

| File | Taken from | SHA-256 of the source package |
|---|---|---|
| `x64/7z.dll` | `7z2605-x64.msi` (administrative install, `Files\7-Zip\7z.dll`) | `3fe4ab99fa15629c652944309a2b2bc6e41fc933fc6b63f5df1ebcf3aea68263` |
| `arm64/7z.dll` | `7z2605-arm64.exe` (extracted with 7-Zip) | `86d5790d5dd84cab4636c3bb7fab10d3ed7cd3e57e6119401bd2769e28165268` |

Packages downloaded from https://www.7-zip.org/a/ and checked against the digests published with the
GitHub release https://github.com/ip7z/7zip/releases/tag/26.05.

SHA-256 of the libraries: x64 `78bf6b668d75801e3aa180b4ccb16e6d8395fba80dec8a065055bb6f8dfabb82`,
arm64 `311d0a45182401e10c71a12b33a5c4a7171f6a8c1d67eeec9ef5dc2a5c962fe8`.

License: GNU LGPL with the unRAR restriction, BSD 3-clause and BSD 2-clause for parts; see `License.txt`,
which ships next to the program as `7-Zip-License.txt`. Source code: https://www.7-zip.org/.
