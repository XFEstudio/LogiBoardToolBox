using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LogiBoardToolBox.Core.Macros;

namespace LogiBoardToolBox.Core.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsInputSink : IMacroInputSink
{
    public static void Validate(MacroProgram program)
    {
        foreach (var b in program.Press.Concat(program.Hold).Concat(program.Release))
        {
            if (b[0] is 0x43 or 0x44 && b[2] != 0) _ = VirtualKey(b[2]);
            if (b[0] is 0x41 or 0x42 && (b[1] != 0 || b[2] > 31)) throw new NotSupportedException("Windows 软件宏支持鼠标按钮 1–5；更多按钮仅可交由支持的固件执行。");
            if (b[0] is 0x45 or 0x46) _ = ConsumerKey((b[1] << 8) | b[2]);
        }
    }
    public void Send(MacroInput input)
    {
        var native = new Input();
        if (input.Kind is MacroInputKind.Key or MacroInputKind.Consumer)
        {
            var key = input.Kind == MacroInputKind.Key ? VirtualKey(input.Code) : ConsumerKey(input.Code);
            native.Type = 1;
            native.Data.Keyboard = new() { VirtualKey = (ushort)key, Flags = input.Down ? 0u : 2u, ExtraInfo = (nuint)0x4C425442 };
        }
        else
        {
            var mouse = new MouseInput { ExtraInfo = (nuint)0x4C425442 };
            switch (input.Kind)
            {
                case MacroInputKind.MouseButton:
                    mouse.Flags = input.Code switch { 1 => input.Down ? 2u : 4u, 2 => input.Down ? 8u : 16u, 3 => input.Down ? 32u : 64u, 4 or 5 => input.Down ? 0x80u : 0x100u, _ => throw new NotSupportedException("Windows 支持 1–5 号鼠标按键。") };
                    if (input.Code >= 4) mouse.Data = (uint)(input.Code - 3);
                    break;
                case MacroInputKind.Wheel or MacroInputKind.Pan:
                    mouse.Flags = input.Kind == MacroInputKind.Wheel ? 0x800u : 0x1000u; mouse.Data = unchecked((uint)(input.Code * 120)); break;
                case MacroInputKind.Move: mouse.Flags = 1; mouse.X = input.Code; mouse.Y = input.Y; break;
                default: throw new ArgumentOutOfRangeException(nameof(input));
            }
            native.Data.Mouse = mouse;
        }
        if (SendInput(1, [native], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法向前台应用发送输入。请确保目标应用与本程序权限相同。");
    }
    private static int ConsumerKey(int usage) => usage switch
    { 0xE2 => 0xAD, 0xE9 => 0xAF, 0xEA => 0xAE, 0xCD => 0xB3, 0xB5 => 0xB0, 0xB6 => 0xB1, 0xB7 => 0xB2, 0x183 => 0xB5, 0x18A => 0xB4, 0x192 => 0xB6, 0x221 => 0xAA, 0x223 => 0xAC, 0x224 => 0xA6, 0x225 => 0xA7, _ => throw new NotSupportedException($"Windows 软件宏尚不支持媒体键 0x{usage:X}。")} ;
    private static int VirtualKey(int usage) => usage switch
    {
        >= 4 and <= 29 => 'A' + usage - 4,
        >= 30 and <= 38 => '1' + usage - 30,
        39 => '0', >= 58 and <= 69 => 0x70 + usage - 58,
        >= 104 and <= 115 => 0x7C + usage - 104,
        >= 89 and <= 97 => 0x61 + usage - 89,
        40 => 13, 41 => 27, 42 => 8, 43 => 9, 44 => 32,
        45 => 0xBD, 46 => 0xBB, 47 => 0xDB, 48 => 0xDD, 49 or 50 => 0xDC, 51 => 0xBA, 52 => 0xDE, 53 => 0xC0, 54 => 0xBC, 55 => 0xBE, 56 => 0xBF,
        57 => 0x14, 70 => 0x2C, 71 => 0x91, 72 => 0x13, 73 => 0x2D, 74 => 0x24, 75 => 0x21, 76 => 0x2E, 77 => 0x23, 78 => 0x22, 79 => 0x27, 80 => 0x25, 81 => 0x28, 82 => 0x26,
        83 => 0x90, 84 => 0x6F, 85 => 0x6A, 86 => 0x6D, 87 => 0x6B, 88 => 13, 98 => 0x60, 99 => 0x6E, 101 => 0x5D,
        0xE0 => 0xA2, 0xE1 => 0xA0, 0xE2 => 0xA4, 0xE3 => 0x5B, 0xE4 => 0xA3, 0xE5 => 0xA1, 0xE6 => 0xA5, 0xE7 => 0x5C,
        _ => throw new NotSupportedException($"Windows 软件宏尚不支持 HID 按键 0x{usage:X2}。")
    };
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
