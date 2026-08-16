/// Phase 0 item 9 — every attached persistent facet is created early enough (in
/// the custom `IGrainActivator`, via `IPersistentStateFactory.Create`) for the
/// Orleans SetupState load.
module Orleans.FSharp.SeamProof.Item09_PersistentFacetTests

open System
open System.Threading.Tasks
open Xunit

let private tryField (reply: string) (name: string) =
    reply.Split '|'
    |> Array.tryPick (fun part ->
        if part.StartsWith(name + "=", StringComparison.Ordinal) then
            Some(part.Substring(name.Length + 1))
        else
            None)

let private field reply name =
    match tryField reply name with
    | Some value -> value
    | None -> failwith $"field '{name}' not present in reply '{reply}'"

[<Collection("SeamCluster")>]
type PersistentFacetTests(fixture: SeamClusterFixture) =

    /// Deactivates, then waits until a fresh activation reports a loaded record.
    let recycle key =
        task {
            let! _ = fixture.Call SeamGrainTypes.Probe key "deactivate" ""
            let deadline = DateTime.UtcNow.AddSeconds 20.0
            let mutable info = ""

            while tryField info "recordExistsAtActivation" <> Some "True"
                  && DateTime.UtcNow < deadline do
                do! Task.Delay 200
                let! current = fixture.Call SeamGrainTypes.Probe key "stateInfo" ""
                info <- current

            return info
        }

    [<Fact>]
    member _.``a first activation sees no record and the facets are still usable``() =
        task {
            let key = $"state-fresh-{Guid.NewGuid():N}"
            let! info = fixture.Call SeamGrainTypes.Probe key "stateInfo" ""
            Assert.Equal("False", field info "recordExistsAtActivation")
            Assert.Equal("", field info "stateAtActivation")

            let! secondInfo = fixture.Call SeamGrainTypes.Probe key "secondInfo" ""
            Assert.Equal("False", field secondInfo "recordExistsAtActivation")

            let! read = fixture.Call SeamGrainTypes.Probe key "stateRead" ""
            Assert.Equal("", read)
        }

    [<Fact>]
    member _.``every attached facet is loaded by SetupState on the next activation``() =
        task {
            let key = $"state-early-{Guid.NewGuid():N}"

            let! written = fixture.Call SeamGrainTypes.Probe key "stateWrite" "durable-early"
            Assert.Equal("ok", written)
            let! writtenSecond = fixture.Call SeamGrainTypes.Probe key "secondWrite" "durable-second"
            Assert.Equal("ok", writtenSecond)

            let! info = recycle key

            // Both facets were created in IGrainActivator.CreateInstance, so both
            // had subscribed when the lifecycle reached SetupState: their values
            // are present *at activation time*, not merely after a later read.
            Assert.Equal("True", field info "recordExistsAtActivation")
            Assert.Equal("durable-early", field info "stateAtActivation")

            let! secondInfo = fixture.Call SeamGrainTypes.Probe key "secondInfo" ""
            Assert.Equal("True", field secondInfo "recordExistsAtActivation")
            Assert.Equal("durable-second", field secondInfo "stateAtActivation")

            let! read = fixture.Call SeamGrainTypes.Probe key "stateRead" ""
            Assert.Equal("durable-early", read)
            let! readSecond = fixture.Call SeamGrainTypes.Probe key "secondRead" ""
            Assert.Equal("durable-second", readSecond)
        }

    [<Fact>]
    member _.``creating a facet after activation is rejected outright by Orleans``() =
        task {
            let key = $"state-late-{Guid.NewGuid():N}"
            let! outcome = fixture.Call SeamGrainTypes.Probe key "lateCreate" ""

            // Decisive negative control: `IPersistentStateFactory.Create`
            // subscribes to the activation lifecycle, so it can only be called
            // before the lifecycle starts — i.e. inside the custom activator.
            Assert.StartsWith("rejected:InvalidOperationException", outcome)
            Assert.Contains("Lifecycle has already been started", outcome)
        }

    [<Fact>]
    member _.``the two attached facets are independent named holders``() =
        task {
            let key = $"state-two-{Guid.NewGuid():N}"
            let! _ = fixture.Call SeamGrainTypes.Probe key "stateWrite" "A"
            let! _ = fixture.Call SeamGrainTypes.Probe key "secondWrite" "B"

            let! early = fixture.Call SeamGrainTypes.Probe key "stateRead" ""
            let! second = fixture.Call SeamGrainTypes.Probe key "secondRead" ""

            Assert.Equal("A", early)
            Assert.Equal("B", second)
        }
