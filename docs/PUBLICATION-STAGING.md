# Publication status

## Current approval

On 24 October 2026, the Product Owner approved public hosting and viewing of this source repository and approved committing and pushing this source update to the existing `cammaxwel-droid/Toolbox-Matrix` repository on `main`.

The repository is public. This approval does not grant a general licence to reuse the project code or documentation. The project remains all rights reserved; no binary release, tag, or additional distribution is approved. See [LICENSE](../LICENSE), [licensing and provenance](LICENSING.md), and the [release checklist](RELEASE-CHECKLIST.md).

## Historical preparation procedure

The following instructions describe a superseded October 2026 plan to create a separate private source repository. They are retained only as historical context. Do not run them for this repository: it already has an `origin`, existing history, and an approved public source-hosting destination.

The old plan used a fresh source tree at `.local/fresh-publication-20261023/source`, proposed a separate initial commit, and prohibited connecting that tree to the canonical repository or its existing GitHub history. Its assumption that the GitHub destination was private and empty is no longer current.

`PUBLISH-FILES.txt` enumerates the reviewed source set. `SOURCE_MANIFEST.json` hashes the listed files except itself and `PUBLISH-FILES.txt` to avoid self-reference. Refresh and review those records when the source set changes.

The candidate ZIP is not the source publication and is not approved for binary distribution. Real installation/UAC, recovery, clean-machine, optional integration, signing/download, and rebuilt-binary visual checks remain release gates.
