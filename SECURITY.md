# Security policy

## Reporting a vulnerability

Please **do not** open a public issue for a security problem. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository
(<https://github.com/plain-browser/plain-commander/security/advisories/new>).

Describe what you found, how to reproduce it and the version of Plain Commander (shown in the title bar). You will get
an answer as soon as possible; a fix is released as a new version and the report is credited unless you prefer
otherwise.

## What is in scope

Plain Commander handles things worth protecting, so reports are especially welcome about:

- the **`.pce` encrypted container** (format, key derivation, handling of malformed or malicious files);
- handling of **malicious archives** (path traversal, links, resource exhaustion) when browsing or unpacking;
- storage and handling of **passwords and keys** (Credential Manager use, memory, logs);
- **server connections** (FTP/FTPS/SFTP/S3): certificate and host-key checks;
- the **administrator helper** (named pipe, privilege boundaries).

## Supported versions

Only the latest release receives fixes.
