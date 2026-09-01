# Slate Control Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved Slate Control layout in the WPF application without changing optimization behavior.

**Architecture:** Keep the existing code-behind contract and rebuild the visual tree around reusable theme resources, a left-positioned `TabControl`, compact action cards, and progressive disclosure in the brightness window. Structural tests protect required named controls and group boundaries, while markup compilation protects WPF wiring.

**Tech Stack:** .NET 10, WPF XAML, xUnit, System.Xml.Linq

**Spec:** `docs/superpowers/specs/2026-08-31-slate-control-redesign.md`

## Global Constraints

- Preserve every existing `x:Name` used by code-behind.
- Preserve all existing button, checkbox, slider, and mouse event handlers.
- Keep the floating brightness window and its topmost behavior.
- Use Ink `#0B0E13`, Slate `#121720`, Graphite `#293241`, Signal Lime `#B7F34A`, Signal Cyan `#5EEAD4`, and Paper `#F4F7FB` as the dark reference palette.
- Keep `net10.0-windows10.0.26100.0` and `SupportedOSPlatformVersion` `10.0.19041.0`.

---

### Task 1: Structural visual contract

**Files:**
- Create: `PCOptimizer.Tests/VisualStructureTests.cs`
- Test: `PCOptimizer.Tests/VisualStructureTests.cs`

**Interfaces:**
- Consumes: repository XAML files resolved from the test output directory.
- Produces: assertions for six navigation headers and the complete required `x:Name` set.

- [ ] Write tests that parse `MainWindow.xaml` and `Views/BrightnessWindow.xaml` with `XDocument`, assert the six approved navigation header values, and assert required named controls are unique.
- [ ] Run `dotnet test PCOptimizer.Tests/PCOptimizer.Tests.csproj -c Release` and confirm failure because the current main window does not contain the approved six-area navigation.

### Task 2: Theme token dictionaries

**Files:**
- Modify: `PCOptimizer/Themes/DarkTheme.xaml`
- Modify: `PCOptimizer/Themes/LightTheme.xaml`
- Test: `PCOptimizer.Tests/VisualStructureTests.cs`

**Interfaces:**
- Consumes: existing resource keys referenced from code-generated controls.
- Produces: shared Slate Control keys including `NavBg`, `SurfaceRaised`, `AccentBrush`, `AccentMuted`, `SignalCyan`, `DangerBrush`, and the existing compatibility keys.

- [ ] Replace gradient-heavy theme resources with solid Slate Control tokens while retaining all legacy resource keys.
- [ ] Run the structural tests and WPF build to verify dictionaries load and all dynamic resource references resolve.

### Task 3: Main window composition

**Files:**
- Modify: `PCOptimizer/MainWindow.xaml`
- Modify only if required for navigation initialization: `PCOptimizer/MainWindow.xaml.cs`
- Test: `PCOptimizer.Tests/VisualStructureTests.cs`

**Interfaces:**
- Consumes: existing named controls and event handlers in `MainWindow.xaml.cs`.
- Produces: six-area left navigation, persistent execution bar, overview action cards, grouped optimization rows, and isolated expert surface.

- [ ] Rebuild `MainWindow.xaml` with compact reusable styles and all required named controls.
- [ ] Run the structural tests until the six-area and named-control contract passes.
- [ ] Run `dotnet build PCOptimizer.sln -c Release -warnaserror` to validate WPF event wiring.

### Task 4: Floating brightness composition

**Files:**
- Modify: `PCOptimizer/Views/BrightnessWindow.xaml`
- Modify only when required for unchanged behavior: `PCOptimizer/Views/BrightnessWindow.xaml.cs`
- Test: `PCOptimizer.Tests/VisualStructureTests.cs`

**Interfaces:**
- Consumes: dynamic monitor rows created by `BrightnessWindow.xaml.cs` and all current named controls.
- Produces: compact always-on-top display panel with visible monitor/HDR controls and grouped secondary sections.

- [ ] Rebuild the floating window around compact monitor, preset, color, display, remote, timer, and hotkey sections.
- [ ] Run structural tests and the WPF build to verify names and handlers.

### Task 5: Visual and release verification

**Files:**
- Review: `PCOptimizer/MainWindow.xaml`
- Review: `PCOptimizer/Views/BrightnessWindow.xaml`
- Review: `PCOptimizer/Themes/DarkTheme.xaml`
- Review: `PCOptimizer/Themes/LightTheme.xaml`

**Interfaces:**
- Consumes: compiled Debug and Release executables.
- Produces: verified layout at runtime and a clean release build.

- [ ] Launch the Debug build, inspect the main and floating windows at desktop scale, and correct clipping, contrast, alignment, or excessive density.
- [ ] Run the full Release test suite.
- [ ] Run Release build with warnings treated as errors.
- [ ] Run `git diff --check` and review that no technical behavior was unintentionally removed.
