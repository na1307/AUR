#!/bin/bash
echo "$GPG_PASSPHRASE" | gpg --batch --passphrase-fd 0 --import <(echo "$GPG_PRIVATE_KEY" | base64 -d)
expect -c 'spawn gpg --edit-key AB69CFD0BE72421F trust quit; send "5\ry\r"; expect eof'
touch ./test
echo "$GPG_PASSPHRASE" | gpg --batch --yes --pinentry-mode loopback --passphrase-fd 0 --sign ./test
dotnet ./AurBuild.dll
