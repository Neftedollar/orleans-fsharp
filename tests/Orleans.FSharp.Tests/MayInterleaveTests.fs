module Orleans.FSharp.Tests.MayInterleaveTests

/// <summary>
/// Unit tests for message-type [MayInterleave] on the universal grain:
///   * the process-wide static <c>FSharpInterleaveRegistry</c> (Orleans.FSharp.Abstractions),
///   * the <c>interleaveMessage</c> grain CE operation (Orleans.FSharp), and
///   * the registration push performed by <c>AddFSharpGrain</c> (Orleans.FSharp.Runtime).
///
/// The static registry is process-global, so every message type used as a registry key here
/// is unique to this file and the deterministic "false" case (<c>NeverInterleavable</c>) is
/// never registered anywhere — guaranteeing order-independent results across the whole run.
/// </summary>

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp
open Orleans.FSharp.Runtime

// ── Test types (unique to this file; see module doc) ─────────────────────────

/// Registered directly via FSharpInterleaveRegistry.Register.
type DirectlyRegistered =
    | Dr

/// Registered indirectly through the grain CE + AddFSharpGrain wiring.
type WiredInterleavable =
    | Wi

/// Used only to assert CE recording; never pushed to the static registry.
type CeRecordedMsg =
    | Cr

/// Never registered anywhere — the deterministic "false" case.
/// A plain sealed class so it derives only from <c>object</c>; no registered type can be
/// assignable from it.
[<Sealed>]
type NeverInterleavable() =
    class
    end

/// Multi-case field-carrying DU: field-carrying cases compile to nested subtypes
/// (IlvQuery+Read, IlvQuery+Write) that are distinct from IlvQuery itself.
/// Used by the assignability-path test below.
type IlvQuery =
    | Read of id: int
    | Write of value: string

type MiState = { N: int }

// ── FSharpInterleaveRegistry (process-wide static registry) ──────────────────

[<Fact>]
let ``FSharpInterleaveRegistry true after Register, false for unregistered`` () =
    FSharpInterleaveRegistry.Register(typeof<DirectlyRegistered>)
    test <@ FSharpInterleaveRegistry.MayInterleave(typeof<DirectlyRegistered>) = true @>
    test <@ FSharpInterleaveRegistry.MayInterleave(typeof<NeverInterleavable>) = false @>

[<Fact>]
let ``FSharpInterleaveRegistry Register is idempotent`` () =
    FSharpInterleaveRegistry.Register(typeof<DirectlyRegistered>)
    FSharpInterleaveRegistry.Register(typeof<DirectlyRegistered>)
    test <@ FSharpInterleaveRegistry.MayInterleave(typeof<DirectlyRegistered>) = true @>

[<Fact>]
let ``FSharpInterleaveRegistry MayInterleave is false for null`` () =
    test <@ FSharpInterleaveRegistry.MayInterleave(null) = false @>

// ── grain CE: interleaveMessage records the message type ─────────────────────

[<Fact>]
let ``grain CE default has empty InterleaveMessageTypes`` () =
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun (state: MiState) (_msg: CeRecordedMsg) -> task { return state, box state })
        }

    test <@ List.isEmpty def.InterleaveMessageTypes @>

[<Fact>]
let ``grain CE interleaveMessage records typeof Msg`` () =
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun (state: MiState) (_msg: CeRecordedMsg) -> task { return state, box state })
            interleaveMessage typeof<CeRecordedMsg>
        }

    test <@ def.InterleaveMessageTypes |> List.contains typeof<CeRecordedMsg> @>

[<Fact>]
let ``grain CE interleaveMessage dedups a repeated registration`` () =
    let def =
        grain {
            defaultState { N = 0 }
            handle (fun (state: MiState) (_msg: CeRecordedMsg) -> task { return state, box state })
            interleaveMessage typeof<CeRecordedMsg>
            interleaveMessage typeof<CeRecordedMsg>
        }

    let occurrences =
        def.InterleaveMessageTypes
        |> List.filter (fun t -> t = typeof<CeRecordedMsg>)
        |> List.length

    test <@ occurrences = 1 @>

// ── AddFSharpGrain pushes interleavable types into the static registry ────────

[<Fact>]
let ``AddFSharpGrain pushes interleavable message types into the static registry`` () =
    // Before registration the type is not interleavable.
    test <@ FSharpInterleaveRegistry.MayInterleave(typeof<WiredInterleavable>) = false @>

    let services = ServiceCollection()

    let def =
        grain {
            defaultState { N = 0 }
            handle (fun (state: MiState) (_msg: WiredInterleavable) -> task { return state, box state })
            interleaveMessage typeof<WiredInterleavable>
        }

    services.AddFSharpGrain<MiState, WiredInterleavable>(def) |> ignore

    test <@ FSharpInterleaveRegistry.MayInterleave(typeof<WiredInterleavable>) = true @>

// ── Assignability path: DU case subtypes interleave via the registered DU type ──

[<Fact>]
let ``registered DU type interleaves its field-carrying nested case types`` () =
    FSharpInterleaveRegistry.Register(typeof<IlvQuery>)
    let caseType = (box (Read 1)).GetType() // IlvQuery+Read nested subtype
    // Prove we are on the assignability path, NOT the ContainsKey fast-path:
    test <@ caseType <> typeof<IlvQuery> @>
    test <@ FSharpInterleaveRegistry.MayInterleave(caseType) = true @>
