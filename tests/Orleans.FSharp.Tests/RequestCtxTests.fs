module Orleans.FSharp.Tests.RequestCtxTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.Runtime

/// <summary>Tests for RequestCtx module — set, get, getOrDefault, remove, withValue.</summary>

/// <summary>Clean up RequestContext before each test to avoid cross-test pollution.</summary>
let private cleanup (key: string) =
    RequestContext.Remove(key) |> ignore

[<Fact>]
let ``set and get round-trip for string value`` () =
    let key = "test-string-roundtrip"

    try
        RequestCtx.set key (box "hello")
        let result = RequestCtx.get<string> key
        test <@ result = Some "hello" @>
    finally
        cleanup key

[<Fact>]
let ``set and get round-trip for int value`` () =
    let key = "test-int-roundtrip"

    try
        RequestCtx.set key (box 42)
        let result = RequestCtx.get<int> key
        test <@ result = Some 42 @>
    finally
        cleanup key

[<Fact>]
let ``get returns None for missing key`` () =
    let result = RequestCtx.get<string> "nonexistent-key-abc123"
    test <@ result = None @>

[<Fact>]
let ``get returns None for wrong type`` () =
    let key = "test-wrong-type"

    try
        RequestCtx.set key (box 42)
        let result = RequestCtx.get<string> key
        test <@ result = None @>
    finally
        cleanup key

[<Fact>]
let ``getOrDefault returns value when key exists`` () =
    let key = "test-getOrDefault-exists"

    try
        RequestCtx.set key (box "found")
        let result = RequestCtx.getOrDefault<string> key "default"
        test <@ result = "found" @>
    finally
        cleanup key

[<Fact>]
let ``getOrDefault returns default when key missing`` () =
    let result = RequestCtx.getOrDefault<string> "nonexistent-getOrDefault" "fallback"
    test <@ result = "fallback" @>

[<Fact>]
let ``remove clears the value`` () =
    let key = "test-remove"

    try
        RequestCtx.set key (box "value")
        RequestCtx.remove key
        let result = RequestCtx.get<string> key
        test <@ result = None @>
    finally
        cleanup key

[<Fact>]
let ``withValue sets and removes value around function`` () =
    task {
        let key = "test-withValue"

        let! result =
            RequestCtx.withValue key (box "scoped") (fun () ->
                task {
                    let v = RequestCtx.get<string> key
                    return v
                })

        test <@ result = Some "scoped" @>
        // Value should be removed after withValue completes
        let after = RequestCtx.get<string> key
        test <@ after = None @>
    }

[<Fact>]
let ``withValue removes value even on exception`` () =
    task {
        let key = "test-withValue-exception"

        let mutable exnCaught = false

        try
            do!
                RequestCtx.withValue key (box "temp") (fun () ->
                    task {
                        failwith "boom"
                        return ()
                    })
        with _ ->
            exnCaught <- true

        test <@ exnCaught @>
        let after = RequestCtx.get<string> key
        test <@ after = None @>
    }

[<Fact>]
let ``set overwrites existing value`` () =
    let key = "test-overwrite"

    try
        RequestCtx.set key (box "first")
        RequestCtx.set key (box "second")
        let result = RequestCtx.get<string> key
        test <@ result = Some "second" @>
    finally
        cleanup key

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``set and get round-trip for any int value`` (key: NonEmptyString) (value: int) =
    let k = $"prop-int-{key.Get}"

    try
        RequestCtx.set k (box value)
        RequestCtx.get<int> k = Some value
    finally
        RequestContext.Remove(k) |> ignore

[<Property>]
let ``getOrDefault returns stored value for any string key and value`` (key: NonEmptyString) (value: NonEmptyString) =
    let k = $"prop-getordefault-{key.Get}"

    try
        RequestCtx.set k (box value.Get)
        RequestCtx.getOrDefault<string> k "fallback" = value.Get
    finally
        RequestContext.Remove(k) |> ignore
