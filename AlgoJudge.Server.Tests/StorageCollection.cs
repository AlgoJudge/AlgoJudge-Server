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
/// </summary>
[CollectionDefinition("storage", DisableParallelization = true)]
public class StorageCollection;
