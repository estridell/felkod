using System.Reflection;
using EdiabasLib;

namespace Felkod.Daemon;

// EdiabasLib keeps the interpreter's registers in a static list that every new EdiabasNet rebinds to
// itself (EdiabasNet.cs RegisterList / constructor), so SGBD work from two threads, or a construction
// during a running job, corrupts results. Serializing alone is insufficient: before using an older
// instance its registers must also be rebound. Keep this compatibility shim tied to the pinned
// submodule; no upstream sources are modified. Lock order: object lock, then Gate.
static class Sgbd
{
    public static readonly object Gate = new();

    static readonly Action<EdiabasNet>[] bindRegisters = GetRegisterBindings();

    static Action<EdiabasNet>[] GetRegisterBindings()
    {
        var registers = typeof(EdiabasNet).GetField("RegisterList", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as Array ?? throw new InvalidOperationException("unsupported EdiabasLib register layout");
        return registers.Cast<object>().Select(register => register.GetType().GetMethod("SetEdiabas")!
            .CreateDelegate<Action<EdiabasNet>>(register)).ToArray();
    }

    public static void Activate(EdiabasNet instance)
    {
        if (!Monitor.IsEntered(Gate))
        {
            throw new InvalidOperationException("SGBD activation requires Gate");
        }
        foreach (var bind in bindRegisters)
        {
            bind(instance);
        }
    }
}
