# PureRDP UI redesign, 2026-10-13

Design read: a Windows remote-desktop utility for technical operators, with a calm,
legible native desktop language. Redesign-preserve, not a framework migration.
DESIGN_VARIANCE=3, MOTION_INTENSITY=1, VISUAL_DENSITY=6.

References: Leonxlnx/taste-skill (design-taste-frontend and redesign-existing-projects).
The main skill excludes dashboards and dense product forms. Apply its typography,
contrast, state, spacing and audit principles contextually; use the redesign skill
for the console and forms. This is native WinForms, not official Fluent UI React.

Audit:
- Console: decorative sky/cloud header consumes space; search and actions wrap
  without a shared grid. Tiny status text is hard to read; long names are clipped.
- Settings: mixed Segoe UI/Chinese fallback fonts, large header, fixed-dialog device
  page, inconsistent fields, no explanation of data storage and cleanup.
- Common buttons: no painted keyboard focus, disabled primary label has no explicit
  state. Align radii, borders, hover and pressed feedback.
- Retry dialog and overlay: colors diverge from common tokens. Retain overlay
  hit-testing, focus, double-click and all native RDP window transitions.

Implementation boundaries:
- Preserve green brand, device badge choices, page labels, field order, settings
  serialization, shortcut routing, RDP connection engine and window-state logic.
- Reference-selected pale mint pages, white cards and sky-to-mint console header,
  with readable text and stronger input outlines; shared Chinese
  UI typography, aligned forms, scrollable device
  settings with fixed left/right columns, readable status text. Narrow windows
  scroll instead of stacking columns. No new animation timer or web dependency.
- Data page reports primary and existing fallback locations. Opening and copying
  paths are explicit actions; no deletion. Credentials and startup registry entry
  require separate cleanup and are not ordinary data-directory files.
- Device settings use common row metrics, aligned label/input columns, rounded
  white section surfaces and theme-colored native CheckBox subclasses. TextBox
  editors are retained inside painted field outlines; password/caret/input remain
  native. LinkLabel is styled before its base Label case. Verify checkbox clicks,
  field value roundtrips and containment, not only screenshots.
- Publish only framework-dependent Windows x64. Release folder in
  D:\Agant\Github_Releases\PureRDP; source folder (including matching executable) in
  D:\Agant\Github_Source\PureRDP. Keep only the latest in those output roots,
  archiving previous PureRDP exports locally for recovery. No suffixes, ZIP or
  self-contained runtime in the default publication workflow.

Validation: build, existing regression probes, native control render snapshots and
layout checks at normal/small window sizes. These do not prove live RDP performance
or field shortcut behavior. No real RDP login or user settings save in UI probes.
