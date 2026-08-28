namespace AlgoJudge.Server.Tests;

/// <summary>
/// The container-backed storage suites, run one at a time.
/// <para>
/// Each of them starts a database or an object store of its own, and xUnit runs
/// collections in parallel — so without this the machine is asked for five
/// containers at once beside the shared Server. That load made a pre-existing
/// timing-sensitive scheduler test fail once on 2026-08-12 and pass on the two
/// runs after it, which is the worst kind of failure to leave in a suite.
/// </para>
/// <para>
/// They share nothing but the name: none of them has a collection fixture, and
/// each still owns its own container. The only thing being shared is a turn.
/// </para>
/// <para>
/// <b><c>DisableParallelization</c> was removed on 2026-08-29, and being one
/// collection is what actually fixed the flake.</b> That flag means something
/// stronger than it reads: the collection takes the whole runner to itself, so
/// xUnit finished everything else first and then ran these alone. Measured on a
/// timeline, the storage suites did not begin until **113 s** into a 199 s run
/// and added **86 s to the end of it** — a third of the suite, spent waiting.
/// Being a single collection already serialises them, so the peak is now one
/// storage container beside three Server ones: **four, where six is what broke
/// in the first place**.
/// </para>
/// </summary>
[CollectionDefinition("storage")]
public class StorageCollection;

/// <summary>
/// <see cref="MemoryTests"/> alone, because it measures the whole process.
/// <para>
/// <b>The one suite that genuinely cannot share a runner.</b>
/// <c>GC.GetTotalAllocatedBytes</c> is process-wide, not per-test and not
/// per-thread, so every byte three other Server hosts allocate lands inside the
/// measurement. Freeing the storage collection on 2026-08-29 failed exactly one
/// test — "parsing allocated 45 MiB for a 128 MiB upload", against a ceiling of
/// 32 — and it failed for that reason rather than a real regression.
/// </para>
/// <para>
/// So the exclusivity that used to cost the whole storage collection **86 s**
/// now costs this class's **6 s**. It is bought where it is needed and nowhere
/// else.
/// </para>
/// </summary>
[CollectionDefinition("memory", DisableParallelization = true)]
public class MemoryCollection;
