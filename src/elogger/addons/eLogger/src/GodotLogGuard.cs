using System;

namespace Enaweg.Logger;

/// <summary>
/// Tracks whether a log processor is currently writing to Godot's output on this thread.
/// <para>
/// <see cref="GodotOSLogger" /> is installed into the engine and turns every engine message back into a ZLogger
/// entry. Without this guard a processor's own <c>GD.Print</c> would be picked up again and routed back into the
/// same logger factory, recursing until the stack overflows. Every processor that writes to Godot must enter the
/// guard around those writes, and the OS logger must ignore messages while it is held.
/// </para>
/// </summary>
internal static class GodotLogGuard
{
    [ThreadStatic] static bool isWriting;

    internal static bool IsWriting => isWriting;

    internal static Scope Enter() => new(isWriting);

    internal readonly ref struct Scope
    {
        readonly bool previous;

        internal Scope(bool previous)
        {
            this.previous = previous;
            isWriting = true;
        }

        public void Dispose()
        {
            isWriting = previous;
        }
    }
}
