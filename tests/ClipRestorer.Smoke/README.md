# Verification

Synthetic Windows tests for snapshots, native payload copies, and scheduled restoration.
The runner starts a second copy of the same signed executable on a newly created,
nonvisible window station and desktop. Both identities and an empty clipboard are
checked before any test. Isolation failure aborts; there is no fallback to the
interactive clipboard. The creating parent never opens a clipboard.

Administrator rights are needed to create the uniquely named **test** station.
The library itself does not require elevation. Tests install/remove the keyboard
hook only on the private desktop and never call SendInput. Key matching and replay
construction are tested separately with synthetic events.

```powershell
dotnet build tests/ClipRestorer.Smoke/ClipRestorer.Smoke.csproj -c Release --artifacts-path artifacts/verify-jit
dotnet build tests/ClipRestorer.Smoke/ClipRestorer.Smoke.csproj -c Release -r win-x86 -p:PlatformTarget=x86 --artifacts-path artifacts/verify-x86
dotnet publish tests/ClipRestorer.Smoke/ClipRestorer.Smoke.csproj -c Release -r win-x64 -p:PublishAot=true --artifacts-path artifacts/verify-aot
```

Sign the built executables and managed assemblies before running where Application
Control requires it. Run the executable directly, elevated, with optional
`--report <absolute-path>`. Exit code 0 requires all included tests to pass.
Build success alone is not a runtime test result.

Coverage:

- Unicode/custom memory, empty snapshots, repeated restore, publication surviving
  snapshot disposal, additional calling threads, and manual backup lifecycle.
- Bitmap, palette, WMF/EMF, independent stream/storage copies, medium conversion,
  stream cursor/length preservation, and native ownership after source disposal.
- Indexed virtual-file content, complete descriptor/content groups, consumer
  mutation isolation, format enumeration, partial/total failures, allocation
  failures, source replacement, restore guards, and shutdown ownership.
- Timer restoration, stale-timer rejection, external copy cancellation, explicit
  cancellation, clipboard contention, backup retention, and manual retry.
- Pure input policy: exact shortcut modifiers, paired key-up suppression,
  repeated/held keys, installation while keys/buttons are held, self-injection
  markers, replay sequences, and passive menu/right-button notifications.

Additional integration coverage: UIPI, real Outlook/Office/browser providers, and
indexed virtual-file availability after process exit. The automated suite uses
no live user clipboard, keyboard, or mouse; manual results are recorded below.

Previous 2.0.0 signed execution results, 2026-11-09:

| Build | Result | Local report |
| --- | --- | --- |
| x64 JIT | Passed | `artifacts/smoke-jit-final.log` |
| x64 NativeAOT | Passed | `artifacts/smoke-aot-final.log` |
| x86 JIT | Passed | `artifacts/smoke-x86-final.log` |

Each 2.0.0 run passed 172 harness checks, 382 pure keyboard-policy assertions, and the
native-payload suite. Builds completed without warnings or errors. This is
synthetic coverage on the current Windows host, not a claim of every application
or Windows 10 configuration being verified.

[Window-station creation requirements](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-createwindowstationw).

## 3.0.0 input policy verification (2026-11-09)

Signed x64 JIT, x86 JIT, and x64 NativeAOT executions each passed 172 harness
checks, 6,426 pure input-policy assertions, and the native-payload suite.
Reports: `artifacts/smoke-v3-jit.log`, `artifacts/smoke-v3-x86.log`,
`artifacts/smoke-v3-aot.log`. Builds completed without warnings or errors.
The isolated lifecycle test now installs and removes both keyboard and mouse hooks.

Immediate inputs are Ctrl+V, Ctrl+Shift+V, Shift+Insert, and Win+V. AppsKey,
Shift+F10, and right-button presses pass through and request background restore.
Right-button movement/release is never suppressed or replayed. Physical Win
release is unchanged; a tagged unassigned-key pulse masks Start activation when
V is intercepted.

The user confirmed Explorer paste and correct contents for two indexed virtual
files. In the signed 3.0.0 tryout app, the user also reported normal operation of
Ctrl+V, Ctrl+Shift+V, Shift+Insert, Win+V, AppsKey, Shift+F10, right-click, and
cancellation after copying new content. The temporary app is outside this repo.

These are user-reported checks on this Windows host. Right-button dragging was
covered by policy tests, but no separate live drag result was reported. The manual
results do not establish timing guarantees for passive menus or compatibility
with every application, elevated window, or Windows 10 environment.
