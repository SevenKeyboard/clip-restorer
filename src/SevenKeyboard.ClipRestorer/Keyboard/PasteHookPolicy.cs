namespace SevenKeyboard;

internal enum PasteKey { ControlV, ControlShiftV, ShiftInsert, WindowsV, AppsKey, ShiftF10, RightButton }

[Flags]
internal enum PasteModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }

internal readonly record struct PasteHookDecision(bool Suppress, PasteKey? Paste = null);

// Pure gesture policy: only the first down can trigger a paste. A passed down
// must keep passing until its up, and a swallowed down must keep swallowing.
internal sealed class PasteHookPolicy
{
    private enum Gesture { Up, Passed, Swallowed }
    private Gesture _v, _apps, _insert, _f10, _right;
    internal bool Enabled { get; set; }
    internal bool HasSwallowedKeys => _v == Gesture.Swallowed || _insert == Gesture.Swallowed;

    internal static bool RequiresReplay(PasteKey key) =>
        key is PasteKey.ControlV or PasteKey.ControlShiftV or PasteKey.ShiftInsert or PasteKey.WindowsV;

    internal void Initialize(bool vDown, bool appsDown, bool insertDown = false, bool f10Down = false)
    {
        _v = vDown ? Gesture.Passed : Gesture.Up;
        _apps = appsDown ? Gesture.Passed : Gesture.Up;
        _insert = insertDown ? Gesture.Passed : Gesture.Up;
        _f10 = f10Down ? Gesture.Passed : Gesture.Up;
    }

    internal void InitializeMouse(bool rightDown) => _right = rightDown ? Gesture.Passed : Gesture.Up;

    internal PasteHookDecision Process(uint virtualKey, bool down, PasteModifiers modifiers, nuint extraInfo)
    {
        // Other programs' injected input is intentionally treated like any input.
        if (extraInfo == HookNative.InjectionMarker) return default;
        if (virtualKey == HookNative.VkV)
        {
            PasteKey? paste = modifiers switch
            {
                PasteModifiers.Control => PasteKey.ControlV,
                PasteModifiers.Control | PasteModifiers.Shift => PasteKey.ControlShiftV,
                PasteModifiers.Windows => PasteKey.WindowsV,
                _ => null
            };
            return ProcessGesture(ref _v, down, paste);
        }
        if (virtualKey == HookNative.VkInsert)
            return ProcessGesture(ref _insert, down, modifiers == PasteModifiers.Shift ? PasteKey.ShiftInsert : null);
        if (virtualKey == HookNative.VkApps)
            return ProcessGesture(ref _apps, down, PasteKey.AppsKey);
        if (virtualKey == HookNative.VkF10)
            return ProcessGesture(ref _f10, down, modifiers == PasteModifiers.Shift ? PasteKey.ShiftF10 : null);
        return default;
    }

    internal PasteHookDecision ProcessMouse(bool down, nuint extraInfo) =>
        extraInfo == HookNative.InjectionMarker ? default : ProcessGesture(ref _right, down, PasteKey.RightButton);

    private PasteHookDecision ProcessGesture(ref Gesture gesture, bool down, PasteKey? key)
    {
        if (!down)
        {
            bool swallowed = gesture == Gesture.Swallowed;
            gesture = Gesture.Up;
            return new(swallowed);
        }
        if (gesture != Gesture.Up) return new(gesture == Gesture.Swallowed);
        if (Enabled && key is PasteKey paste)
        {
            bool swallow = RequiresReplay(paste);
            gesture = swallow ? Gesture.Swallowed : Gesture.Passed;
            return new(swallow, paste);
        }
        gesture = Gesture.Passed;
        return default;
    }
}

// Pure SendInput construction, also usable by the smoke harness without sending input.
internal static class PasteReplayPolicy
{
    internal static int Build(PasteKey key, PasteModifiers modifiers, Span<HookNative.Input> inputs)
    {
        (ushort vk, PasteModifiers requiredModifiers) = key switch
        {
            PasteKey.ControlV => (HookNative.VkV, PasteModifiers.Control),
            PasteKey.ControlShiftV => (HookNative.VkV, PasteModifiers.Control | PasteModifiers.Shift),
            PasteKey.ShiftInsert => (HookNative.VkInsert, PasteModifiers.Shift),
            PasteKey.WindowsV => (HookNative.VkV, PasteModifiers.Windows),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        if ((modifiers & ~requiredModifiers) != 0)
            throw new InvalidOperationException("Cannot replay paste while unrelated modifiers are held.");
        PasteModifiers missing = requiredModifiers & ~modifiers;
        bool addControl = (missing & PasteModifiers.Control) != 0;
        bool addShift = (missing & PasteModifiers.Shift) != 0;
        bool addWindows = (missing & PasteModifiers.Windows) != 0;
        int required = 2 + (addControl ? 2 : 0) + (addShift ? 2 : 0) + (addWindows ? 2 : 0);
        if (inputs.Length < required) throw new ArgumentException("Input buffer is too small.", nameof(inputs));

        // Fixed modifier order; release only the modifiers introduced by this batch.
        int count = 0;
        if (addControl) inputs[count++] = Key(HookNative.VkLControl, false);
        if (addShift) inputs[count++] = Key(HookNative.VkLShift, false);
        if (addWindows) inputs[count++] = Key(HookNative.VkLWin, false);
        inputs[count++] = Key(vk, false);
        inputs[count++] = Key(vk, true);
        if (addWindows) inputs[count++] = Key(HookNative.VkLWin, true);
        if (addShift) inputs[count++] = Key(HookNative.VkLShift, true);
        if (addControl) inputs[count++] = Key(HookNative.VkLControl, true);
        return count;
    }

    internal static HookNative.Input Key(ushort vk, bool up) => new()
    {
        Type = 1,
        Data = new HookNative.InputUnion
        {
            Keyboard = new HookNative.KeyboardInput
            {
                VirtualKey = vk,
                Flags = (up ? HookNative.KeyUp : 0) |
                    (vk is HookNative.VkInsert or HookNative.VkLWin or HookNative.VkRWin or HookNative.VkApps ? HookNative.ExtendedKey : 0),
                ExtraInfo = HookNative.InjectionMarker
            }
        }
    };
}
