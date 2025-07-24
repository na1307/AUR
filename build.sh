#!/bin/bash
echo "$GPG_PASSPHRASE" | gpg --pinentry-mode loopback --passphrase-fd 0 -abs -o /dev/null --sign /dev/null
./AurBuild
