# Building PlainBytes DocumentExtractor

This guide covers building the application from source on Windows. Official Microsoft Store packages are published separately; this repository does not distribute official installers.

## Prerequisites

- Windows 10 or Windows 11, **64-bit**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (includes the Windows Desktop / WPF workload for `net8.0-windows`)
- Optional: [Visual Studio 2022](https://visualstudio.microsoft.com/) with the **.NET desktop development** workload

## Repository layout

The repository contains the following projects and supporting files:

```text
PlainBytes.Core/                 Shared shell, Store helpers, shared assets
PlainBytes.DocumentExtractor/    Application project
PlainBytes.DocumentExtractor.sln Solution (optional but recommended)
LICENSE
README.md
BUILDING.md
CHANGELOG.md
CONTRIBUTING.md
TRADEMARKS.md
THIRD_PARTY_NOTICES.md
resources/                       README screenshots (if present)
```

`PlainBytes.DocumentExtractor` references `PlainBytes.Core` via a project reference. Both projects must be present and keep their relative paths.

Microsoft Store / MSIX packaging projects are **not** part of this repository. Official installers are distributed via the Store only.

## Restore and build (CLI)

From the repository root (the folder that contains `PlainBytes.Core` and `PlainBytes.DocumentExtractor`):

```powershell
dotnet restore .\PlainBytes.DocumentExtractor\PlainBytes.DocumentExtractor.csproj
dotnet build   .\PlainBytes.DocumentExtractor\PlainBytes.DocumentExtractor.csproj -c Release -p:Platform=x64
```

Output (typical):

```text
PlainBytes.DocumentExtractor\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\
```

Run the generated `PlainBytes.DocumentExtractor.exe` from that folder (unpackaged). Unpackaged builds are not gated by the Microsoft Store trial or in-app purchase check.

### Visual Studio

1. Open `PlainBytes.DocumentExtractor\PlainBytes.DocumentExtractor.csproj` (or a solution that includes both this project and `PlainBytes.Core`).
2. Set configuration to **Release**, platform to **x64**.
3. Build and run the DocumentExtractor startup project.

## Dependencies

NuGet packages used by DocumentExtractor include:

| Package | Role |
| --- | --- |
| ClosedXML | Excel (`.xlsx`) export |
| UglyToad.PdfPig | PDF text and geometry |
| PdfiumViewer (+ native x64 package) | PDF preview |
| System.Drawing.Common | Imaging helpers |
| WPF-UI | Fluent-style controls |

`pdfium.dll` is copied next to the build output by a project target after build when the native package is restored.

### PdfPig package version

The project references:

```text
UglyToad.PdfPig Version="1.7.0-custom-5"
```

This is a normal NuGet `PackageReference`. Version `1.7.0-custom-5` is published on [nuget.org](https://www.nuget.org/packages/UglyToad.PdfPig/1.7.0-custom-5) and restores from the public feed like the other dependencies. The `custom-5` suffix is a pre-release / customized package version id, not a local-only package.

See also `THIRD_PARTY_NOTICES.md`.

## What this guide does not cover

- Creating or signing **MSIX / Microsoft Store** packages (Partner Center and local certificates)
- Publishing Store add-on / purchase configuration
- Building other PlainBytes Suite products that are not part of this repository drop

## Troubleshooting

| Issue | What to check |
| --- | --- |
| Restore fails on NuGet packages | Network access to nuget.org; correct .NET SDK; retry `dotnet restore` |
| Preview blank / PDF native errors | Confirm `pdfium.dll` was copied to the output directory; build **x64** |
| WPF / Windows TFM errors | Install .NET 8 SDK with Windows Desktop support |
| Project reference not found | Ensure `PlainBytes.Core` sits next to `PlainBytes.DocumentExtractor` as in the layout above |

## License

Source code is licensed under **GNU GPLv3**. See [LICENSE](./LICENSE).
