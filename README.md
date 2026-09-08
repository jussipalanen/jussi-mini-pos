# JussiMiniPos

A small point-of-sale (POS) desktop application built with WPF on .NET 10.

> **Status:** early scaffolding. The project currently contains the initial WPF
> application shell (`App.xaml`, `MainWindow.xaml`); POS features are not
> implemented yet.

## Requirements

- **Windows** (WPF does not run on Linux or macOS)
- **.NET SDK 10.0** or newer — <https://dotnet.microsoft.com/download>
- Optional: Visual Studio 2026 or JetBrains Rider for XAML designer support

Verify the SDK is installed and on your `PATH`:

```powershell
dotnet --version      # should print 10.x
```

## Running the application locally

### 1. Get the source

```powershell
git clone git@github.com:jussipalanen/jussi-mini-pos.git
cd jussi-mini-pos
```

### 2. Restore dependencies

```powershell
dotnet restore
```

### 3. Build and run

```powershell
dotnet run
```

The WPF main window opens. `dotnet run` builds first, so a separate
`dotnet build` is only needed when you want to compile without launching.

Stop the app by closing its window, or with `Ctrl+C` in the terminal.

### Running from Visual Studio

1. Open `JussiMiniPos.csproj` (File → Open → Project/Solution).
2. Make sure `JussiMiniPos` is the startup project.
3. Press `F5` to run with the debugger, or `Ctrl+F5` to run without it.

### Running the compiled executable directly

After a build, the executable is in the build output folder:

```powershell
dotnet build
.\bin\Debug\net10.0-windows\JussiMiniPos.exe
```

### Release build

```powershell
dotnet run -c Release
# or produce a distributable folder:
dotnet publish -c Release -r win-x64 --self-contained false
```

The published output lands in `bin\Release\net10.0-windows\win-x64\publish\`.

### Troubleshooting

| Problem | Fix |
| ------- | --- |
| `dotnet` is not recognized | Install the .NET 10 SDK and reopen the terminal |
| `NETSDK1045: The current .NET SDK does not support targeting net10.0` | Your SDK is older than 10.0 — upgrade it |
| Build errors after pulling changes | `dotnet clean` then `dotnet restore` |
| The app builds but no window appears | Check `StartupUri` in `App.xaml` points at `MainWindow.xaml` |

## Project layout

| Path                   | Purpose                                       |
| ---------------------- | --------------------------------------------- |
| `JussiMiniPos.csproj`  | Project file (`net10.0-windows`, WPF enabled) |
| `App.xaml(.cs)`        | Application entry point and global resources  |
| `MainWindow.xaml(.cs)` | Main application window                       |
| `AssemblyInfo.cs`      | Assembly-level theme configuration            |

Build output (`bin/`, `obj/`) is generated locally and is not tracked in git.

## License

Not yet specified.
