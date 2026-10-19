# BookHub

BookHub is a Windows desktop book catalog and reader built with WPF and .NET 9.
It uses PDFium for PDF rendering and supports several document formats.

## Requirements

- Windows x64
- .NET 9 SDK to build
- .NET 9 Desktop Runtime to run a framework-dependent portable build

## Build

```powershell
dotnet restore .\BookHub.sln
dotnet build .\BookHub.sln -c Release
```

To create the portable x64 folder:

```powershell
.\Publish-Portable.ps1 -Runtime win-x64 -Configuration Release
```

The portable build is framework-dependent and requires the .NET 9 Desktop
Runtime. The publishing script excludes the local sample library by default.
Only use `-IncludeSampleLibrary` when you have verified redistribution rights
for every included item.

## License

The BookHub source is licensed under the MIT License; see [LICENSE](LICENSE).
Third-party dependencies and their declared licenses are listed in
[THIRD_PARTY_LICENSES.txt](THIRD_PARTY_LICENSES.txt).

Do not add books, documents, databases, settings, backups, or published build
outputs to source control. The sample library and local user data are excluded
by `.gitignore` and are not included in portable builds unless explicitly
requested.
