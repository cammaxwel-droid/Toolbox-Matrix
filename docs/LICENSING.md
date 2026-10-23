# Licensing and provenance

## Current decision

On 24 October 2026 the Product Owner approved public hosting and viewing of the source repository, and chose **all rights reserved; no general reuse licence** for the project code and documentation. This supersedes the earlier instruction to leave the licence undecided.

[LICENSE](../LICENSE) records the scope. This is not an open-source licence and does not grant permission to independently use, copy, modify, redistribute, sublicense or sell the project's original code or documentation. Repository visibility does not change those terms. No binary release or broader reuse permission is approved.

GitHub's terms govern viewing and forking the repository on its service. That does not grant an independent general reuse licence for the project. See [GitHub's licensing guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository).

The Toolbox Matrix name, branding, icon and original visual artwork are reserved separately, only to the extent enforceable rights exist and belong to the maintainer. A later code licence must state its scope clearly without silently including the artwork. See [ARTWORK_NOTICE](../ARTWORK_NOTICE.md).

If MIT is selected later, its standard grant permits commercial reuse and redistribution subject to its notice requirements. An artwork exclusion belongs in a clear scope notice rather than silently changing the standard MIT text. Sources: [MIT text](https://opensource.org/license/mit), [GitHub licensing guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository), [no-licence guidance](https://choosealicense.com/no-permission/).

## Original artwork

Cameron Maxwell explicitly confirmed that all original application icon/logo and tile artwork was created by Cameron Maxwell, with nothing copied and no third-party references. The required v02 icon is retained unchanged. The app's existing tile treatment is drawn with code and installed fonts. This is owner-supplied provenance, not an independent authorship or trademark investigation.

Earlier project documentation records ChatGPT/Codex assistance. Rights depend on the protectable contribution, applicable law and relevant creation-service terms; neither AI assistance nor an owner's statement warrants a guarantee of zero infringement.

## External and excluded material

The apphost embedded in the EXE comes from Microsoft's exact .NET host package. Its unmodified licence and notices are retained under `docs/licenses`. .NET/Windows desktop runtimes, WinGet, Windows fonts and package installers are external prerequisites, not bundled payload.

Six inactive third-party brand assets and their old SOURCES document are excluded from the publication source set and build. No local originals are deleted. Their individual brand/redistribution permissions remain unverified.

No explicit NuGet PackageReference appears in the six existing application/test projects. This does not imply that the native host or runtime has no third-party components. See [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md).

## Before any broader release

The owner must separately approve any broader reuse licence or binary/stable release, confirm permissions for any externally copied code/text, and review branding claims. Source inspection and pattern scans do not establish originality or trademark availability. Unknown or suspect third-party files must stay excluded until their rights are established. See the remaining [release gates](RELEASE-CHECKLIST.md).

This preparation does not reinterpret the licence or history of an earlier repository or revoke rights that may already have been granted elsewhere.
