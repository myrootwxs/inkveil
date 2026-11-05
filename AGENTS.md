# Repository Guidelines

## Project Structure & Module Organization

- `src/`: WPF application source. `MainWindow.cs` contains UI and interaction logic; `NovelBook.cs` handles TXT parsing and encoding; `ReaderSettings.cs` persists preferences; `NativeMethods.cs` contains Win32 interop.
- `tests/`: Framework-free console tests for parsing, encoding, and core behavior.
- `assets/`: Application icon. `samples/`: Example novel. `tools/`: Build-time helper scripts.
- `dist/`: Generated executable and release artifacts; ignored by Git.
- `.github/workflows/`: GitHub Actions build and test workflow.

## Build, Test, and Development Commands

- `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test`: Build the app and run all core tests.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`: Build only.
- `.\dist\Inkveil.exe`: Launch the generated application.
- `MSBuild src\Inkveil.csproj /t:Rebuild /p:Configuration=Release`: Build through the project file.
- Never commit generated files from `dist/`, `bin/`, or `obj/`.

## Coding Style & Naming Conventions

- Use C# 5 syntax compatible with .NET Framework 4.8 and WPF.
- Use four-space indentation, PascalCase for types/members, camelCase for parameters, and `_camelCase` for private fields.
- Keep code identifiers in English. Write comments and documentation in Chinese unless a file is intentionally English.
- Place P/Invoke declarations in `NativeMethods.cs`; keep UI work on the WPF dispatcher thread.
- Keep the project dependency-free: no NuGet, Electron, or external UI framework unless explicitly approved.
- Save PowerShell scripts as UTF-8 with BOM. Keep C# source UTF-8 and preserve `<CodePage>65001</CodePage>`.

## Testing Guidelines

- Add focused tests to `tests/CoreTests.cs`; use `Test...` method names and deterministic assertions.
- Run `.\build.ps1 -Test` before every commit. All existing tests must pass.
- For UI changes, manually verify hover reveal, mouse-leave transparency, click-through behavior, chapter shortcuts, font selection, and opacity controls.
- Test with both `samples\雨巷书店.txt` and at least one large real TXT file.

## Commit & Pull Request Guidelines

- Follow Conventional Commits with concise Chinese descriptions, for example: `fix: 修复 Alt 方向键切章`, `perf: 延迟加载系统字体列表`.
- Keep commits scoped to one logical change. Do not amend or force-push shared branches.
- Pull requests should include purpose, linked issue (if any), test results, and screenshots or a short recording for visual changes.
- Document manual verification steps and note any configuration impact, such as changes to `%APPDATA%\Inkveil\settings.json`.

When adding another `AGENTS.md` or `CLAUDE.md` in a subdirectory, add its `@path` reference to this file.
