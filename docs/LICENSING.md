# Licensing and provenance

## Current decision

On 23 October 2026 the Product Owner explicitly instructed: **keep the licence undecided for now**. This supersedes the earlier local preparation documents' statement that MIT had been confirmed. The original documents and MIT draft remain in private preservation storage.

[LICENSE](../LICENSE) retains the reserved-rights notice for the publicly visible source. It is not an open-source licence and does not grant public redistribution rights. The Product Owner approved public source visibility on 24 October 2026 while keeping the licence undecided.

GitHub's terms govern viewing and forking the public repository on its service. Public visibility does not select an open-source licence for the project. See [GitHub's licensing guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository).

The Toolbox Matrix name, branding, icon and original visual artwork are reserved separately, only to the extent enforceable rights exist and belong to the maintainer. A later code licence must state its scope clearly without silently including the artwork. See [ARTWORK_NOTICE](../ARTWORK_NOTICE.md).

If MIT is selected later, its standard grant permits commercial reuse and redistribution subject to its notice requirements. An artwork exclusion belongs in a clear scope notice rather than silently changing the standard MIT text. Sources: [MIT text](https://opensource.org/license/mit), [GitHub licensing guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository), [no-licence guidance](https://choosealicense.com/no-permission/).

## Original artwork

Cameron Maxwell explicitly confirmed that all original application icon/logo and tile artwork was created by Cameron Maxwell, with nothing copied and no third-party references. The required v02 icon is retained unchanged. The app's existing tile treatment is drawn with code and installed fonts. This is owner-supplied provenance, not an independent authorship or trademark investigation.

Earlier project documentation records ChatGPT/Codex assistance. Rights depend on the protectable contribution, applicable law and relevant creation-service terms; neither AI assistance nor an owner's statement warrants a guarantee of zero infringement.

## External and excluded material

The apphost embedded in the EXE comes from Microsoft's exact .NET host package. Its unmodified licence and notices are retained under `docs/licenses`. .NET/Windows desktop runtimes, WinGet, Windows fonts and package installers are external prerequisites, not bundled payload.

Six inactive third-party brand assets and their old SOURCES document are excluded from the publication source set and build. No local originals are deleted. Their individual brand/redistribution permissions remain unverified.

No explicit NuGet PackageReference appears in the six existing application/test projects. This does not imply that the native host or runtime has no third-party components. See [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md).

## Before a licensed or binary release

The source repository is public with the owner's approval. A future code/documentation licence still requires the owner's explicit choice and a clear artwork scope. Any binary or stable release requires separate approval, confirmation of externally copied code/text permissions, and the remaining [release gates](RELEASE-CHECKLIST.md). Source inspection and pattern scans do not establish originality or trademark availability. Unknown or suspect third-party files must stay excluded until their rights are established.

This preparation does not reinterpret the licence or history of an earlier repository or revoke rights that may already have been granted elsewhere.
