# File Organizer by Limited Underground

An open-source, installed file-management application planned for Windows 11, with Linux as a future platform. Official distribution will be free. Personal and commercial use and community modifications are welcome under [Apache License 2.0](LICENSE).

**V1 development checkpoint:** the owner authorized a local desktop shell, application lifecycle and SQLite bootstrap on October 29, 2026. There is no file-management engine, installer or release. See [project status](docs/PROJECT_STATUS.md) for current validation and limits.

Confirmed baseline: C#, .NET 10, SQLite, offline core operation. Avalonia is the preferred UI, subject to technical evaluation.

Development requires .NET SDK 10.0.303 and PowerShell 7.2+. Run `./tools/Test-Development.ps1` for locked restore, Debug/Release builds and tests. Launch `dotnet run --project src/FileOrganizer.Desktop` for the shell. Normal application state uses the per-user LocalApplicationData convention; developer smoke runs require the marked disposable fixture described in [project status](docs/PROJECT_STATUS.md). Closing the window stops the in-process worker. No folders are scanned.

Start with [project status](docs/PROJECT_STATUS.md), then [requirements](docs/REQUIREMENTS.md), [technology evaluation](docs/TECHNOLOGY.md), [architecture](docs/ARCHITECTURE.md), [roadmap](docs/ROADMAP.md), and [benchmark plan](docs/BENCHMARKS.md). [Contributing](CONTRIBUTING.md) owns development and maintenance rules. [Publication proposal](docs/PUBLICATION.md) owns the proposed GitHub setup.
