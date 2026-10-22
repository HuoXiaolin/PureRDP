# UI design regression probe

Use the isolated-desktop runner (also builds the application):

```powershell
./Build/scripts/purerdp.test.ps1 -Suite UiDesign
```

Renders eight global-settings pages, device settings at two window sizes,
console empty state, a fixture device card and retry dialog into
`outputs/ui-review/`. Test windows run on a separate desktop that is never switched to. It verifies retained
fields/password protection, scrollable settings, data-directory reporting,
non-destructive controls and text contrast. It never saves settings, deletes
data, registers startup or establishes a remote connection. Startup registration
and saved device lists may be read, but device snapshots use a reserved test IP.

Screenshots are actual WinForms controls, not mockups. They do not establish
real RDP shortcut behavior, performance, every monitor's DPI or accessibility
coverage. Visually inspect the renders before release.
