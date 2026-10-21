namespace Felkod.Daemon;

// EdiabasLib keeps the interpreter's registers in a static list that every new EdiabasNet rebinds to
// itself (EdiabasNet.cs RegisterList / constructor), so SGBD work from two threads, or a construction
// during a running job, corrupts results. Every construction, SGBD resolution, job and dispose in the
// process holds this lock. Lock order: an object's own lock (decoder, catalog) first, then this one.
static class Sgbd
{
    public static readonly object Gate = new();
}
