/// <summary>
/// Spec 004 item 9 over the real transport: the C#-callable facade bound to the same probe
/// contract every other functional integration test uses, called through a live cluster rather
/// than through the in-memory sender the unit suite exercises.
/// </summary>
/// <remarks>
/// The unit suite (<c>FunctionalInteropTests</c>) owns the binding rules; there is nothing
/// version-specific about them, and repeating them here would only make the cluster suite slower.
/// What this file adds is the part the in-memory sender cannot show: that a facade call travels
/// the production Orleans path -- real grain reference, real serialization, real activation --
/// for every argument and reply shape that path treats differently, including a mapped domain key
/// passed as a boxed <c>obj</c> and a one-way operation.
/// </remarks>
module Orleans.FSharp.Integration.FunctionalInteropIntegrationTests

open System
open System.Threading.Tasks
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalClusterFixture
open Xunit

/// <summary>Waits for a target-recorded observation, or gives up after ten seconds.</summary>
let private waitForObservation (key: string) =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 10.0
        let mutable observed = Probe.tryGet key

        while observed.IsNone && DateTime.UtcNow < deadline do
            do! Task.Delay 100
            observed <- Probe.tryGet key

        return observed
    }

/// <summary>A partial C#-shaped facade over <c>ProbeApi</c>.</summary>
type IProbeFacade =
    /// Single argument, single reply.
    abstract Echo: message: string -> Task<string>
    /// Unit argument: a parameterless member.
    abstract Peek: unit -> Task<int>
    /// A tuple argument, packed from two parameters.
    abstract Tag: name: string * value: string -> Task<string>
    /// An F# record argument and a discriminated-union reply, both through the F# binary codec.
    abstract Note: note: Note -> Task<NoteResult>
    /// A one-way operation, declared as the bare Task a C# author writes.
    abstract Bump: delay: int -> Task

[<Collection("FunctionalCluster")>]
type InteropTests(fixture: FunctionalClusterFixture) =

    /// <summary>
    /// The key is a mapped domain key (<c>ProbeId</c>, not the native string), boxed through the
    /// facade's <c>obj</c> parameter -- so this also proves the key check accepts what the
    /// contract's own codec expects rather than only a native key type.
    /// </summary>
    let facade key =
        FunctionalGrainInterop.For<IProbeFacade>(probeContract, fixture.Client, ProbeId.create key)

    [<Fact>]
    member _.``a facade call travels the real transport for every argument and reply shape``() =
        task {
            let probe = facade "interop-shapes"

            let! echoed = probe.Echo "hello from a facade"
            Assert.Equal("hello from a facade", echoed)

            let! tagged = probe.Tag("colour", "green")
            Assert.Equal("colour=green", tagged)

            let! inFlight = probe.Peek()
            Assert.Equal(0, inFlight)

            let! accepted =
                probe.Note
                    { title = "release notes"
                      tags = [ "spec-004"; "interop" ]
                      author = Some "alice" }

            match accepted with
            | Accepted(id, echoedNote) ->
                Assert.Equal(2, id)
                Assert.Equal("release notes", echoedNote.title)
                Assert.Equal<string list>([ "spec-004"; "interop" ], echoedNote.tags)
                Assert.Equal(Some "alice", echoedNote.author)
            | Rejected reason -> Assert.Fail $"the note was rejected: {reason}"

            let! rejected = probe.Note { title = "   "; tags = []; author = None }
            Assert.Equal(Rejected "the title is blank", rejected)
        }

    [<Fact>]
    member _.``a one-way operation called through a facade is delivered``() =
        task {
            let key = $"interop-oneway-{Guid.NewGuid():N}"
            let probe = facade key

            // The bare Task returns as soon as the send is accepted; the delivery is observed on
            // the target, which is the only place a one-way call can be observed at all.
            do! probe.Bump 0

            let! observed = waitForObservation $"bump:{FunctionalGrainTypes.Probe}/{key}"
            Assert.Equal(Some "delivered", observed)
        }

    [<Fact>]
    member _.``the same facade interface binds independently to two keys``() =
        task {
            let first = facade "interop-first"
            let second = facade "interop-second"

            let! a = first.Echo "a"
            let! b = second.Echo "b"
            Assert.Equal("a", a)
            Assert.Equal("b", b)

            // Distinct activations: each key has its own state, so what the first wrote is not
            // what the second reads.
            let! firstTag = first.Tag("owner", "first")
            let! secondTag = second.Tag("owner", "second")
            Assert.Equal("owner=first", firstTag)
            Assert.Equal("owner=second", secondTag)
        }
