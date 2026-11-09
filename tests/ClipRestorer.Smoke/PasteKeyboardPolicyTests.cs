namespace SevenKeyboard;

// Intentionally pure: this entry point installs no hook and calls no Windows API.
// Accesses internal gesture logic through InternalsVisibleTo. Mouse events here
// are right-button transitions; native filtering and live Win+V behavior are not tested.
internal static class PasteKeyboardPolicyTests
{
    internal static unsafe int Run()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Paste policy: " + message);
        }
        void Decision(PasteHookDecision actual, bool suppress, PasteKey? paste, string message) =>
            Check(actual == new PasteHookDecision(suppress, paste), message);
        static PasteHookPolicy Enabled() => new() { Enabled = true };
        static PasteHookDecision Down(PasteHookPolicy p, uint key, PasteModifiers mods = PasteModifiers.Control, nuint marker = 0) => p.Process(key, true, mods, marker);
        static PasteHookDecision Up(PasteHookPolicy p, uint key, nuint marker = 0) => p.Process(key, false, 0, marker);

        const PasteModifiers controlShift = PasteModifiers.Control | PasteModifiers.Shift;
        (ushort Vk, PasteModifiers Mods, PasteKey Paste, bool Suppress)[] gestures =
        [
            (HookNative.VkV, PasteModifiers.Control, PasteKey.ControlV, true),
            (HookNative.VkV, controlShift, PasteKey.ControlShiftV, true),
            (HookNative.VkInsert, PasteModifiers.Shift, PasteKey.ShiftInsert, true),
            (HookNative.VkV, PasteModifiers.Windows, PasteKey.WindowsV, true),
            (HookNative.VkApps, PasteModifiers.None, PasteKey.AppsKey, false),
            (HookNative.VkF10, PasteModifiers.Shift, PasteKey.ShiftF10, false)
        ];
        ushort[] keys = [HookNative.VkV, HookNative.VkApps, HookNative.VkInsert, HookNative.VkF10];
        foreach (var gesture in gestures)
            Check(PasteHookPolicy.RequiresReplay(gesture.Paste) == gesture.Suppress, $"replay classification: {gesture.Paste}");
        Check(!PasteHookPolicy.RequiresReplay(PasteKey.RightButton), "right button is notification only");
        Check(!PasteHookPolicy.RequiresReplay((PasteKey)99), "unknown keys do not request replay");

        // All modifier combinations, including AltGr, and every modifier change
        // while held. The first down decides the entire physical key pair.
        foreach (bool enabled in new[] { false, true })
        foreach (ushort vk in keys)
        for (int bits = 0; bits < 16; bits++)
        {
            var policy = new PasteHookPolicy { Enabled = enabled };
            PasteKey? expected = null;
            bool suppress = false;
            foreach (var gesture in gestures)
                if (enabled && gesture.Vk == vk && (gesture.Paste == PasteKey.AppsKey || gesture.Mods == (PasteModifiers)bits))
                {
                    expected = gesture.Paste;
                    suppress = gesture.Suppress;
                }
            string context = $"enabled={enabled}, key={vk}, modifiers={bits}";
            Decision(Down(policy, vk, (PasteModifiers)bits), suppress, expected, "first down: " + context);
            Check(policy.HasSwallowedKeys == suppress, "only immediate keyboard inputs need draining: " + context);
            for (int held = 0; held < 16; held++)
                Decision(Down(policy, vk, (PasteModifiers)held), suppress, null, "held modifier change: " + context);
            Decision(policy.Process(vk, false, (PasteModifiers)(15 - bits), 0), suppress, null, "paired up: " + context);
            Check(!policy.HasSwallowedKeys, "paired up clears drain: " + context);
            Decision(Up(policy, vk), false, null, "orphan up: " + context);
            Decision(Down(policy, vk, (PasteModifiers)bits), suppress, expected, "next press can trigger: " + context);
            Decision(Up(policy, vk), suppress, null, "next press up: " + context);
        }

        // Unrelated keyboard events must neither trigger nor disturb another pair.
        var unrelated = Enabled();
        Decision(Down(unrelated, HookNative.VkV), true, PasteKey.ControlV, "start unrelated-key isolation");
        foreach (uint other in new uint[] { 0x43, 0x58, 0x41, HookNative.VkControl, HookNative.VkLControl,
            HookNative.VkRControl, HookNative.VkShift, HookNative.VkLShift, HookNative.VkAlt,
            HookNative.VkLWin, HookNative.VkRWin, HookNative.VkRButton })
        for (int bits = 0; bits < 16; bits++)
        {
            Decision(Down(unrelated, other, (PasteModifiers)bits), false, null, "unrelated keyboard down");
            Decision(unrelated.Process(other, false, (PasteModifiers)bits, 0), false, null, "unrelated keyboard up");
            Check(unrelated.HasSwallowedKeys, "unrelated key cannot release V");
        }
        Decision(Up(unrelated, HookNative.VkV), true, null, "V pair survives unrelated keys");

        foreach (var gesture in gestures)
        {
            var policy = Enabled();
            string context = gesture.Paste.ToString();
            Decision(Down(policy, gesture.Vk, gesture.Mods, HookNative.InjectionMarker), false, null, "own idle down: " + context);
            Decision(Up(policy, gesture.Vk, HookNative.InjectionMarker), false, null, "own idle up: " + context);
            Decision(Down(policy, gesture.Vk, gesture.Mods, 0x12345678), gesture.Suppress, gesture.Paste, "foreign down: " + context);
            Decision(Down(policy, gesture.Vk, gesture.Mods, HookNative.InjectionMarker), false, null, "own held down: " + context);
            Decision(Up(policy, gesture.Vk, HookNative.InjectionMarker), false, null, "own held up: " + context);
            Check(policy.HasSwallowedKeys == gesture.Suppress, "own input cannot finish physical pair: " + context);
            Decision(Down(policy, gesture.Vk, gesture.Mods), gesture.Suppress, null, "own up does not reset repeat: " + context);

            policy.Enabled = false;
            Check(policy.HasSwallowedKeys == gesture.Suppress, "disable preserves pair: " + context);
            for (int bits = 0; bits < 16; bits++)
                Decision(Down(policy, gesture.Vk, (PasteModifiers)bits), gesture.Suppress, null, "disabled held repeat: " + context);
            Decision(Up(policy, gesture.Vk, 0x12345678), gesture.Suppress, null, "disabled foreign release: " + context);
            Check(!policy.HasSwallowedKeys, "disabled release clears drain: " + context);
            Decision(Down(policy, gesture.Vk, gesture.Mods), false, null, "disabled fresh down: " + context);
            policy.Enabled = true;
            Decision(Down(policy, gesture.Vk, gesture.Mods), false, null, "enable cannot capture passed repeat: " + context);
            Decision(Up(policy, gesture.Vk), false, null, "enable cannot capture passed up: " + context);
            Decision(Down(policy, gesture.Vk, gesture.Mods), gesture.Suppress, gesture.Paste, "fresh enabled down: " + context);
            policy.Enabled = false;
            policy.Enabled = true;
            Decision(Down(policy, gesture.Vk, gesture.Mods), gesture.Suppress, null, "toggle cannot duplicate held gesture: " + context);
            Decision(Up(policy, gesture.Vk), gesture.Suppress, null, "toggle preserves up: " + context);
        }

        // All sixteen held-at-install combinations seed four independent keys.
        for (int held = 0; held < 16; held++)
        foreach (var gesture in gestures)
        {
            var policy = Enabled();
            policy.Initialize((held & 1) != 0, (held & 2) != 0, (held & 4) != 0, (held & 8) != 0);
            Check(!policy.HasSwallowedKeys, "seeded keyboard state is never swallowed");
            int bit = gesture.Vk == HookNative.VkV ? 1 : gesture.Vk == HookNative.VkApps ? 2 : gesture.Vk == HookNative.VkInsert ? 4 : 8;
            bool seeded = (held & bit) != 0;
            bool suppress = !seeded && gesture.Suppress;
            Decision(Down(policy, gesture.Vk, gesture.Mods), suppress, seeded ? null : gesture.Paste, "seeded keyboard first observed down");
            Decision(Down(policy, gesture.Vk, gesture.Mods), suppress, null, "seeded keyboard repeat");
            Decision(Up(policy, gesture.Vk), suppress, null, "seeded keyboard up");
            Decision(Down(policy, gesture.Vk, gesture.Mods), gesture.Suppress, gesture.Paste, "seeded keyboard fresh press");
            Decision(Up(policy, gesture.Vk), gesture.Suppress, null, "seeded keyboard fresh release");
        }
        var defaults = Enabled();
        defaults.Initialize(vDown: true, appsDown: true);
        Decision(Down(defaults, HookNative.VkV), false, null, "two-argument initializer seeds V");
        Decision(Down(defaults, HookNative.VkApps), false, null, "two-argument initializer seeds Apps");
        Decision(Down(defaults, HookNative.VkInsert, PasteModifiers.Shift), true, PasteKey.ShiftInsert, "default Insert state is up");
        Decision(Down(defaults, HookNative.VkF10, PasteModifiers.Shift), false, PasteKey.ShiftF10, "default F10 state is up");
        defaults.Initialize(false, false);
        Check(!defaults.HasSwallowedKeys, "keyboard installation resets prior keyboard state");
        Decision(Down(defaults, HookNative.VkInsert, PasteModifiers.Shift), true, PasteKey.ShiftInsert, "keyboard reset clears Insert");
        Decision(Down(defaults, HookNative.VkF10, PasteModifiers.Shift), false, PasteKey.ShiftF10, "keyboard reset clears F10");

        // Both swallowed keys may coexist; menu and mouse releases cannot drain them.
        foreach (bool insertFirst in new[] { false, true })
        {
            var policy = Enabled();
            Decision(Down(policy, HookNative.VkV, controlShift), true, PasteKey.ControlShiftV, "independent V");
            Decision(Down(policy, HookNative.VkInsert, PasteModifiers.Shift), true, PasteKey.ShiftInsert, "independent Insert");
            Decision(Down(policy, HookNative.VkApps), false, PasteKey.AppsKey, "independent Apps");
            Decision(Down(policy, HookNative.VkF10, PasteModifiers.Shift), false, PasteKey.ShiftF10, "independent F10");
            Decision(policy.ProcessMouse(true, 0), false, PasteKey.RightButton, "independent right button");
            policy.Enabled = false;
            Decision(Up(policy, HookNative.VkApps), false, null, "menu release passes");
            Decision(Up(policy, HookNative.VkF10), false, null, "F10 release passes");
            Decision(policy.ProcessMouse(false, 0), false, null, "mouse release passes");
            Check(policy.HasSwallowedKeys, "menu and mouse releases cannot finish keyboard drain");
            ushort first = insertFirst ? HookNative.VkInsert : HookNative.VkV;
            ushort last = insertFirst ? HookNative.VkV : HookNative.VkInsert;
            Decision(Up(policy, first), true, null, "first swallowed release");
            Check(policy.HasSwallowedKeys, "other swallowed key still needs draining");
            Decision(Down(policy, last, PasteModifiers.None), true, null, "remaining swallowed repeat");
            Decision(Up(policy, last), true, null, "last swallowed release");
            Check(!policy.HasSwallowedKeys, "last swallowed release finishes drain");
        }
        var firstPolicy = Enabled();
        var secondPolicy = Enabled();
        Decision(Down(firstPolicy, HookNative.VkV), true, PasteKey.ControlV, "first policy gesture");
        Decision(Down(secondPolicy, HookNative.VkV), true, PasteKey.ControlV, "second policy gesture");
        Decision(Up(firstPolicy, HookNative.VkV), true, null, "first policy release");
        Check(secondPolicy.HasSwallowedKeys, "policy instances have independent keyboard state");
        Decision(Up(secondPolicy, HookNative.VkV), true, null, "second policy release");

        // Right-button holds (including drags) always pass and trigger only once.
        foreach (bool enabled in new[] { false, true })
        foreach (bool held in new[] { false, true })
        {
            var policy = new PasteHookPolicy { Enabled = enabled };
            policy.InitializeMouse(held);
            Decision(policy.ProcessMouse(true, HookNative.InjectionMarker), false, null, "own mouse down ignored");
            Decision(policy.ProcessMouse(false, HookNative.InjectionMarker), false, null, "own mouse up ignored");
            Decision(policy.ProcessMouse(true, 0x12345678), false, enabled && !held ? PasteKey.RightButton : null, "mouse first down or seeded hold");
            Check(!policy.HasSwallowedKeys, "right-button hold never requires keyboard drain");
            Decision(policy.ProcessMouse(false, HookNative.InjectionMarker), false, null, "own mouse up cannot end physical hold");
            policy.Enabled = !enabled;
            Decision(policy.ProcessMouse(true, 0), false, null, "toggle during mouse hold cannot trigger");
            policy.Enabled = true;
            // Movement has no policy callback; interleaved modifier/key events do
            // not end the drag. Repeated down notifications must also pass.
            for (int bits = 0; bits < 16; bits++)
            {
                Decision(Down(policy, HookNative.VkLShift, (PasteModifiers)bits), false, null, "keyboard event during right drag");
                Decision(Up(policy, HookNative.VkLShift), false, null, "keyboard release during right drag");
                Decision(policy.ProcessMouse(true, 0), false, null, "right drag repeat");
            }
            Decision(policy.ProcessMouse(false, 0x12345678), false, null, "right drag release");
            Decision(policy.ProcessMouse(false, 0), false, null, "orphan right release");
            Decision(policy.ProcessMouse(true, 0), false, PasteKey.RightButton, "new right press after release");
            policy.Enabled = false;
            Decision(policy.ProcessMouse(false, 0), false, null, "disabled right release passes");
            Check(!policy.HasSwallowedKeys, "mouse never creates swallowed state");
        }
        var independent = Enabled();
        Decision(Down(independent, HookNative.VkV), true, PasteKey.ControlV, "keyboard before mouse installation");
        independent.InitializeMouse(true);
        Check(independent.HasSwallowedKeys, "mouse installation preserves keyboard hold");
        Decision(Down(independent, HookNative.VkV), true, null, "mouse installation preserves keyboard repeat");
        Decision(independent.ProcessMouse(true, 0), false, null, "seeded mouse hold passes");
        independent.Initialize(false, false, false, false);
        Decision(independent.ProcessMouse(true, 0), false, null, "keyboard installation preserves mouse hold");
        Decision(independent.ProcessMouse(false, 0), false, null, "seeded mouse release");
        Decision(independent.ProcessMouse(true, 0), false, PasteKey.RightButton, "fresh mouse press");
        independent.Initialize(false, false);
        Decision(independent.ProcessMouse(true, 0), false, null, "keyboard installation cannot duplicate mouse notification");
        var otherMouse = Enabled();
        Decision(otherMouse.ProcessMouse(true, 0), false, PasteKey.RightButton, "second policy has independent mouse state");
        Decision(otherMouse.ProcessMouse(false, 0), false, null, "second policy mouse release");
        Decision(independent.ProcessMouse(true, 0), false, null, "other policy release cannot end mouse hold");
        independent.InitializeMouse(false);
        Decision(independent.ProcessMouse(true, 0), false, PasteKey.RightButton, "mouse installation resets prior mouse state");

        // Literal sequences cover every permitted held-modifier subset. Grouped
        // modifiers mean either physical side is reused; no held group gets an up.
        const uint keyUp = HookNative.KeyUp, extended = HookNative.ExtendedKey;
        (PasteKey Paste, PasteModifiers Mods, (ushort Vk, uint Flags)[] Expected)[] replays =
        [
            (PasteKey.ControlV, PasteModifiers.None,
                [(HookNative.VkLControl, 0), (HookNative.VkV, 0), (HookNative.VkV, keyUp), (HookNative.VkLControl, keyUp)]),
            (PasteKey.ControlV, PasteModifiers.Control,
                [(HookNative.VkV, 0), (HookNative.VkV, keyUp)]),
            (PasteKey.ControlShiftV, PasteModifiers.None,
                [(HookNative.VkLControl, 0), (HookNative.VkLShift, 0), (HookNative.VkV, 0),
                 (HookNative.VkV, keyUp), (HookNative.VkLShift, keyUp), (HookNative.VkLControl, keyUp)]),
            (PasteKey.ControlShiftV, PasteModifiers.Control,
                [(HookNative.VkLShift, 0), (HookNative.VkV, 0), (HookNative.VkV, keyUp), (HookNative.VkLShift, keyUp)]),
            (PasteKey.ControlShiftV, PasteModifiers.Shift,
                [(HookNative.VkLControl, 0), (HookNative.VkV, 0), (HookNative.VkV, keyUp), (HookNative.VkLControl, keyUp)]),
            (PasteKey.ControlShiftV, controlShift,
                [(HookNative.VkV, 0), (HookNative.VkV, keyUp)]),
            (PasteKey.ShiftInsert, PasteModifiers.None,
                [(HookNative.VkLShift, 0), (HookNative.VkInsert, extended),
                 (HookNative.VkInsert, extended | keyUp), (HookNative.VkLShift, keyUp)]),
            (PasteKey.ShiftInsert, PasteModifiers.Shift,
                [(HookNative.VkInsert, extended), (HookNative.VkInsert, extended | keyUp)]),
            (PasteKey.WindowsV, PasteModifiers.None,
                [(HookNative.VkLWin, extended), (HookNative.VkV, 0), (HookNative.VkV, keyUp), (HookNative.VkLWin, extended | keyUp)]),
            (PasteKey.WindowsV, PasteModifiers.Windows,
                [(HookNative.VkV, 0), (HookNative.VkV, keyUp)])
        ];
        var sentinel = new HookNative.Input
        {
            Type = 0xABCD,
            Data = new HookNative.InputUnion
            {
                Keyboard = new HookNative.KeyboardInput
                {
                    VirtualKey = 0xCAFE, ScanCode = 0xBEEF, Flags = 0x1234,
                    Time = 0x5678, ExtraInfo = 0x2468
                }
            }
        };
        var inputs = new HookNative.Input[7];
        void Untouched(ReadOnlySpan<HookNative.Input> buffer, string message)
        {
            foreach (var input in buffer)
                Check(input.Type == sentinel.Type &&
                    input.Data.Keyboard.VirtualKey == sentinel.Data.Keyboard.VirtualKey &&
                    input.Data.Keyboard.ScanCode == sentinel.Data.Keyboard.ScanCode &&
                    input.Data.Keyboard.Flags == sentinel.Data.Keyboard.Flags &&
                    input.Data.Keyboard.Time == sentinel.Data.Keyboard.Time &&
                    input.Data.Keyboard.ExtraInfo == sentinel.Data.Keyboard.ExtraInfo, message);
        }
        void Input(HookNative.Input input, ushort vk, uint flags, string message) =>
            Check(input.Type == 1 && input.Data.Keyboard.VirtualKey == vk &&
                input.Data.Keyboard.Flags == flags && input.Data.Keyboard.ScanCode == 0 &&
                input.Data.Keyboard.Time == 0 && input.Data.Keyboard.ExtraInfo == HookNative.InjectionMarker, message);

        int maximumInputs = 0;
        foreach (var replay in replays)
        {
            string context = $"{replay.Paste}, held={replay.Mods}";
            Array.Fill(inputs, sentinel);
            int count = PasteReplayPolicy.Build(replay.Paste, replay.Mods, inputs);
            Check(count == replay.Expected.Length, "replay input count: " + context);
            maximumInputs = Math.Max(maximumInputs, count);
            for (int i = 0; i < count; i++)
                Input(inputs[i], replay.Expected[i].Vk, replay.Expected[i].Flags, $"replay event {i}: " + context);
            Untouched(inputs.AsSpan(count), "replay does not touch trailing capacity: " + context);
            Check(PasteReplayPolicy.Build(replay.Paste, replay.Mods, inputs.AsSpan(0, count)) == count, "exact-sized replay buffer: " + context);

            // Validate capacity before writing any part of a replay.
            for (int length = 0; length < count; length++)
            {
                Array.Fill(inputs, sentinel);
                bool rejected = false;
                try { PasteReplayPolicy.Build(replay.Paste, replay.Mods, inputs.AsSpan(0, length)); }
                catch (ArgumentException error) { rejected = error.ParamName == "inputs"; }
                Check(rejected, "short replay buffer rejected: " + context);
                Untouched(inputs, "short buffer rejection leaves all inputs intact: " + context);
            }
        }
        Check(maximumInputs == 6, "Ctrl+Shift+V with both modifiers missing sets the six-input maximum");

        // All known modifier combinations plus an unknown modifier bit. Reject
        // supersets such as Ctrl+Shift+V when plain Ctrl+V was requested.
        foreach (var gesture in gestures)
        {
            if (!gesture.Suppress) continue;
            for (int bits = 0; bits < 32; bits++)
            {
                if (((PasteModifiers)bits & ~gesture.Mods) == 0) continue;
                Array.Fill(inputs, sentinel);
                bool rejected = false;
                try { PasteReplayPolicy.Build(gesture.Paste, (PasteModifiers)bits, inputs); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, $"unrelated replay modifiers rejected: {gesture.Paste}, {bits}");
                Untouched(inputs, "wrong modifiers cannot partially construct a shortcut");
            }
        }
        foreach (PasteKey key in new[] { PasteKey.AppsKey, PasteKey.ShiftF10, PasteKey.RightButton, (PasteKey)99 })
        for (int bits = 0; bits < 16; bits++)
        {
            Array.Fill(inputs, sentinel);
            bool rejected = false;
            try { PasteReplayPolicy.Build(key, (PasteModifiers)bits, inputs); }
            catch (ArgumentOutOfRangeException error) { rejected = error.ParamName == "key"; }
            Check(rejected, "menu and unknown keys cannot be replayed");
            Untouched(inputs, "unsupported replay leaves input buffer intact");
        }
        foreach (ushort vk in new ushort[] { HookNative.VkInsert, HookNative.VkLWin, HookNative.VkRWin, HookNative.VkApps })
        {
            Input(PasteReplayPolicy.Key(vk, false), vk, extended, "helper marks extended down");
            Input(PasteReplayPolicy.Key(vk, true), vk, extended | keyUp, "helper marks extended up");
        }
        foreach (ushort vk in new ushort[] { HookNative.VkV, HookNative.VkLControl, HookNative.VkLShift, HookNative.VkF10 })
        {
            Input(PasteReplayPolicy.Key(vk, false), vk, 0, "helper marks ordinary down");
            Input(PasteReplayPolicy.Key(vk, true), vk, keyUp, "helper marks ordinary up");
        }

        Check(sizeof(HookNative.Input) == (IntPtr.Size == 8 ? 40 : 28), "native INPUT size");
        Check(sizeof(HookNative.KeyboardInput) == (IntPtr.Size == 8 ? 24 : 16), "native KEYBDINPUT size");
        Check(sizeof(HookNative.KeyboardEvent) == (IntPtr.Size == 8 ? 24 : 20), "native KBDLLHOOKSTRUCT size");
        Check(sizeof(HookNative.Message) == (IntPtr.Size == 8 ? 48 : 32), "native MSG size");
        HookNative.Input layout = default;
        Check((byte*)&layout.Data - (byte*)&layout == (IntPtr.Size == 8 ? 8 : 4), "native INPUT union alignment");
        return assertions;
    }
}
