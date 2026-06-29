module Orleans.FSharp.Tests.OneWayTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
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
