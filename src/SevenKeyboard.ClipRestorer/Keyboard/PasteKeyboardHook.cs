using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

/// <summary>Owns keyboard and passive mouse hooks on a dedicated message-loop thread; never uses the clipboard STA.</summary>
/// <remarks>
/// Enable/Disable acknowledge the policy change synchronously. onPaste runs outside
/// the native hook callback and MUST only enqueue actor work: no Invoke, waits, OLE,
/// or Replay. A callback already queued can outlive Disable/re-enable; the parent
/// must validate its pending operation/generation on the actor before restoring.
/// Disable and Shutdown retain the hook until every swallowed key has its matching
/// up. Shutdown is terminal, but does not wait for a user to release a held key.
/// </remarks>
internal static class PasteKeyboardHook
{
    private static readonly object LifecycleGate = new(), ReplayGate = new();
    private static Worker? _worker;
    private static volatile bool _shutdown;
    [ThreadStatic] private static Worker? _current;
    [ThreadStatic] private static bool _replaying;

    internal static void Enable(Action<PasteKey> onPaste)
    {
        ArgumentNullException.ThrowIfNull(onPaste);
        Worker worker;
        lock (LifecycleGate)
        {
            ThrowIfShutdown();
            worker = _worker ??= new Worker();
        }
        worker.Invoke(() => worker.Enable(onPaste));
    }

    internal static void Disable()
    {
        Worker? worker;
        lock (LifecycleGate) worker = _worker;
        worker?.Invoke(worker.Disable, allowStopped: true);
    }

    internal static void Dispose() => Shutdown();

    internal static void Shutdown()
    {
        Worker? worker;
        lock (LifecycleGate)
        {
            _shutdown = true;
            worker = _worker; // Do not initialize a thread/desktop/hook just to stop it.
        }
        worker?.Invoke(worker.Shutdown, allowStopped: true);
    }

    /// <summary>Called on the clipboard actor after successful restore and Disable.</summary>
    /// <remarks>
    /// SendInput runs on the caller, never the hook worker. It reuses an existing
    /// modifier, adding paired downs/ups only for missing required modifiers.
    /// Unexpected modifiers are rejected rather than sending a different shortcut.
    /// Menu inputs are never replayed. State sampling and injection are not atomic with
    /// physical input; a modifier/focus change during restore cannot be undone.
    /// SendInput can be blocked by UIPI and may fail after inserting part of a
    /// gesture. Report the error; never automatically replay the entire batch.
    /// </remarks>
    internal static unsafe void Replay(PasteKey key)
    {
        if (_current is not null || _replaying)
            throw new InvalidOperationException("Replay must run on the clipboard actor, outside hook callbacks and another Replay.");
        lock (ReplayGate)
        {
            ThrowIfShutdown();
            _replaying = true;
            try
            {
                Span<HookNative.Input> inputs = stackalloc HookNative.Input[6];
                int count = PasteReplayPolicy.Build(key, ReadModifiers(), inputs);
                uint sent;
                fixed (HookNative.Input* pointer = inputs)
                    sent = HookNative.SendInput((uint)count, pointer, sizeof(HookNative.Input));
                if (sent == count) return;
                int error = Marshal.GetLastPInvokeError();
                // Release only our reported, unmatched synthetic downs. In
                // particular the held-Ctrl path never generates any Ctrl up.
                ReleasePartial(inputs[..count], sent);
                string message = $"SendInput inserted {sent} of {count} paste events; the gesture may be incomplete.";
                if (error != 0) throw new Win32Exception(error, message);
                throw new InvalidOperationException(message);
            }
            finally { _replaying = false; }
        }
    }

    private static unsafe void ReleasePartial(ReadOnlySpan<HookNative.Input> inputs, uint sent)
    {
        if (sent == 0 || sent >= inputs.Length) return;
        Span<HookNative.Input> release = stackalloc HookNative.Input[3];
        int count = 0;
        // Reverse order releases the key before any modifiers that this batch introduced.
        for (int i = (int)sent - 1; i >= 0; i--)
        {
            HookNative.KeyboardInput down = inputs[i].Data.Keyboard;
            if ((down.Flags & HookNative.KeyUp) != 0) continue;
            bool alreadyUp = false;
            for (int j = i + 1; j < sent; j++)
                if (inputs[j].Data.Keyboard.VirtualKey == down.VirtualKey &&
                    (inputs[j].Data.Keyboard.Flags & HookNative.KeyUp) != 0) alreadyUp = true;
            if (!alreadyUp) release[count++] = PasteReplayPolicy.Key(down.VirtualKey, true);
        }
        if (count != 0)
            fixed (HookNative.Input* pointer = release)
                _ = HookNative.SendInput((uint)count, pointer, sizeof(HookNative.Input));
    }

    private static void ThrowIfShutdown()
    {
        if (_shutdown) throw new ObjectDisposedException(nameof(PasteKeyboardHook));
    }

    private static bool IsDown(int vk) => HookNative.GetAsyncKeyState(vk) < 0;

    private static PasteModifiers ReadModifiers()
    {
        PasteModifiers result = PasteModifiers.None;
        if (IsDown(HookNative.VkControl)) result |= PasteModifiers.Control;
        if (IsDown(HookNative.VkShift)) result |= PasteModifiers.Shift;
        if (IsDown(HookNative.VkAlt)) result |= PasteModifiers.Alt;
        if (IsDown(HookNative.VkLWin) || IsDown(HookNative.VkRWin)) result |= PasteModifiers.Windows;
        return result;
    }

    private sealed unsafe class Worker
    {
        private readonly nint _desktop;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake;
        private readonly object _submissionGate = new();
        private readonly ConcurrentQueue<Command> _commands = new();
        private readonly Queue<(Action<PasteKey> Callback, PasteKey Key)> _notifications = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PasteHookPolicy _policy = new();
        private Action<PasteKey>? _onPaste;
        private nint _hook, _mouseHook;
        private bool _stopped, _shuttingDown, _exit;
        private Exception? _failure;

        internal Worker()
        {
            // The caller may be attached to an isolated nonvisible desktop.
            // Desktop handles returned by GetThreadDesktop are borrowed.
            _desktop = HookNative.GetThreadDesktop(HookNative.GetCurrentThreadId());
            if (_desktop == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _wake = new AutoResetEvent(false);
            _thread = new Thread(Run) { IsBackground = true, Name = "ClipRestorer input" };
            try { _thread.Start(); }
            catch { _wake.Dispose(); throw; }
            _started.Task.GetAwaiter().GetResult();
        }

        internal void Invoke(Action action, bool allowStopped = false)
        {
            if (_current == this) { action(); return; }
            Command command = new(action);
            lock (_submissionGate)
            {
                if (_stopped)
                {
                    if (allowStopped) return;
                    throw new InvalidOperationException("The keyboard worker has stopped.", _failure);
                }
                _commands.Enqueue(command);
                _wake.Set();
            }
            command.Completion.Task.GetAwaiter().GetResult();
        }

        internal void Enable(Action<PasteKey> callback)
        {
            ThrowIfShutdown();
            if (_shuttingDown) throw new ObjectDisposedException(nameof(PasteKeyboardHook));
            try
            {
                nint module = HookNative.GetModuleHandleW(null);
                if (module == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (_hook == 0)
                {
                    _hook = HookNative.SetWindowsHookExW(13,
                        (nint)(delegate* unmanaged[Stdcall]<int, nuint, nint, nint>)&HookProc, module, 0);
                    if (_hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    // Seed AFTER installation, before pumping hooks. An already held
                    // key must keep passing; hooked downs have not updated async state.
                    _policy.Initialize(IsDown(HookNative.VkV), IsDown(HookNative.VkApps),
                        IsDown(HookNative.VkInsert), IsDown(HookNative.VkF10));
                }
                if (_mouseHook == 0)
                {
                    _mouseHook = HookNative.SetWindowsHookExW(14,
                        (nint)(delegate* unmanaged[Stdcall]<int, nuint, nint, nint>)&MouseHookProc, module, 0);
                    if (_mouseHook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    _policy.InitializeMouse(IsDown(HookNative.VkRButton));
                }
                _onPaste = callback;
                _policy.Enabled = true;
            }
            catch
            {
                // Failed installation must not leave an active partial subscription.
                _policy.Enabled = false;
                _onPaste = null;
                FinishDisable();
                throw;
            }
        }
        internal void Disable()
        {
            _policy.Enabled = false;
            _onPaste = null;
            FinishDisable();
        }

        internal void Shutdown()
        {
            _shuttingDown = true;
            _notifications.Clear();
            Disable();
        }

        private void FinishDisable()
        {
            // Mouse events are never swallowed, so there is no matching-up debt.
            if (!_policy.Enabled && _mouseHook != 0)
            {
                if (HookNative.UnhookWindowsHookEx(_mouseHook) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                _mouseHook = 0;
            }
            if (!_policy.Enabled && !_policy.HasSwallowedKeys && _hook != 0)
            {
                if (HookNative.UnhookWindowsHookEx(_hook) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                _hook = 0;
            }
            if (_shuttingDown && _hook == 0 && _mouseHook == 0) _exit = true;
        }

        private void Run()
        {
            try
            {
                // Do this before the first message queue, window, or hook API.
                if (HookNative.SetThreadDesktop(_desktop) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                _current = this;
                HookNative.PeekMessageW(out _, 0, 0, 0, 0);
                nint wakeHandle = _wake.SafeWaitHandle.DangerousGetHandle();
                _started.TrySetResult();
                while (!_exit)
                {
                    // The event wakes commands AND hook housekeeping. Unlike
                    // PostThreadMessage it cannot lose a wake to queue quota.
                    uint wait = HookNative.MsgWaitForMultipleObjectsEx(1, &wakeHandle, uint.MaxValue, 0x04FF, 4);
                    if (wait == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    // Bounded batches keep native hook delivery responsive even
                    // if the caller is submitting commands continuously.
                    for (int i = 0; i < 64 && HookNative.PeekMessageW(out var message, 0, 0, 0, 1) != 0; i++)
                    {
                        if (message.Id == 0x0012) { Shutdown(); continue; }
                        HookNative.TranslateMessage(in message);
                        HookNative.DispatchMessageW(in message);
                    }
                    for (int i = 0; i < 64 && _commands.TryDequeue(out Command? command); i++) command.Run();
                    for (int i = 0; i < 64 && _notifications.TryDequeue(out var notification); i++)
                    {
                        // Parent callback is intentionally outside HookProc. It
                        // only Posts to the actor; it must never wait on that STA.
                        try { notification.Callback(notification.Key); }
                        catch (Exception error)
                        {
                            // A broken parent callback must not kill the hook or
                            // strand a swallowed key. A later press can retry.
                            System.Diagnostics.Trace.TraceError("Paste callback failed: {0}", error);
                        }
                    }
                    FinishDisable();
                    if (!_commands.IsEmpty || _notifications.Count != 0) _wake.Set();
                }
            }
            catch (Exception error)
            {
                _failure = error;
                _started.TrySetException(error);
            }
            finally
            {
                if (_hook != 0) HookNative.UnhookWindowsHookEx(_hook);
                if (_mouseHook != 0) HookNative.UnhookWindowsHookEx(_mouseHook);
                _hook = _mouseHook = 0;
                _current = null;
                _onPaste = null;
                _notifications.Clear();
                lock (_submissionGate)
                {
                    _stopped = true;
                    while (_commands.TryDequeue(out Command? command))
                        command.Completion.TrySetException(new InvalidOperationException("The keyboard worker has stopped.", _failure));
                    _wake.Dispose();
                }
            }
        }

        // https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static nint HookProc(int code, nuint message, nint data)
        {
            bool suppress = false;
            try
            {
                Worker? worker = _current;
                if (code == 0 && worker is not null && data != 0 &&
                    (message == 0x0100 || message == 0x0101 || message == 0x0104 || message == 0x0105))
                {
                    HookNative.KeyboardEvent key = *(HookNative.KeyboardEvent*)data;
                    if (key.ExtraInfo != HookNative.InjectionMarker &&
                        (key.VirtualKey is HookNative.VkV or HookNative.VkApps or HookNative.VkInsert or HookNative.VkF10))
                    {
                        bool down = message == 0x0100 || message == 0x0104;
                        // GetAsyncKeyState is pre-event in a LL hook. That is
                        // appropriate here: modifiers are NOT the changing key.
                        PasteModifiers modifiers = down ? ReadModifiers() : 0;
                        if ((key.Flags & 0x20) != 0) modifiers |= PasteModifiers.Alt;
                        PasteHookDecision decision = worker._policy.Process(key.VirtualKey, down, modifiers, key.ExtraInfo);
                        suppress = decision.Suppress;
                        if (decision.Paste == PasteKey.WindowsV) MaskWindowsTap();
                        worker.QueueNotification(decision);
                        if (suppress && !worker._policy.Enabled && !worker._policy.HasSwallowedKeys)
                            worker._wake.Set(); // Uninstall in the loop, after this callback returns.
                    }
                }
            }
            catch { /* Never unwind through native code; retain an already chosen suppression. */ }
            return suppress ? 1 : HookNative.CallNextHookEx(0, code, message, data);
        }

        // An intercepted V must still mark the physical Win press as a chord.
        // Otherwise releasing Win before restoration/replay can open Start.
        // As in PowerToys, use one tagged, unassigned-key batch inside the hook;
        // unlike ordinary replay this never waits on the actor or takes ReplayGate.
        private static void MaskWindowsTap()
        {
            try
            {
                Span<HookNative.Input> mask = stackalloc HookNative.Input[2];
                mask[0] = PasteReplayPolicy.Key(0xE8, false); // unassigned VK
                mask[1] = PasteReplayPolicy.Key(0xE8, true);
                uint sent;
                fixed (HookNative.Input* pointer = mask)
                    sent = HookNative.SendInput(2, pointer, sizeof(HookNative.Input));
                if (sent == 2) return;
                if (sent == 1)
                {
                    HookNative.Input release = mask[1];
                    _ = HookNative.SendInput(1, &release, sizeof(HookNative.Input));
                }
                System.Diagnostics.Trace.TraceError("Could not fully mask the intercepted Win press ({0}/2 events).", sent);
            }
            catch (Exception error)
            {
                // Still enqueue restoration: never strand an already swallowed V.
                System.Diagnostics.Trace.TraceError("Win-key masking failed: {0}", error);
            }
        }

        private void QueueNotification(PasteHookDecision decision)
        {
            if (decision.Paste is PasteKey key && _onPaste is Action<PasteKey> callback)
            {
                _notifications.Enqueue((callback, key));
                _wake.Set();
            }
        }

        // Never suppress or synthesize mouse input: right-button drag remains intact.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static nint MouseHookProc(int code, nuint message, nint data)
        {
            try
            {
                Worker? worker = _current;
                if (code == 0 && worker is not null && data != 0 &&
                    (message == 0x0204 || message == 0x0205))
                {
                    HookNative.MouseEvent mouse = *(HookNative.MouseEvent*)data;
                    worker.QueueNotification(worker._policy.ProcessMouse(message == 0x0204, mouse.ExtraInfo));
                }
            }
            catch { /* A passive notification must never affect the user's mouse input. */ }
            return HookNative.CallNextHookEx(0, code, message, data);
        }

        private sealed class Command(Action action)
        {
            internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void Run()
            {
                try { action(); Completion.TrySetResult(); }
                catch (Exception error) { Completion.TrySetException(error); }
            }
        }
    }
}
