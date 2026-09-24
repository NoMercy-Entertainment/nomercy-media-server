# Third-Party Notices

NoMercy Media Server includes binaries from the following third-party libraries.

---

## libnfs

**Version:** 6.0.2
**Source:** https://github.com/sahlberg/libnfs
**License:** GNU Lesser General Public License v2.1 (LGPL-2.1)

Copyright (C) 2010 by Ronnie Sahlberg

libnfs is a client library for accessing NFS shares over a network.
NoMercy bundles pre-built shared libraries (`libnfs.dll`, `libnfs.so`, `libnfs.dylib`)
for Windows, Linux, and macOS to support in-process NFS storage access without
requiring OS-level mounts.

The LGPL-2.1 license text is reproduced in full at
`server/network/nomercy-libnfs/LICENSE`.

Build scripts and Dockerfiles used to produce these binaries are in
`server/network/nomercy-libnfs/`.
