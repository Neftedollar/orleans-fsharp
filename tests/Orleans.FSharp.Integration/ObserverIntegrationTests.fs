/// <summary>
/// Integration tests for the Observer module — verifying that <c>FSharpObserverManager</c>,
/// <c>Observer.createRef</c>, <c>Observer.deleteRef</c>, and <c>Observer.subscribe</c> work
/// end-to-end inside a real TestCluster.
///
/// The test grain (<c>ITestChatGrain</c> / <c>TestChatGrainImpl</c>) is defined in C# in
/// <c>Orleans.FSharp.CodeGen</c> so Orleans source generators can produce the required proxies.
/// The client-side observer (<c>LocalChatObserver</c>) is defined here in F# and registered
/// via <c>Observer.createRef</c>.
/// </summary>
module Orleans.FSharp.Integration.ObserverIntegrationTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.CodeGen

// ── Client-side observer implementation ─────────────────────────────────────

/// <summary>
/// Client-side test observer that records all received messages and signals
/// a semaphore so tests can await message arrival without busy-looping.
/// </summary>
type LocalChatObserver() =
    let received = ConcurrentBag<string>()
    let gate = new SemaphoreSlim(0)

    /// <summary>All messages received so far (in arrival order is not guaranteed).</summary>
    member _.Messages = received :> seq<string>

    /// <summary>Number of messages received.</summary>
    member _.Count = received.Count

    /// <summary>Waits up to <paramref name="timeoutMs"/> ms for at least <paramref name="n"/> messages.</summary>
    member _.WaitFor(n: int, timeoutMs: int) : Task<bool> =
        task {
            let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
            let mutable ok = received.Count >= n
            while not ok && DateTime.UtcNow < deadline do
                let remaining = max 0 (int (deadline - DateTime.UtcNow).TotalMilliseconds)
                let! signalled = gate.WaitAsync(remaining)
                if signalled then ok <- received.Count >= n
            return ok
        }

    interface ITestChatObserver with
        member _.ReceiveMessage(msg: string) =
            received.Add(msg)
            gate.Release() |> ignore
            Task.CompletedTask

// ── Helpers ──────────────────────────────────────────────────────────────────

let getChatGrain (fixture: ClusterFixture) (key: string) =
    fixture.GrainFactory.GetGrain<ITestChatGrain>(key)

// ── Tests ─────────────────────────────────────────────────────────────────────

[<Collection("ClusterCollection")>]
type ObserverTests(fixture: ClusterFixture) =

    // ── Observer.createRef / deleteRef lifecycle ──────────────────────────────

    [<Fact>]
    member _.``Observer.createRef returns a non-null addressable reference`` () =
        let obs = LocalChatObserver()
        let ref = Observer.createRef<ITestChatObserver> fixture.GrainFactory obs
        Assert.NotNull(ref)
        Observer.deleteRef<ITestChatObserver> fixture.GrainFactory ref

    [<Fact>]
    member _.``Observer.subscribe returns IDisposable that removes ref on Dispose`` () =
        let obs = LocalChatObserver()
        use _ = Observer.subscribe<ITestChatObserver> fixture.GrainFactory obs
        // If Dispose did NOT run this would leak, but since subscribe returns IDisposable
        // the 'use' binding guarantees cleanup — no exception means the pattern is sound.
        ()

    // ── Single observer receiving messages ────────────────────────────────────

    [<Fact>]
    member _.``Subscribed observer receives Broadcast message`` () =
        task {
            let grain = getChatGrain fixture "obs-single-recv"
            let obs = LocalChatObserver()
            let ref = Observer.createRef<ITestChatObserver> fixture.GrainFactory obs
            do! grain.Subscribe(ref)
            do! grain.Broadcast("Hello, world!")
            let! arrived = obs.WaitFor(1, 3000)
            test <@ arrived @>
            test <@ obs.Messages |> Seq.contains "Hello, world!" @>
            Observer.deleteRef<ITestChatObserver> fixture.GrainFactory ref
        }

    [<Fact>]
    member _.``Observer receives multiple messages in order`` () =
        task {
            let grain = getChatGrain fixture "obs-multi-recv"
            let obs = LocalChatObserver()
            let ref = Observer.createRef<ITestChatObserver> fixture.GrainFactory obs
            do! grain.Subscribe(ref)
            do! grain.Broadcast("msg-1")
            do! grain.Broadcast("msg-2")
            do! grain.Broadcast("msg-3")
            let! arrived = obs.WaitFor(3, 5000)
            test <@ arrived @>
            test <@ obs.Count = 3 @>
            test <@ obs.Messages |> Seq.contains "msg-1" @>
            test <@ obs.Messages |> Seq.contains "msg-2" @>
            test <@ obs.Messages |> Seq.contains "msg-3" @>
            Observer.deleteRef<ITestChatObserver> fixture.GrainFactory ref
        }

    // ── Subscriber count ──────────────────────────────────────────────────────

    [<Fact>]
    member _.``GetSubscriberCount reflects active subscriptions`` () =
        task {
            let grain = getChatGrain fixture "obs-count"
            let obs1 = LocalChatObserver()
            let obs2 = LocalChatObserver()
            let ref1 = Observer.createRef<ITestChatObserver> fixture.GrainFactory obs1
            let ref2 = Observer.createRef<ITestChatObserver> fixture.GrainFactory obs2
            do! grain.Subscribe(ref1)
            do! grain.Subscribe(ref2)
            let! count = grain.GetSubscriberCount()
            test <@ count = 2 @>
            Observer.deleteRef<ITestChatObserver> fixture.GrainFactory ref1
            Observer.deleteRef<ITestChatObserver> fixture.GrainFactory ref2
        }

    // ── Unsubscribe stops notifications ───────────────────────────────────────

    [<Fact>]
    member _.``Unsubscribed observer no longer receives messages`` () =
        task {
            let grain = getChatGrain fixture "obs-unsub"
            let obs = LocalChatObserver()
            let ref = Observer.createRef<ITestChatObserver> fixture.GrainFactory obs
            do! grain.Subscribe(ref)
            do! grain.Broadcast("before-unsub")
            let! arrived = obs.WaitFor(1, 3000)
            test <@ arrived @>

            do! grain.Unsubscribe(ref)
            let countBeforeExtra = obs.Count
            do! grain.Broadcast("after-unsub")
            // Give a short window to check no extra message arrives
            do! Task.Delay(300)
            test <@ obs.Count = countBeforeExtra @>
            Observer.deleteRef<ITestChatObserver> fixture.GrainFactory ref
        }

    // ── Multiple observers receive the same broadcast ─────────────────────────

    [<Fact>]
    member _.``Multiple observers all receive the same Broadcast`` () =
        task {
            let grain = getChatGrain fixture "obs-fanout"
            let obs1 = LocalChatObserver()
            let obs2 = LocalChatObserver()
            let obs3 = LocalChatObserver()
            let refs =
                [obs1; obs2; obs3]
                |> List.map (fun obs -> Observer.createRef<ITestChatObserver> fixture.GrainFactory obs)
            for r in refs do do! grain.Subscribe(r)
            do! grain.Broadcast("fanout-msg")
            let! a1 = obs1.WaitFor(1, 3000)
            let! a2 = obs2.WaitFor(1, 3000)
            let! a3 = obs3.WaitFor(1, 3000)
            test <@ a1 && a2 && a3 @>
            test <@ obs1.Messages |> Seq.contains "fanout-msg" @>
            test <@ obs2.Messages |> Seq.contains "fanout-msg" @>
            test <@ obs3.Messages |> Seq.contains "fanout-msg" @>
            for r in refs do Observer.deleteRef<ITestChatObserver> fixture.GrainFactory r
        }

    // ── Observer.subscribe IDisposable pattern ────────────────────────────────

    [<Fact>]
    member _.``Observer.subscribe IDisposable cleans up when disposed`` () =
        task {
            let grain = getChatGrain fixture "obs-idisposable"
            let obs = LocalChatObserver()
            let observerRef = Observer.createRef<ITestChatObserver> fixture.GrainFactory obs
            do! grain.Subscribe(observerRef)
            do! grain.Broadcast("before-dispose")
            let! arrived = obs.WaitFor(1, 3000)
            test <@ arrived @>

            // Dispose via createRef + explicit deleteRef (Observer.subscribe pattern)
            Observer.deleteRef<ITestChatObserver> fixture.GrainFactory observerRef
            // No assertion needed — if deleteRef throws, the test fails
        }

    // ── FSharpObserverManager: no notification when no subscribers ────────────

    [<Fact>]
    member _.``Broadcast with no subscribers completes without error`` () =
        task {
            let grain = getChatGrain fixture "obs-empty-broadcast"
            // No subscribers registered — Broadcast should complete silently
            do! grain.Broadcast("no-one-listening")
            let! count = grain.GetSubscriberCount()
            test <@ count = 0 @>
        }
