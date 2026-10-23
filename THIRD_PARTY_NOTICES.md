# Third-party notices and distribution inventory

Toolbox Matrix's original code and documentation are all rights reserved, with no general public reuse licence. The owner has approved public hosting and viewing of this repository only. [LICENSE](LICENSE) records this scope; [ARTWORK_NOTICE](ARTWORK_NOTICE.md) records the owner's confirmation and the separate treatment of original branding and artwork.

## Shipped components

| Component | Use | Licence and notice treatment |
| --- | --- | --- |
| Toolbox Matrix application DLL, resources and launcher | Project implementation, original icon and tile artwork | All rights reserved; no general reuse licence. Public hosting/viewing approval does not grant independent reuse rights. |
| Microsoft.NETCore.App.Host.win-x64 **10.0.13** | Native host code incorporated into ToolboxMatrix.exe | Exact official package declares MIT. Retain the complete upstream licence and third-party notices, unchanged, below. |

The prepared distribution is framework-dependent. Its runtime payload is the application EXE, DLL, deps/runtimeconfig JSON and adjacent launcher. Licence, artwork, provenance, usage and hash records accompany those files. PDBs, SDK/runtime DLLs, installers, downloaded product logos, test executables and private profiles are not shipped.

Exact upstream files:

- [Host licence](docs/licenses/dotnet-host-10.0.13-LICENSE.txt), SHA-256 `D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B`.
- [Host package third-party notices](docs/licenses/dotnet-host-10.0.13-THIRD-PARTY-NOTICES.txt), SHA-256 `6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21`.

Official source: [Microsoft.NETCore.App.Host.win-x64 10.0.13](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64/10.0.13). The locally retained official package has SHA-256 `503712375103BD93AA6AB51D1B64C134AC131E002540EC818B447DFE72C52174`. The build audit checks the installed host against that retained package and compares the packaged EXE's native code section.

The complete upstream notices are preserved without pruning. Their discussion of Apache, BSD, Unicode, zlib and other resources does not mean every listed resource is compiled into this small host. It avoids substituting a generic claim that all dependencies are MIT.

## External prerequisites, not bundled

| Component | Role and scope |
| --- | --- |
| .NET 10 Windows Desktop Runtime, x64 | Supplies Microsoft.NETCore.App and Microsoft.WindowsDesktop.App; installed separately through Microsoft's distribution. Their own terms/notices apply. |
| .NET SDK | Build tool only. This preparation used SDK 10.0.402. No SDK payload is redistributed. |
| WinGet / Microsoft App Installer | External package manager. [WinGet source licence](https://github.com/microsoft/winget-cli/blob/master/LICENSE); client binaries, services and sources retain their applicable terms. |
| Windows APIs and system fonts | user32/kernel32, registry, shell, Segoe UI, Segoe UI Symbol and Arial Black are supplied by Windows. No Windows DLL or font file is bundled. [Microsoft font FAQ](https://learn.microsoft.com/en-us/typography/fonts/font-faq). |
| Packages listed in Collections or search | Package IDs and live metadata identify separately licensed third-party software. No installers are bundled. Listing a package does not grant a licence or imply endorsement. |
| ChatGPT and Codex | Recorded creation assistance, not a bundled runtime service or library. |

Toolbox Matrix is independent of Microsoft, OpenAI and package publishers. Product names identify the products involved and do not imply endorsement. No trademark clearance is claimed.

## Excluded local material

The inactive files `discord.svg`, `epicgames.svg`, `firefox.svg`, `steam.svg`, `vlcmediaplayer.svg`, `vscode.ico` and their historical SOURCES.md are omitted. Their local acquisition records identify external brand sources; their individual reuse/brand permissions have not been established. They are preserved locally. The active LogoRegistry returns no external logo and the project embeds only the original Toolbox Matrix icon.

Screenshots, old binaries, recovery history, private reports/settings, credentials and signing material are outside the publication source set. A future self-contained build, installer, font, new artwork, third-party library or bundled utility requires a new inventory.
