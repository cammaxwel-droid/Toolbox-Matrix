# Release gates

This is a private, sanitized V1 candidate. Passing headless checks does not approve public distribution.

| Gate | Required evidence |
| --- | --- |
| Licence | Product Owner selects code/documentation terms and confirms the separate scope of branding/artwork. Currently undecided. |
| Provenance | Owner confirmation for original icon/tile art is recorded. Confirm permissions for any externally copied code/text and review branding claims. Excluded brand assets stay excluded. |
| Source | Review every path and hash in SOURCE_MANIFEST.json and PUBLISH-FILES.txt; no secrets/private data/generated binaries. |
| Binary | Run the path/secret scan, inspect debug and resource metadata, verify notices and archive hashes for the exact package. |
| Visual acceptance | The original V1 reference was visually approved by the owner. A rebuilt binary has not had a new visual review; unchanged source/resource checks are narrower evidence. |
| Real installation/UAC | Confirm success, denial, installer failure, retry/skip, stop/cancellation and any continuing elevated installer behaviour on the exact candidate. |
| Recovery and data | Exercise backup/settings restore, destinations, retention, interruption/restart and error recovery with a disposable profile. |
| Clean Windows 11 x64 | Confirm Desktop Runtime and WinGet prerequisites, extraction and startup on a clean machine. |
| Optional integration | Explicitly test alias/Start Menu registration and removal, relocation and stale-target recovery. |
| Signing and download | Decide signing/distribution policy, inspect downloaded-package behaviour and record unsigned status where applicable. |
| Publication authority | Separately approve commit/push, any tag/release and any change to public visibility. |

No app launch, provider call, installer, UAC, desktop input, screen capture, registration, signing, tag or release is part of the headless preparation script.

Static scans cannot guarantee the absence of all secrets or infringement. Do not mark a human or Windows integration gate passed from a build, hash, console test or source inspection.
