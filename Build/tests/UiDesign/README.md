# UI design regression probe

Use the isolated-desktop runner (also builds the application):

```powershell
./Build/scripts/purerdp.test.ps1 -Suite UiDesign
```

Renders nine global-settings pages, device settings at two window sizes,
console empty state, a fixture device card and retry dialog into
`outputs/ui-review/`. Test windows run on a separate desktop that is never switched to. It verifies retained
fields/password protection, scrollable settings, data-directory reporting,
non-destructive controls and text contrast. It never saves settings, deletes
data, registers startup or establishes a remote connection. Startup registration
and saved device lists may be read, but device snapshots use a reserved test IP.

Also renders the native six-layer crystal loading surface (adapted from the
user-supplied Uiverse.io / Z4drus design) at normal and small window sizes.
Checks immediate cancellation, unobstructed loading-panel order, different
animation phases and timer stop/resume for minimize, completion and disposal.
Loading-panel snapshots are captured directly to avoid Form.DrawToBitmap
printing the native RDP sibling over its foreground overlay.

File-opening checks cover the console entry's event dispatch and toolbar
geometry at four window widths, plus both settings actions. Tests never open
the file chooser or Windows Default Apps, and never change file associations.

Screenshots are actual WinForms controls, not mockups. They do not establish
real RDP shortcut behavior, performance, every monitor's DPI or accessibility
coverage. Visually inspect the renders before release.
