module Orleans.FSharp.Tests.OneWayTests

// FS44: deprecated CE keywords (reentrant, oneWay, interleave) used here intentionally to assert legacy behaviour.
#nowarn "44"

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans
open Orleans.FSharp

/// <summary>Test grain interface with a one-way method.</summary>
type IOneWayGrain =
    inherit IGrainWithStringKey
    abstract FireAndForget: string -> Task
    abstract GetValue: unit -> Task<string>

/// <summary>Fake grain that records calls for testing invokeOneWay.</summary>
type FakeOneWayGrain(mutable_value: string ref) =
    interface IOneWayGrain with
        member _.FireAndForget(data) =
            mutable_value.Value <- data
            Task.CompletedTask

        member _.GetValue() =
            Task.FromResult(mutable_value.Value)

// --- oneWay CE keyword tests ---

[<Fact>]
let ``grain CE default has empty OneWayMethods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.OneWayMethods = Set.empty @>

[<Fact>]
let ``grain CE oneWay keyword adds method name to OneWayMethods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            oneWay "FireAndForget"
        }

    test <@ def.OneWayMethods |> Set.contains "FireAndForget" @>

[<Fact>]
let ``grain CE multiple oneWay calls add multiple methods`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            oneWay "FireAndForget"
            oneWay "SendNotification"
        }

    test <@ def.OneWayMethods |> Set.count = 2 @>
    test <@ def.OneWayMethods |> Set.contains "FireAndForget" @>
    test <@ def.OneWayMethods |> Set.contains "SendNotification" @>

[<Fact>]
let ``grain CE oneWay does not affect other fields`` () =
    let def =
        grain {
            defaultState 42
            handle (fun state _msg -> task { return state, box state })
            persist "Default"
            oneWay "FireAndForget"
        }

    test <@ def.DefaultState = Some 42 @>
    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.Handler |> Option.isSome @>
    test <@ def.OneWayMethods |> Set.contains "FireAndForget" @>

[<Fact>]
let ``grain CE oneWay and interleave can coexist`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            oneWay "FireAndForget"
            interleave "GetValue"
        }

    test <@ def.OneWayMethods |> Set.contains "FireAndForget" @>
    test <@ def.InterleavedMethods |> Set.contains "GetValue" @>

[<Fact>]
let ``grain CE oneWay and reentrant can coexist`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            reentrant
            oneWay "FireAndForget"
        }

    test <@ def.IsReentrant = true @>
    test <@ def.OneWayMethods |> Set.contains "FireAndForget" @>

// --- invokeOneWay tests ---

[<Fact>]
let ``GrainRef.invokeOneWay calls one-way method on grain`` () =
    task {
        let value = ref "initial"

        let ref: GrainRef<IOneWayGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "test-key"
                Grain = FakeOneWayGrain(value)
            }

        do! GrainRef.invokeOneWay ref (fun g -> g.FireAndForget("updated"))
        test <@ value.Value = "updated" @>
    }

[<Fact>]
let ``GrainRef.invokeOneWay returns completed task`` () =
    task {
        let value = ref ""

        let ref: GrainRef<IOneWayGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "test-key"
                Grain = FakeOneWayGrain(value)
            }

        let t = GrainRef.invokeOneWay ref (fun g -> g.FireAndForget("data"))
        test <@ t.IsCompleted @>
        do! t
    }

[<Fact>]
let ``GrainRef.invokeOneWay works independently of invoke`` () =
    task {
        let value = ref "start"

        let ref: GrainRef<IOneWayGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "test-key"
                Grain = FakeOneWayGrain(value)
            }

        // Use invoke for a method returning Task<T>
        let! result = GrainRef.invoke ref (fun g -> g.GetValue())
        test <@ result = "start" @>

        // Use invokeOneWay for fire-and-forget
        do! GrainRef.invokeOneWay ref (fun g -> g.FireAndForget("modified"))
        test <@ value.Value = "modified" @>
    }

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``oneWay stores correct method name for any non-empty string`` (name: NonEmptyString) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            oneWay name.Get
        }
    def.OneWayMethods |> Set.contains name.Get

[<Property>]
let ``oneWay is idempotent: registering same name twice results in set of 1`` (name: NonEmptyString) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            oneWay name.Get
            oneWay name.Get
        }
    def.OneWayMethods |> Set.count = 1
