**Encrypted `.pce` containers can now live on S3 storage and be changed there.** On Amazon S3 and compatible servers (MinIO, RustFS, Cloudflare R2, Wasabi, Backblaze B2) a container is kept as a folder `name.pce` of encrypted objects instead of one file. In the panel it is one violet folder: `Enter` and the password open it, and `F5`, `F6`, `F7`, `Shift+F6` and `F8` work inside as in any server folder. Adding a file sends only that file, deleting one writes a small new index, and reading a file fetches only its blocks. The server sees object sizes and the times of changes, never names or content.

- `F5` on a `.pce` file towards S3 asks: **as a container** (the default; nothing is encrypted again and the password stays) or as a plain copy of the one file. `F5` on a container towards a disk gives back one `.pce` file.
- *Files → Encrypt* with an `s3://…` target creates the container on the server directly; *Files → Change .pce password* works on it and rewrites one small object.
- Large files go up in parts and come down in ranges, 8 at once. Measured over a 1 Gb/s network: 10 GiB up at 111 MiB/s and down at 113 MiB/s, which is the speed of the link. Deleting 10 MiB from a 10 GiB container takes about 0.3 s.
- Two computers may change the same container at the same time on servers with conditional writes: the slower one repeats its change on top of the other's.
- If a container is found in an older state than this computer last saw (a bucket restored from a backup, or tampering), Plain Commander asks before opening it.

**Fixed:** a password, login or confirmation dialog that stayed open for more than 10 seconds while a server folder was being opened ended in "the disk did not respond"; the time a dialog is open no longer counts.

**Compatibility:** `.pce` files on disk are unchanged and still open in 1.15. A container kept on S3 opens only in 1.16 or later; earlier versions show it as an ordinary folder of unreadable objects.

**Known limits:** viewing (`F3`) a file from a container on a server downloads a decrypted temporary copy, as for any server file; directory synchronisation and comparison treat the container as a folder of its decrypted files; copying a container from one place to another on the same server is not supported; a small S3 server with little memory can slow down downloads in 8 streams. Tested with RustFS only.

Install from GitHub: download the MSIX and PlainCommander-dev.cer, trust the certificate once, then open the MSIX. The Microsoft Store version (https://apps.microsoft.com/detail/9NJMJ1VQ1X87) updates by itself once the submission is certified.
