# ClipRestorer

Windows clipboard snapshots and one shared temporary-clipboard restorer for .NET 10.
No UI framework or source-project dependency. Supports NativeAOT builds.

```csharp
using SevenKeyboard;

using var backup = ClipboardSnapshot.Capture();
// Use the clipboard temporarily.
ClipboardRestoreResult result = backup.Restore();
```

```csharp
ClipRestorer.CreateBackup();
// Write temporary clipboard content here.
var completion = ClipRestorer.ScheduleRestore(TimeSpan.FromSeconds(8));
// Paste/history shortcuts restore first; menu inputs pass through and request restore.
await completion;
```

`Restore()` restores immediately. `CancelRestore()` stops interception and keeps
its backup; `DeleteBackup()` cancels and releases it. A standalone snapshot can be
restored repeatedly. Disposing it does not invalidate separately published data.

Copies readable memory formats, bitmaps/palettes/metafiles, streams, structured
storage, and indexed virtual-file contents. A virtual-file group must be complete.
File-drop paths remain paths; the library does not copy files from disk. AHK's
linked/embedded-object exclusions remain, and OLE ownership metadata is regenerated.
Owner-drawn content and arbitrary application pointers cannot be preserved.

Partial failures are available through `ReadFailures` and `WriteFailures`;
`SkippedFormats` lists intentional exclusions. Total failure throws. Empty input
is valid, while nonempty input with nothing recoverable fails without clearing it.
Clipboard-open contention is retried for 500 ms by default. This does not impose a
time limit on another application's COM calls or streams.

A scheduled restore runs once. Partial success completes it; total failure keeps
the backup and interception for a later request. External copying cancels the old
restore. Observe `Completion` for the latest attempt, including key replay errors.
Ctrl+C/X are not intercepted. A held paste key is replayed once per press, and
replay can fail if modifiers change or Windows blocks input to an elevated app.

While a restore is pending:

| Input | Behavior |
| --- | --- |
| Ctrl+V, Ctrl+Shift+V, Shift+Insert, Win+V | Restore, then replay the original shortcut. |
| AppsKey, Shift+F10, right mouse button | Pass input unchanged; request background restoration. |

Menu contents can be built before restoration finishes. Right-button dragging is
never suppressed or replayed. Win+V waits for restoration before opening history;
temporary entries already recorded in history are not removed.

Clipboard work uses an internal STA; a separate small thread keeps keyboard and
passive mouse hooks responsive during OLE calls. No hooks are installed by
standalone snapshots.
Process-exit restoration is best effort. Indexed virtual files are intended for
use while the publishing process remains alive; post-exit persistence is not guaranteed.

C# is **3.0.0**: additional paste/history shortcuts are intercepted, and AppsKey
now passes through while requesting restoration instead of waiting for it.
The AHK branch remains **1.0.0**. See [verification](tests/ClipRestorer.Smoke/README.md)
for tested coverage and remaining limits.

API references: [Windows clipboard](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-operations),
[Shell data objects](https://learn.microsoft.com/windows/win32/shell/dataobject),
[OLE publication](https://learn.microsoft.com/windows/win32/api/ole2/nf-ole2-olesetclipboard),
[Chromium virtual-file capture](https://github.com/chromium/chromium/blob/f6fb968d9c264dbcd2a78833121f7031df70119e/ui/base/clipboard/clipboard_util_win.cc).
