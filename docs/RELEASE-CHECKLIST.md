# Release gates

The source repository is public. The sanitized V1 compiled candidate remains private, and no binary or stable release has been approved. Headless checks and public source visibility do not pass the release gates below.

| Gate | Required evidence |
| --- | --- |
| Licence and source visibility | Owner approved public source hosting/viewing with all rights reserved and no general reuse licence. This is not an open-source licence and does not approve a binary release. Branding/artwork remain separately reserved. |
| Provenance | Owner confirmation for original icon/tile art is recorded. Confirm permissions for any externally copied code/text and review branding claims. Excluded brand assets stay excluded. |
| Source | Review every path and hash in SOURCE_MANIFEST.json and PUBLISH-FILES.txt; no secrets/private data/generated binaries. |
| Binary | Run the path/secret scan, inspect debug and resource metadata, verify notices and archive hashes for the exact package. |
| Visual acceptance | The original V1 reference was visually approved by the owner. A rebuilt binary has not had a new visual review; unchanged source/resource checks are narrower evidence. |
| Real installation/UAC | Confirm success, denial, installer failure, retry/skip, stop/cancellation and any continuing elevated installer behaviour on the exact candidate. |
| Recovery and data | Exercise backup/settings restore, destinations, retention, interruption/restart and error recovery with a disposable profile. |
| Clean Windows 11 x64 | Confirm Desktop Runtime and WinGet prerequisites, extraction and startup on a clean machine. |
| Optional integration | Explicitly test alias/Start Menu registration and removal, relocation and stale-target recovery. |
| Signing and download | Decide signing/distribution policy, inspect downloaded-package behaviour and record unsigned status where applicable. |
| Publication authority | The initial source push and public visibility were approved and completed on 24 October 2026. The owner separately approved this source rename/update. No tag, binary or stable release is approved. |

No app launch, provider call, installer, UAC, desktop input, screen capture, registration, signing, tag or release is part of the headless preparation script.

Static scans cannot guarantee the absence of all secrets or infringement. Do not mark a human or Windows integration gate passed from a build, hash, console test or source inspection.
