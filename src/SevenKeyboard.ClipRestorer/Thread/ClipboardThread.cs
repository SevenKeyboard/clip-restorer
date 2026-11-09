using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

// All OLE/clipboard operations live on one STA, without a UI framework.
internal static class ClipboardThread
{
    private static readonly Lazy<Worker> Instance = new(() => new Worker());
    internal static bool IsCurrent => Instance.IsValueCreated && Instance.Value.ThreadId == ThreadNative.GetCurrentThreadId();
    internal static nint Window => Instance.Value.Window;
    internal static event Action? ClipboardChanged;
    internal static event Action? TimerElapsed;
    internal static event Action? Exiting;
    private static nuint _timerId, _nextTimerId;
    internal static nuint TimerId => _timerId;

    internal static T Invoke<T>(Func<T> action)
    {
        Worker worker = Instance.Value;
        if (IsCurrent) return action();
        return worker.Enqueue(action).GetAwaiter().GetResult();
    }
    internal static void Invoke(Action action) => Invoke(() => { action(); return 0; });
    internal static void Post(Action action) => Instance.Value.Post(action);
    internal static void StartTimer(uint milliseconds)
    {
        StopTimer();
        if (++_nextTimerId == 0) ++_nextTimerId;
        if (ThreadNative.SetTimer(Window, _nextTimerId, Math.Max(1, milliseconds), 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        _timerId = _nextTimerId;
    }
    internal static void StopTimer()
    {
        nuint old = _timerId;
        _timerId = 0;
        if (old != 0 && Instance.IsValueCreated) ThreadNative.KillTimer(Window, old);
    }

    private sealed unsafe class Worker
    {
        private readonly Queue<Command> _queue = new();
        private readonly object _submissionGate = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private readonly nint _desktop;
        private bool _draining;
        private volatile bool _stopped;
        private static Worker? _active;
        internal uint ThreadId;
        internal nint Window;
        internal Worker()
        {
            _desktop = ThreadNative.GetThreadDesktop(ThreadNative.GetCurrentThreadId());
            _thread = new Thread(Run) { IsBackground = true, Name = "ClipRestorer STA" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _started.Task.GetAwaiter().GetResult();
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        internal Task<T> Enqueue<T>(Func<T> action)
        {
            if (_stopped) throw new InvalidOperationException("The clipboard worker has stopped.");
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Submit(new Command(() => completion.TrySetResult(action()),
                error => completion.TrySetException(error)));
            return completion.Task;
        }
        internal void Post(Action action) => Submit(new Command(action, null));
        private void Submit(Command command)
        {
            lock (_submissionGate)
            {
                if (_stopped) throw new InvalidOperationException("The clipboard worker has stopped.");
                // A failed wake never leaves executable work behind. Drain shares
                // this short lock, so it cannot consume the wake before enqueue.
                if (ThreadNative.PostMessageW(Window, 0x8001, 0, 0) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                _queue.Enqueue(command);
            }
        }
        private sealed record Command(Action Run, Action<Exception>? Fail)
        {
            internal void Reject(Exception error)
            {
                if (Fail is not null) Fail(error);
                else System.Diagnostics.Trace.TraceError("Clipboard notification failed: {0}", error);
            }
        }
        private void Drain()
        {
            if (_draining) return; // COM may pump messages during an operation.
            _draining = true;
            try
            {
                while (true)
                {
                    Command? command;
                    lock (_submissionGate) { if (!_queue.TryDequeue(out command)) break; }
                    try { command.Run(); }
                    catch (Exception error) { command.Reject(error); }
                }
            }
            finally { _draining = false; }
        }
        private void Run()
        {
            bool ole = false;
            nint className = 0;
            try
            {
                ThreadId = ThreadNative.GetCurrentThreadId();
                // Inherit the initiating thread's desktop, including isolated test desktops.
                if (_desktop != 0 && ThreadNative.GetThreadDesktop(ThreadId) != _desktop &&
                    ThreadNative.SetThreadDesktop(_desktop) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(),
                        $"Cannot attach clipboard worker to desktop {ThreadNative.DesktopName(_desktop)}; current={ThreadNative.DesktopName(ThreadNative.GetThreadDesktop(ThreadId))}.");
                int hr = ThreadNative.OleInitialize(0);
                Marshal.ThrowExceptionForHR(hr);
                ole = true;
                _active = this;
                className = Marshal.StringToHGlobalUni("SevenKeyboard.ClipRestorer.Worker");
                var wc = new ThreadNative.WindowClass
                {
                    Size = (uint)sizeof(ThreadNative.WindowClass),
                    Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProc,
                    Instance = ThreadNative.GetModuleHandleW(null), Name = className
                };
                ushort atom = ThreadNative.RegisterClassExW(in wc);
                if (atom == 0 && Marshal.GetLastPInvokeError() != 1410)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                Window = ThreadNative.CreateWindowExW(0, "SevenKeyboard.ClipRestorer.Worker", "ClipRestorer", 0,
                    0, 0, 0, 0, new nint(-3), 0, wc.Instance, 0);
                if (Window == 0 || ThreadNative.AddClipboardFormatListener(Window) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                _started.TrySetResult();
                int result;
                while ((result = ThreadNative.GetMessageW(out var message, 0, 0, 0)) > 0)
                {
                    ThreadNative.TranslateMessage(in message);
                    ThreadNative.DispatchMessageW(in message);
                }
                if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            catch (Exception error) { _started.TrySetException(error); }
            finally
            {
                lock (_submissionGate)
                {
                    _stopped = true;
                    while (_queue.TryDequeue(out Command? command))
                        command.Reject(new InvalidOperationException("The clipboard worker has stopped."));
                }
                if (Window != 0)
                {
                    ThreadNative.RemoveClipboardFormatListener(Window);
                    ThreadNative.DestroyWindow(Window);
                }
                if (className != 0) Marshal.FreeHGlobal(className);
                if (ole) ThreadNative.OleUninitialize();
                _active = null;
            }
        }
        private void OnProcessExit(object? sender, EventArgs args)
        {
            if (_stopped) return;
            try
            {
                // Process exit is best effort. Never wait indefinitely on another process's COM call.
                Task<int> ending = Enqueue(() =>
                {
                    Exiting?.Invoke();
                    SnapshotRuntime.Shutdown();
                    ThreadNative.PostQuitMessage(0);
                    return 0;
                });
                ending.Wait(TimeSpan.FromSeconds(1));
            }
            catch { /* Windows reclaims process resources; exit must not throw. */ }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            Worker? worker = _active;
            try
            {
                switch (message)
                {
                    case 0x8001: worker?.Drain(); return 0;
                    case 0x031D:
                        worker?.Post(() => ClipboardChanged?.Invoke()); return 0;
                    case 0x0113:
                        if (wParam != 0 && wParam == _timerId)
                            worker?.Post(() => { if (wParam == _timerId) TimerElapsed?.Invoke(); });
                        return 0;
                }
            }
            catch { /* Never unwind a managed exception through a native window procedure. */ }
            return ThreadNative.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }
}

internal static partial class ThreadNative
{
    internal static unsafe string DesktopName(nint desktop)
    {
        char* name = stackalloc char[256];
        return GetUserObjectInformationW(desktop, 2, name, 512, out _) != 0
            ? new string(name) : desktop.ToString();
    }
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetUserObjectInformationW(nint handle, int index, void* value, uint length, out uint needed);
    [StructLayout(LayoutKind.Sequential)] internal struct WindowClass
    {
        internal uint Size, Style; internal nint Procedure; internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background, Menu, Name, SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    {
        internal nint Hwnd; internal uint Id; internal nuint WParam; internal nint LParam;
        internal uint Time; internal int X, Y; internal uint Private;
    }
    [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint GetModuleHandleW(string? name);
    [LibraryImport("ole32.dll")] internal static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll")] internal static partial void OleUninitialize();
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint GetThreadDesktop(uint thread);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int SetThreadDesktop(nint desktop);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial ushort RegisterClassExW(in WindowClass cls);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowExW(uint extended, string cls, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] internal static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int AddClipboardFormatListener(nint hwnd);
    [LibraryImport("user32.dll")] internal static partial int RemoveClipboardFormatListener(nint hwnd);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int GetMessageW(out Message msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] internal static partial int TranslateMessage(in Message msg);
    [LibraryImport("user32.dll")] internal static partial nint DispatchMessageW(in Message msg);
    [LibraryImport("user32.dll")] internal static partial nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")] internal static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [LibraryImport("user32.dll")] internal static partial int KillTimer(nint hwnd, nuint id);
}
