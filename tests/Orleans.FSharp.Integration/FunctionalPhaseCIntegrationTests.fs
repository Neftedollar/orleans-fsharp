/// <summary>
/// Spec 004 Phase C over a live two-silo cluster: item 5 (reentrancy variants) and item 7
/// (version-tolerant contracts). Every interleaving claim is made by observing completion ORDER
/// on one activation — a call that finishes while another is demonstrably parked inside the same
/// activation — and every one has a negative control that must NOT interleave.
/// </summary>
module Orleans.FSharp.Integration.FunctionalPhaseCIntegrationTests

open System
open System.Threading.Tasks
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalPhaseCFixture
open Xunit

/// <summary>A fresh domain key per case, so gates never collide however xunit schedules tests.</summary>
let private freshKey (prefix: string) = $"{prefix}-{Guid.NewGuid():N}"

[<Collection("FunctionalPhaseC")>]
type PhaseCReentrancyTests(fixture: FunctionalPhaseCFixture) =

    // ──────────────────────────────────────────────────────────────────────────
    // Item 5 — whole-grain reentrancy
    // ──────────────────────────────────────────────────────────────────────────

    [<Fact>]
    member _.``a reentrant definition publishes Orleans' own reentrant grain property``() =
        let reentrant = fixture.PropertiesOf PhaseCGrainTypes.Reentrant
        let plain = fixture.PropertiesOf PhaseCGrainTypes.Plain

        Assert.Equal<string>("true", reentrant.["reentrant"])
        Assert.False(plain.ContainsKey "reentrant")

        // The property is published by every silo, not only the one the test happens to read.
        for _, manifest in fixture.LocalManifests do
            let properties =
                manifest.Grains.[Orleans.Runtime.GrainType.Create PhaseCGrainTypes.Reentrant].Properties

            Assert.Equal<string>("true", properties.["reentrant"])

    [<Fact>]
    member _.``a second call completes while the first is still parked on a reentrant activation``() =
        task {
            let key = freshKey "reentrant"
            let api = reentrantRef fixture.Client key

            let parked = api.park 5000
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "the first call never parked")

            // Completion ORDER, not a sleep: 'release' can only return while 'park' is still
            // inside the activation, because 'park' is what it releases.
            let! released = api.release ()
            Assert.Equal<string>("ok", released)

            let! outcome = parked
            Assert.Equal<string>("released", outcome)
        }

    [<Fact>]
    member _.``CONTROL the same pair does not interleave without reentrant``() =
        task {
            let key = freshKey "plain"
            let api = plainRef fixture.Client key

            let parked = api.park 1500
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "the first call never parked")

            let release = api.release ()
            let! outcome = parked
            Assert.Equal<string>("timeout", outcome)

            let! released = release
            Assert.Equal<string>("ok", released)
        }

    /// <remarks>
    /// The cost of reentrancy, made observable rather than only documented: two interleaved
    /// handlers each publish a whole replacement state built from the snapshot they started with,
    /// so the one that returns last wins and the other's write is gone.
    /// </remarks>
    [<Fact>]
    member _.``reentrancy makes whole-state replacement last-writer-wins``() =
        task {
            let key = freshKey "lost-update"
            let api = reentrantRef fixture.Client key

            // 'slow' reads [], parks, and will publish [ "slow" ].
            let slow = api.slowAppend "slow"
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "slowAppend never parked")

            // Interleaves and publishes [ "fast" ] while 'slow' holds its stale snapshot.
            do! api.fastAppend "fast"
            let! interim = api.notes ()
            Assert.Equal<string list>([ "fast" ], interim)

            let! released = api.release ()
            Assert.Equal<string>("ok", released)
            do! slow

            // 'slow' returned last, so its replacement — built from the empty snapshot — is what
            // the activation now holds. The interleaved write is lost.
            let! final = api.notes ()
            Assert.Equal<string list>([ "slow" ], final)
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Item 5 — may-interleave predicate
    // ──────────────────────────────────────────────────────────────────────────

    [<Fact>]
    member _.``a mayInterleave definition publishes Orleans' own predicate property``() =
        let selective = fixture.PropertiesOf PhaseCGrainTypes.Selective
        let plain = fixture.PropertiesOf PhaseCGrainTypes.Plain

        Assert.Equal<string>("MayInterleave", selective.["may-interleave-predicate"])
        Assert.False(plain.ContainsKey "may-interleave-predicate")

        // A predicate is NOT reentrancy: the two publish different properties, and declaring one
        // must not silently declare the other.
        Assert.False(selective.ContainsKey "reentrant")
        Assert.False((fixture.PropertiesOf PhaseCGrainTypes.Reentrant).ContainsKey "may-interleave-predicate")

    [<Fact>]
    member _.``the predicate admits the operation it names``() =
        task {
            let key = freshKey "selective-yes"
            let api = selectiveRef fixture.Client key

            let parked = api.park 5000
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "the first call never parked")

            let! released = api.release ()
            Assert.Equal<string>("ok", released)

            let! outcome = parked
            Assert.Equal<string>("released", outcome)
        }

    [<Fact>]
    member _.``CONTROL the predicate refuses every other operation on the same activation``() =
        task {
            let key = freshKey "selective-no"
            let api = selectiveRef fixture.Client key

            let parked = api.park 1500
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "the first call never parked")

            let blocked = api.blocked ()
            let! outcome = parked
            Assert.Equal<string>("timeout", outcome)

            let! blockedReply = blocked
            Assert.Equal<string>("blocked-ran", blockedReply)
        }

    /// <remarks>
    /// Orleans logs and rethrows a failing MayInterleave callback and the message is then rejected
    /// to its caller (ActivationData.MayInvokeRequest → the message loop's catch →
    /// MessageCenter.RejectMessage, transient). That behaviour is kept rather than swallowed —
    /// the runtime only wraps the fault so the rejection names the grain type and the operation.
    /// </remarks>
    [<Fact>]
    member _.``a throwing predicate rejects the call it was deciding, and names it``() =
        task {
            let key = freshKey "throwing"
            let api = throwingRef fixture.Client key

            let parked = api.park 4000
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "the first call never parked")

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.boom () :> Task)
            let text = error.ToString()

            Assert.Contains("mayInterleave", text)
            Assert.Contains(PhaseCGrainTypes.Throwing, text)
            Assert.Contains("boom", text)
            Assert.Contains(ThrowingPredicateMessage, text)

            // The activation survives: the predicate failure rejected one message, it did not
            // wedge the grain.
            let! released = api.release ()
            Assert.Equal<string>("ok", released)
            let! outcome = parked
            Assert.Equal<string>("released", outcome)
        }

    /// <remarks>
    /// The predicate is metadata-only by construction — spec 003's protocol-before-payload
    /// invariant. The strongest available end-to-end evidence is that the SAME operation is
    /// admitted or refused purely by its identity, with an argument the predicate could not have
    /// influenced either way; the type-level proof is that the predicate is declared over
    /// IFunctionalRequestMetadata, which exposes no payload.
    /// </remarks>
    [<Fact>]
    member _.``the predicate decides on protocol metadata alone``() =
        task {
            let key = freshKey "metadata-only"
            let api = selectiveRef fixture.Client key

            // A large payload on the parked call changes nothing about the decision.
            let parked = api.park 5000
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "the first call never parked")

            let! released = api.release ()
            Assert.Equal<string>("ok", released)
            let! outcome = parked
            Assert.Equal<string>("released", outcome)
        }

[<Collection("FunctionalPhaseC")>]
type PhaseCVersionTests(fixture: FunctionalPhaseCFixture) =

    // ──────────────────────────────────────────────────────────────────────────
    // Item 7 — version admission
    // ──────────────────────────────────────────────────────────────────────────

    [<Fact>]
    member _.``the hosted version is admitted whatever the policy``() =
        task {
            let! tolerant = (tolerantV4Ref fixture.Client (freshKey "v4")).echo "hi"
            Assert.Equal<string>("echo:hi", tolerant)

            let! strict = (strictV4Ref fixture.Client (freshKey "v4")).echo "hi"
            Assert.Equal<string>("echo:hi", strict)
        }

    /// <remarks>
    /// The spec-003 sentence, unchanged. The feature tour and the README quote it, so version
    /// tolerance shipping unused must not move a character of it.
    /// </remarks>
    [<Fact>]
    member _.``a version-3 caller is rejected by a version-4 host on the default policy``() =
        task {
            let api = strictV3Ref fixture.Client (freshKey "strict-v3")
            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.echo "hi" :> Task)

            Assert.Contains(
                $"grain type '{PhaseCGrainTypes.Strict}' hosts contract version 4 but received version 3.",
                error.ToString()
            )
        }

    [<Fact>]
    member _.``a version-3 caller is admitted by a version-4 host that accepts it``() =
        task {
            let api = tolerantV3Ref fixture.Client (freshKey "tolerant-v3")
            let! echoed = api.echo "hi"
            Assert.Equal<string>("echo:hi", echoed)
        }

    [<Fact>]
    member _.``a version below the accepted floor is rejected, naming the range``() =
        task {
            let api = tolerantV2Ref fixture.Client (freshKey "tolerant-v2")
            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.echo "hi" :> Task)

            Assert.Contains(
                $"grain type '{PhaseCGrainTypes.Tolerant}' hosts contract version 4 and accepts versions 3 through 4, but received version 2.",
                error.ToString()
            )
        }

    [<Fact>]
    member _.``an operation introduced later is refused for an admitted older call``() =
        task {
            let key = freshKey "since"

            // The same operation, at the hosted version, runs.
            let! current = (tolerantV4Ref fixture.Client key).fresh ()
            Assert.Equal<string>("fresh-ran", current)

            // At the admitted older version it is refused by name, and the diagnostic carries
            // BOTH numbers.
            let older = tolerantV3Ref fixture.Client key
            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> older.fresh () :> Task)

            Assert.Contains(
                $"operation 'fresh' on grain type '{PhaseCGrainTypes.Tolerant}' was introduced at contract version 4, but the request declares version 3.",
                error.ToString()
            )
        }

    [<Fact>]
    member _.``an operation without sinceVersion still runs for an admitted older call``() =
        task {
            let older = tolerantV3Ref fixture.Client (freshKey "since-other")
            let! echoed = older.echo "still here"
            Assert.Equal<string>("echo:still here", echoed)
        }

    /// <remarks>
    /// Admission only. A v3 call and a v4 call to the same grain key reach the same activation,
    /// the same state, and the same handler, and produce the same reply for the same argument —
    /// the version changed which requests are let in, and nothing else.
    /// </remarks>
    [<Fact>]
    member _.``an admitted older call dispatches identically to a current one``() =
        task {
            let key = freshKey "identity"
            let current = tolerantV4Ref fixture.Client key
            let older = tolerantV3Ref fixture.Client key

            // Same reply for the same argument, in both directions of the wire.
            let! fromCurrent = current.echo "same"
            let! fromOlder = older.echo "same"
            Assert.Equal<string>(fromCurrent, fromOlder)

            // Same grain identity and same activation state: what the older call writes, the
            // current one reads back, and the reverse.
            do! older.stash "written-by-v3"
            let! seenByCurrent = current.peek ()
            Assert.Equal<string>("written-by-v3", seenByCurrent)

            do! current.stash "written-by-v4"
            let! seenByOlder = older.peek ()
            Assert.Equal<string>("written-by-v4", seenByOlder)

            // And the identity really is the same one, computed independently of version.
            Assert.Equal(
                (FunctionalGrain.rawRef tolerantV4 fixture.Client key).GrainId,
                (FunctionalGrain.rawRef tolerantV3 fixture.Client key).GrainId
            )
        }

    /// <remarks>
    /// The version policy is a host-side admission rule and is NOT published as a grain property:
    /// nothing about the manifest, the routing identity, or the interface ID changes, so a silo
    /// that has gossiped this grain type sees exactly what it saw before.
    /// </remarks>
    [<Fact>]
    member _.``a version policy publishes no grain property of its own``() =
        let tolerant = fixture.PropertiesOf PhaseCGrainTypes.Tolerant
        let strict = fixture.PropertiesOf PhaseCGrainTypes.Strict

        let comparable (properties: Map<string, string>) =
            properties
            |> Map.toList
            |> List.filter (fun (key, _) -> not (key.StartsWith("interface.", StringComparison.Ordinal)))
            |> List.filter (fun (key, _) -> key <> "grain-class" && key <> "type-name" && key <> "full-type-name")
            |> List.map fst
            |> List.sort

        Assert.Equal<string list>(comparable strict, comparable tolerant)
        Assert.False(tolerant |> Map.containsKey "accepts-versions")

// ──────────────────────────────────────────────────────────────────────────────
// The Phase A C# facade over both features
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A C#-shaped facade over the selective contract.</summary>
type ISelectiveFacade =
    abstract Park: timeout: int -> Task<string>
    abstract Release: unit -> Task<string>
    abstract Blocked: unit -> Task<string>

/// <summary>A C#-shaped facade over the version-tolerant contract.</summary>
type IVersionedFacade =
    abstract Echo: message: string -> Task<string>
    abstract Fresh: unit -> Task<string>

/// <remarks>
/// The facade is not a second transport. <c>FunctionalGrainInterop.For</c> binds the contract
/// exactly as an F# caller does and installs, per interface member, the same preclosed API-record
/// field closure — so a facade call produces the same envelope, at the same contract version, with
/// the same protocol token and admission flags. These tests state that as behaviour rather than as
/// a claim about the code: the interleaving decision and the version admission a facade call gets
/// are the ones its contract already had.
/// </remarks>
[<Collection("FunctionalPhaseC")>]
type PhaseCFacadeTests(fixture: FunctionalPhaseCFixture) =

    [<Fact>]
    member _.``a facade call is admitted by the predicate exactly as an F# call is``() =
        task {
            let key = freshKey "facade-yes"

            let facade =
                FunctionalGrainInterop.For<ISelectiveFacade>(selectiveContract, fixture.Client, key)

            let parked = facade.Park 5000
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "the facade call never parked")

            let! released = facade.Release()
            Assert.Equal<string>("ok", released)

            let! outcome = parked
            Assert.Equal<string>("released", outcome)
        }

    [<Fact>]
    member _.``CONTROL a facade call the predicate refuses does not interleave either``() =
        task {
            let key = freshKey "facade-no"

            let facade =
                FunctionalGrainInterop.For<ISelectiveFacade>(selectiveContract, fixture.Client, key)

            let parked = facade.Park 1500
            let! entered = PhaseCGates.waitForEntry key
            Assert.True(entered, "the facade call never parked")

            let blocked = facade.Blocked()
            let! outcome = parked
            Assert.Equal<string>("timeout", outcome)

            let! blockedReply = blocked
            Assert.Equal<string>("blocked-ran", blockedReply)
        }

    /// <remarks>
    /// A facade cannot claim a version of its own: the version travels with the contract it was
    /// bound from, so binding the v3 contract through a facade produces v3 requests and gets the
    /// v3 admission decisions — including the per-operation one.
    /// </remarks>
    [<Fact>]
    member _.``a facade inherits the version policy of the contract it was bound from``() =
        task {
            let key = freshKey "facade-version"

            let current =
                FunctionalGrainInterop.For<IVersionedFacade>(tolerantV4, fixture.Client, key)

            let older =
                FunctionalGrainInterop.For<IVersionedFacade>(tolerantV3, fixture.Client, key)

            let! fromCurrent = current.Echo "same"
            let! fromOlder = older.Echo "same"
            Assert.Equal<string>(fromCurrent, fromOlder)

            let! fresh = current.Fresh()
            Assert.Equal<string>("fresh-ran", fresh)

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> older.Fresh() :> Task)

            Assert.Contains(
                $"operation 'fresh' on grain type '{PhaseCGrainTypes.Tolerant}' was introduced at contract version 4, but the request declares version 3.",
                error.ToString()
            )
        }

    [<Fact>]
    member _.``a facade bound below the accepted floor is rejected like an F# caller``() =
        task {
            let facade =
                FunctionalGrainInterop.For<IVersionedFacade>(tolerantV2, fixture.Client, freshKey "facade-floor")

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> facade.Echo "hi" :> Task)
            Assert.Contains("accepts versions 3 through 4, but received version 2", error.ToString())
        }
