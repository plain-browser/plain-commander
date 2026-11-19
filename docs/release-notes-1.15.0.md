**Encrypted `.pce` files are now containers you can change in place.** Open one with `Enter` and its password, and it behaves like a folder: copy files in or out with `F5`, make folders with `F7`, rename with `Shift+F6`, delete with `F8`. Deleting a file shrinks the `.pce` at once by exactly what the file took; nothing else is re-encrypted (10 MiB out of a 10 GiB container: about 0.1 s). Nothing is decrypted to disk for browsing any more.

- *Files → Change .pce password* replaces the password without re-encrypting the content.
- An interrupted change (crash, killed process) leaves a file that still opens; the unused space is reclaimed the next time it is changed.
- Compression is decided per file inside the container.
- Encrypting is about one second per GiB slower than before, because the data is now flushed to disk before the file is declared complete.

**Compatibility:** `.pce` files written by 1.15 do **not** open in earlier versions (they report "Not a Plain Commander encrypted file"). Files from earlier versions open as before, read-only; the first change, or a password change, offers to rewrite the file in the new layout. Keep a copy if you still need to open a file with an older version.

**Known limits:** deleting from a container needs a few kilobytes of free disk space (on a completely full disk it fails and leaves the file untouched); a tab inside a `.pce` is not restored after a restart; copying from one `.pce` straight into another is not supported.

Also in this release (from the unreleased 1.14.10):

- **Command palette** (`Ctrl+Shift+P`): type a few letters to find and run any command.
- **Reopen a closed tab** (`Ctrl+Shift+T`), with its folder, cursor and marks.
- **Name masks in copy and move** (`F5`/`F6`): a target such as `*.bak` or `old_*.*` renames the files on the way.

Install from GitHub: download the MSIX and PlainCommander-dev.cer, trust the certificate once, then open the MSIX. The Microsoft Store version (https://apps.microsoft.com/detail/9NJMJ1VQ1X87) updates by itself once the submission is certified.
