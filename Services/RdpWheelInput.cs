namespace CxShell.Services;

/// <summary>
/// Builds the pointer flags for RDP wheel input.
/// <para>
/// [MS-RDPBCGR] 2.2.8.1.1.3.1.1.3: a wheel event carries exactly one notch of
/// rotation in the low nine bits of the pointer flags. 0x78 (120) is one notch
/// away from the user, and <c>PTRFLAGS_WHEEL_NEGATIVE</c> (0x0100) inverts the
/// direction. Sending <c>PTRFLAGS_WHEEL</c> with a zero rotation field asks the
/// remote for zero notches, which is why one direction appeared to do nothing.
/// </para>
/// </summary>
public static class RdpWheelInput
{
    public const ushort Wheel = 0x0200;
    public const ushort WheelNegative = 0x0100;
    public const ushort HorizontalWheel = 0x0400;
    public const ushort NotchRotation = 0x0078;

    /// <summary>
    /// Turns an Avalonia wheel delta (measured in notches, positive scrolls up
    /// or left) into the number of whole notches to send, keeping the
    /// fractional part in <paramref name="remainder"/>. Precision touchpads
    /// deliver fractions of a notch, so they have to add up across events.
    /// </summary>
    public static int TakeNotches(ref double remainder, double delta)
    {
        remainder += delta;

        var notches = (int)remainder;
        remainder -= notches;
        return notches;
    }

    /// <summary>
    /// Pointer flags for one notch of rotation. Only the sign of
    /// <paramref name="notches"/> matters, because the protocol encodes a single
    /// notch per event.
    /// </summary>
    public static ushort GetNotchFlags(int notches, bool horizontal = false)
    {
        var flags = horizontal ? HorizontalWheel : Wheel;

        if (notches < 0)
            flags |= WheelNegative;

        return (ushort)(flags | NotchRotation);
    }
}
