using System.Reflection;
using EdiabasLib;

namespace Felkod.Daemon;

// EdiabasLib keeps the interpreter's registers in a static list that every new EdiabasNet rebinds to
// itself (EdiabasNet.cs RegisterList / constructor), so SGBD work from two threads, or a construction
// during a running job, corrupts results. Serializing is not enough either: after another instance has
// been constructed, an older instance's jobs run on the newer one's registers. So every construction
// holds Gate (a new instance binds the registers to itself), and every later use of an instance (jobs,
// dispose) goes through Use, which holds Gate and binds the registers back to it first. The binding reaches into
// EdiabasLib's private RegisterList: check it when updating the submodule.
// Lock order: the catalog's own lock first, then Gate.
static class Sgbd
{
    public static readonly object Gate = new();

    static readonly Action<EdiabasNet>[] bindRegisters = RegisterBindings();

    static Action<EdiabasNet>[] RegisterBindings()
    {
        var registers = typeof(EdiabasNet).GetField("RegisterList", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Array
            ?? throw new InvalidOperationException("EdiabasNet.RegisterList not found; the EdiabasLib version is not supported");
        return registers.Cast<object>()
            .Select(register => register.GetType().GetMethod("SetEdiabas")!.CreateDelegate<Action<EdiabasNet>>(register))
            .ToArray();
    }

    public static T Use<T>(EdiabasNet instance, Func<T> work)
    {
        lock (Gate)
        {
            foreach (var bind in bindRegisters)
            {
                bind(instance);
            }
            return work();
        }
    }

    public static void Use(EdiabasNet instance, Action work) => Use(instance, () =>
    {
        work();
        return true;
    });
}
