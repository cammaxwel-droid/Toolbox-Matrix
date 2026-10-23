# Source publication status and procedure

## Current status

The `cammaxwel-droid/Toolbox-Matrix` repository is public, with `main` as its default branch. The initial source push and public visibility were approved by the Product Owner on 24 October 2026. The initial publication used an independent history so the canonical project's prior history and the older GitHub repository were preserved.

The Product Owner has since approved this source rename/update for the existing public repository. This update does not approve a general code reuse licence, binary release, tag, or stable release. The code and documentation remain all rights reserved; see [LICENSE](../LICENSE) and [licensing and provenance](LICENSING.md).

## Source inventory

`PUBLISH-FILES.txt` enumerates the 67 reviewed source paths. `SOURCE_MANIFEST.json` hashes the other 65 files, excluding itself and `PUBLISH-FILES.txt` to avoid self-reference. Hashes use Git index blob bytes so they remain stable across checkout line endings. Refresh and verify the complete path list and hashes whenever the reviewed source changes.

The root `Toolbox-Matrix.sln` is the supported solution for the current app and smoke-check projects. It references `src/ToolboxMatrix/ToolboxMatrix.csproj`, not the old project location.

## Approved update workflow

For this owner-approved rename/update, the canonical working clone is being updated on `main` with a regular non-force push to the existing `origin`. A concurrent owner-approved remote documentation commit was fetched and preserved; no remote history is overwritten.

For future updates:

1. Confirm the repository and branch, fetch the latest `main`, and inspect any remote changes before proceeding.
2. Edit only owner-approved files; refresh `SOURCE_MANIFEST.json` and review `PUBLISH-FILES.txt`.
3. Run the solution build and relevant headless checks. Inspect the staged diff and ensure no generated binaries, private files, or unrelated changes are included.
4. Commit the reviewed source and use a regular non-force push. Verify the pushed commit and repository state.

Do not force-push, create tags/releases, or publish compiled binaries without separate approval. If authentication requires a browser or prompt, the owner must complete that interaction.

## Binary candidate and remaining gates

The sanitized candidate under `preview/V1-Public-Candidates` is a separate local review build. No compiled ZIP or binary release is approved for public distribution.

Real installation/UAC, recovery, clean-machine, optional integration, signing/download, and rebuilt-binary visual checks remain open. The six inactive third-party product logos and their SOURCES document remain excluded pending rights verification. Static checks do not establish originality or trademark availability.
