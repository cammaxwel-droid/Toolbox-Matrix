# Source publication status and procedure

The reviewed source was pushed to `main` and the repository was made public with the Product Owner's explicit approval on 24 October 2026. Its independent history contains the reviewed source set; the canonical project's original history and the older GitHub repository were preserved.

## Local publication source tree

Inside the canonical project, the independent publication repository is:

`.local/fresh-publication-20261023/source`

It began as a non-destructive copy of the explicitly selected source and documents. It now has its own `.git` directory and `main` branch. Only this publication repository is connected to the new Toolbox-Matrix destination; the canonical index, refs, history and origin remote remain unchanged.

PUBLISH-FILES.txt enumerates the exact 66 published source files. SOURCE_MANIFEST.json hashes the other 64 reviewed files, excluding itself and PUBLISH-FILES.txt to avoid self-reference. Refresh affected sizes and hashes for approved source changes, then verify the complete path list and manifest before each commit.

## Repository status

The [cammaxwel-droid/Toolbox-Matrix](https://github.com/cammaxwel-droid/Toolbox-Matrix) repository is public, with `main` as its default branch. Repository ID: `1430839613`.

The initial source commit is [`d29c864413e4a349a5f87f5f0fd6bf310c060275`](https://github.com/cammaxwel-droid/Toolbox-Matrix/commit/d29c864413e4a349a5f87f5f0fd6bf310c060275). It was pushed to the verified empty private repository with a regular non-force push before the separately approved visibility change. The commit, complete 66-file tree and public access were verified.

Use this existing repository for approved updates. Recheck the account, destination and remote `main` before pushing; stop if unexpected remote changes need review. Leave the older FP2.4 repository and the canonical origin remote untouched.

## Attribution-corrected binary candidate

The separately checked binary package is `preview/V1-Public-Candidates/1.0.0-candidate-sanitized-20261023-attribution-corrected.zip` in the canonical project. It identifies Cameron Maxwell in the project notices and retains the existing application payload. The original `1.0.0-candidate-sanitized-20261023` folder and ZIP remain untouched.

This ZIP contains the compiled application and notices and remains a separate private review candidate. The public repository contains the 66 source files in PUBLISH-FILES.txt; no compiled ZIP or binary release was uploaded. The source-code licence remains undecided, and public binary distribution has not been approved.

## Approved source updates

The initial history and origin remote already exist. Do not repeat the initialisation procedure or replace the repository.

For an owner-authorised update:

1. Work in the independent publication repository, confirm the exact destination above and inspect local changes.
2. Check remote `main` and compare it with the local branch. Inspect any unexpected changes before continuing; do not overwrite remote history.
3. Edit only the approved files, refresh their SOURCE_MANIFEST.json sizes and SHA-256 hashes, and recheck the complete source inventory and private-data scan.
4. Stage the explicitly reviewed paths and manifest, then inspect the staged diff and verify that no generated binaries, private files or unrelated changes are included.
5. Commit the reviewed update and use a regular non-force push to `main`. Verify the remote commit, tree and file contents after the push.

Use Cameron Maxwell's confirmed attribution and the account's GitHub noreply commit address. Keep the canonical root's `.git`, original pending work, compiled candidates and older repository untouched. Do not push other refs, tags or releases as part of a source documentation update. If authentication requires a browser or prompt, the user must complete that interaction.

## Exclusions and housekeeping diagnosis

The six inactive third-party product logos and their SOURCES.md are excluded pending rights verification. Local originals and the original approved icon remain untouched.

Build-Local.ps1, Build-V1Candidate.ps1, Review-InterfaceConsistency.ps1 and Verify-TargetedCli.ps1 are retained in the canonical folder but excluded from this fresh source proposal. The new headless procedure is sufficient to build and check the publication source set. The historical verifier expects deleted `preview/Toolbox-Matrix-FP2.4-Refinement/build/WinGetToolbox.dll` and `docs/FP0.5.0-TARGETED-VERIFICATION.json`, plus a historical six-launcher inventory. It is not valid current verification and has not been run or silently repaired.

Seven earlier tracked document deletions remain unstaged in the canonical repository. No old file is restored or deleted to make this fresh source snapshot.

## Public source and release status

Public source visibility is approved and active. The code/documentation licence remains undecided under the existing reserved-rights notice, and the provenance and human gates in [RELEASE-CHECKLIST](RELEASE-CHECKLIST.md) still apply to future releases. No tag, compiled binary or stable release is approved by the source push or public visibility.
