# Toolbox Matrix
### A Toolbox for WinGet

Toolbox Matrix is a Windows desktop app for finding packages through WinGet, reviewing an installation selection and keeping track of installed applications.

**Status: 1.0.0 candidate.** The owner has approved public source hosting and viewing in this repository. The code and documentation remain all rights reserved; no general reuse licence or binary release is approved. See [LICENSE](LICENSE), [licensing and provenance](docs/LICENSING.md) and the [release gates](docs/RELEASE-CHECKLIST.md).

Maintained by [@cammaxwel-droid](https://github.com/cammaxwel-droid). Built with assistance from ChatGPT and Codex. The application icon and tile artwork are Cameron Maxwell's original work; see [ARTWORK_NOTICE](ARTWORK_NOTICE.md).

Toolbox Matrix is independent of Microsoft, OpenAI and the publishers of the packages it lists. Windows, WinGet and other product names identify the products involved; they do not imply endorsement.

## What it does

| Area | Implemented behaviour |
| --- | --- |
| Package discovery | Search WinGet, inspect package identity and details, and load further results when available. |
| Installed Apps | Read WinGet and Windows application inventory, distinguish installed/unknown status, and view available updates. |
| Collections | Browse curated package IDs in Tiles or Compact List and add eligible packages to the installation selection. |
| Installation Centre | Review exact package IDs and sources, explicitly enable real installation, and confirm the queue. Packages run sequentially, with progress, retry/skip/stop handling and reports. |
| Appearance | Dark, Light and System backgrounds; accent, subheading and tile-artwork colours; presets, custom colour values and draft preview/apply controls. |
| Backup / Restore | Export a reinstall list and Toolbox preferences; inspect a backup, select eligible packages for review, or confirm a settings restore. |
| Diagnostics | Run read-only checks and preview/export a support report. These checks do not repair Windows. |
| Settings | Configure navigation, text size, shortcuts, report/backup destinations, retention and optional Windows integration. |
| TLBXM commands | Open a page, search, or request a read-only inventory refresh through the local launcher. |

V1 shows seven collections: **AI & Automation, Gaming, Development, Media / Creative, Studying, Security / Diagnostics, and Everyday Essentials**.

Tools and Sites remain in the source and ordinary Debug/Release development builds. V1 hides them from collection browsing/search and redirects their direct routes to Collections. Both registries are empty.

Selecting a package does not install it. Real installation requires the separate opt-in and final confirmation in Installation Centre. Installers may display their own windows or request administrator permission. Stopping WinGet does not roll back completed changes, and an independent or elevated installer may continue.

## Requirements

- Windows 11 x64 is the intended platform. Other Windows versions, ARM and x86 delivery have not been validated.
- The **x64 .NET 10 Windows Desktop Runtime** supplies WPF and Windows Forms. The base .NET or ASP.NET runtime alone is insufficient. See [Microsoft's Windows installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
- A working WinGet command, normally supplied by Microsoft's App Installer. No minimum version is enforced by the app; the supported version range still needs release testing. See [WinGet setup](https://learn.microsoft.com/en-us/windows/package-manager/winget/).
- Package discovery/downloads need network access. Source agreements and each selected application's licence still apply; some installers require elevation.
- Building requires the .NET 10 SDK and PowerShell on Windows. This preparation used SDK 10.0.402 and host pack 10.0.13.

The candidate is a framework-dependent folder distribution. It does not bundle the .NET runtime, WinGet, fonts or the applications listed in Collections.

## Use a candidate

Public download and redistribution are not yet approved. For an explicitly authorised local review, keep the complete candidate folder together and open `ToolboxMatrix.exe`.

Its executable payload is:

- `ToolboxMatrix.exe`
- `ToolboxMatrix.dll`
- `ToolboxMatrix.deps.json`
- `ToolboxMatrix.runtimeconfig.json`
- `tlbxm.cmd`

The candidate also carries project rights/artwork notices, exact third-party notices, usage guidance and a hash manifest. The EXE alone is incomplete. The sanitized build omits debugging symbols and maps compiler source paths to a neutral prefix. It is unsigned.

1. Search for an application or open a Collection.
2. Inspect its package ID, source, publisher and installed status.
3. Use Select to add eligible packages, then open Installation Centre.
4. Review the selection. Enable real installation only when ready, then read and accept the final confirmation.
5. Review the resulting report and refresh Installed Apps.

Backup files contain a reinstall list and Toolbox preferences. They do not back up personal documents, installed program files, application data, product keys or software licences. Opening a backup does not install anything. Restoring preferences requires confirmation, keeps this machine's destinations, resets retention to Keep all and clears imported deletion consent.

## TLBXM commands

From a complete bundle folder, use `.\tlbxm.cmd` in PowerShell. The shorter `tlbxm` spelling works after deliberate per-user registration; opening the app does not register it.

| Command after `.\tlbxm.cmd` | Effect |
| --- | --- |
| No argument | Open the saved start page. |
| `search visual studio` | Search for the supplied package name. |
| `apps` | Open Installed Apps. |
| `collections` | Open Collections. |
| `updates` | Refresh inventory and show available updates; no updates run automatically. |
| `install` | Open Installation Centre; no installation starts automatically. |
| `settings` | Open Settings. |
| `backup` | Open Backup / Restore; no automatic export/import. |
| `diagnostics` | Open Diagnostics; choose a check to run it. |
| `refresh` | Open Home and refresh read-only inventory. |
| `help` / `about` | Open the corresponding page. |

Global alias and Start Menu actions have separate confirmation dialogs. Registration changes current-user PATH/App Paths; relocating a registered build can require updating it. These paths still need clean-machine testing.

## Headless build and checks

Open [Toolbox-Matrix.sln](Toolbox-Matrix.sln) in Visual Studio or VS Code for the current application and smoke-check projects.

From the source root:

```powershell
.\scripts\Build-PublicCandidate.ps1
```

This creates a fresh candidate under `preview/V1-Public-Candidates` and a separate work directory under `.local`. Existing destinations cause a stop. The procedure:

1. Builds and runs the console-only V1 and development Release configuration checks.
2. Publishes the exact tested V1 DLL without symbols, source-link data or local Git revision metadata.
3. Includes only the required runtime files, launcher and distribution documents.
4. Statically inspects PE/debug metadata, managed resources, native icon resources, runtime dependencies, private-path/secret patterns and upstream-notice hashes.
5. Creates a ZIP and verifies every archived file against the audited bundle.

The configuration suite expects 77 checks per profile, with seven V1 and nine development categories. The static inspector does not load or execute the product assembly. No app window, WinGet provider, installer, UAC, screen capture, registration, signing or Git command is run.

For a build inside the canonical developer folder, `-ReferenceCandidate` can point to the preserved approved V1 folder to compare its compiled UI/icon resources. That local reference is not required for an independent source build.

Work logs and reports remain in the new private `.local` work directory. Read them before relying on a candidate. Historical UI/capture tests in `tests/FP050Smoke` require their own authorised environment and are not executed by this procedure.

## Local data and privacy

Toolbox stores preferences, selection, caches and recovery data under `%LOCALAPPDATA%\ToolboxMatrix`. Default export folders are `tblxm Reports` and `tblxm Backups` there. Settings also offers Custom or Ask Every Time.

Reports/backups can contain software inventory, paths and machine information. Inspect exports before sharing. Retention can delete eligible aged files when explicitly configured; Keep all is the conservative option, and custom-folder retention requires consent for that exact location and scope.

The inspected source has no telemetry uploader or automatic report-upload service. WinGet, its sources and selected installers make their own network requests and have their own privacy terms.

## Publication source set

`SOURCE_MANIFEST.json` records the reviewed files, sizes and SHA-256 hashes. `PUBLISH-FILES.txt` is the exact path list for a later separately approved source commit.

The set contains application C#/XAML/project files and the original icon, test source, the headless publication procedure, the adjacent launcher and current documentation/notices. It excludes old previews/history, generated binaries, screenshots, private profiles, secrets, signing keys, inactive product logos and historical helpers that depend on deleted local files.

The canonical project retains all surviving local material. See [publication procedure](docs/PUBLICATION-STAGING.md). Source preparation is not permission to commit, push, tag, release or change repository visibility.

## Licence and attribution

The code and documentation are all rights reserved, with no general public reuse licence. The owner's permission to host and view this source repository does not grant permission to reuse its code, documentation or artwork. No MIT grant or open-source status is implied.

Original branding and artwork are reserved separately to the extent the maintainer has enforceable rights. Third-party components keep their own terms. See [ARTWORK_NOTICE](ARTWORK_NOTICE.md), [licensing and provenance](docs/LICENSING.md) and [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md).

Builds, scans and owner-supplied provenance do not establish zero infringement or pass the [remaining human release gates](docs/RELEASE-CHECKLIST.md).
