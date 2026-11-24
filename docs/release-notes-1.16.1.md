**A connection form made for S3.** In the connections window (`Ctrl+F`) an S3 connection no longer uses the fields of FTP and SFTP. Choose the provider first and the form follows it:

- **Amazon S3:** type or pick the region (suggested while you type); the address is made from it and shown below the field.
- **Cloudflare R2, Backblaze B2, Wasabi:** one field for the service address, with an example; the region is read from the address.
- **MinIO or another S3-compatible server:** address, port, region and "no encryption (HTTP)". A pasted URL such as `http://192.168.1.10:9000` is split into host, port and HTTP.
- **Access keys:** *Access Key ID* and *Secret Access Key* side by side. The secret key can be typed right in the form and is kept in the Windows Credential Manager; "Forget the secret key" removes it. Without an access key ID the connection is anonymous.
- **Where to start:** a bucket and a folder inside it, as two fields. Name the bucket when the key may use only that one and cannot list them all.

The list of connections shows the provider and the bucket instead of an address with the access key in it. The secret key follows a changed address or port, but is dropped when the access key changes, so another key never signs with the old secret. The HTTP warning in this window no longer talks about FTP.

**Compatibility:** connections saved by 1.16.0 and earlier open as before; the provider is read from their address.

Install from GitHub: download the MSIX and PlainCommander-dev.cer, trust the certificate once, then open the MSIX. The Microsoft Store version (https://apps.microsoft.com/detail/9NJMJ1VQ1X87) updates by itself once the submission is certified.
