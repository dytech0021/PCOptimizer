# Slate Control Redesign

## Objective

Replace the crowded PC Optimizer presentation with a compact control-console layout while preserving every existing optimization, event handler, saved setting, and the floating brightness window.

## Visual system

- Ink `#0B0E13`: application background.
- Slate `#121720`: primary surfaces.
- Graphite `#293241`: boundaries and inactive controls.
- Signal Lime `#B7F34A`: operational state, selection, progress, and primary actions.
- Signal Cyan `#5EEAD4`: display/color context and secondary live state.
- Paper `#F4F7FB`: primary text.
- Display role: Segoe UI Variable Display, semibold, tightly spaced.
- Body role: Segoe UI Variable Text / Segoe UI.
- Utility role: Cascadia Mono for versions, percentages, status codes, and measurements.

The signature element is the signal spine: a narrow lime indicator attached to the currently selected navigation area and to the primary system-state card. It makes active state legible without gradients or decorative glow.

## Main window

The window becomes a wide desktop console with a compact header, a left-side six-area navigator, a scrollable work surface, and a persistent bottom execution bar.

```text
┌────────────────────────────────────────────────────────────┐
│ PC Optimizer / version                     screen theme    │
├──────────────┬─────────────────────────────────────────────┤
│ Overview     │ system state + immediate actions            │
│ Display      │ grouped controls for selected area          │
│ Performance │                                             │
│ System       │                                             │
│ Security     │                                             │
│ Expert       │                                             │
├──────────────┴─────────────────────────────────────────────┤
│ selection · progress · activity · Run optimizations        │
└────────────────────────────────────────────────────────────┘
```

- Overview contains the eight immediate actions already present in the application.
- Display & Color leads to the floating brightness panel and monitor maximization.
- Performance groups gaming and performance-related checkboxes.
- System groups cleanup, repair, startup, and maintenance actions.
- Security groups Defender/junkware, privacy, background applications, and core isolation.
- Expert remains visually isolated and retains every GPU/CPU advanced control.
- Quick presets remain above the execution bar, expressed as compact text controls instead of large decorative tiles.

## Floating brightness window

The floating window remains always-on-top and functionally identical. It becomes narrower and uses progressive disclosure:

- monitor brightness and HDR remain visible immediately;
- presets become a single compact row;
- night light, advanced color, displays, remote mode, timer, and hotkey are grouped into quiet disclosure sections;
- all dynamic `x:Name` controls and code-behind contracts remain unchanged.

## Behavior and compatibility

- No command, checkbox, slider, button event, or saved setting is removed.
- Existing code-behind names are preserved to avoid a behavioral rewrite.
- Dark and light dictionaries expose the same resource keys; dark is the reference aesthetic and light is a restrained high-contrast counterpart.
- Keyboard focus remains visible. Animations are limited to short opacity/background transitions.
- Minimum supported platform remains Windows 10 build 19041; target remains .NET 10 / Windows SDK 26100.

## Validation

- A structural XAML test loads both redesigned views as XML and verifies the required navigation groups and named controls.
- WPF markup compilation must succeed with warnings treated as errors.
- Existing unit tests must remain green.
- Release UI is launched with the non-admin Debug manifest for a visual smoke test and screenshots are inspected at normal desktop scale.
