/// <summary>
/// Protocol-token, admission-flag, and payload-limit tests for spec 003 Phase 2:
/// the two golden SHA-256 vectors, the direction and length properties of the token, the
/// admission-flag layout including reserved bits, and the four payload boundaries.
/// </summary>
module Orleans.FSharp.Tests.FunctionalProtocolTests

open System
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ──────────────────────────────────────────────────────────────────────────────
// Golden vectors
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the request golden vector matches the specification`` () =
    let token = ProtocolToken.request "chat.room" 1 "join"

    test <@ ProtocolToken.toHex token = "525f112d5114016be421e973fee8aa7e4b439b560f29b419fd374e48336c430e" @>

[<Fact>]
let ``the reply golden vector matches the specification`` () =
    let token = ProtocolToken.reply "chat.room" 1 "join"

    test <@ ProtocolToken.toHex token = "2a2e7b5513cb992ef81759d0e761ef0071ec634be2d8d3b0931f961641ad61bf" @>

[<Fact>]
let ``the golden vectors are the digest of the documented NUL-separated text`` () =
    // The same digest computed from the literal specification input, so a refactor of the
    // token builder cannot quietly change what is hashed.
    let literal = "chat.room\000" + "1\000" + "join\000" + "request"

    let expected =
        Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes literal)

    test <@ ProtocolToken.toHex expected = ProtocolToken.toHex (ProtocolToken.request "chat.room" 1 "join") @>

// ──────────────────────────────────────────────────────────────────────────────
// Token properties
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a protocol token is always exactly 32 bytes`` () =
    let tokens =
        [ ProtocolToken.request "chat.room" 1 "join"
          ProtocolToken.reply "chat.room" 1 "join"
          ProtocolToken.request "" 1 ""
          ProtocolToken.request (String('x', 4096)) 2147483647 (String('y', 4096)) ]

    test <@ tokens |> List.forall (fun token -> token.Length = ProtocolToken.Length) @>
    test <@ ProtocolToken.Length = 32 @>

[<Fact>]
let ``request and reply tokens of one operation differ`` () =
    let request = ProtocolToken.request "chat.room" 1 "join"
    let reply = ProtocolToken.reply "chat.room" 1 "join"

    test <@ not (ProtocolToken.equal request reply) @>

[<Fact>]
let ``every component of the token input changes the digest`` () =
    let baseline = ProtocolToken.request "chat.room" 1 "join"

    let variants =
        [ ProtocolToken.request "chat.rooms" 1 "join"
          ProtocolToken.request "chat.room" 2 "join"
          ProtocolToken.request "chat.room" 1 "joiN"
          ProtocolToken.reply "chat.room" 1 "join" ]

    test <@ variants |> List.forall (fun variant -> not (ProtocolToken.equal baseline variant)) @>

[<Fact>]
let ``the version is rendered as invariant decimal without sign or padding`` () =
    let previous = Globalization.CultureInfo.CurrentCulture

    try
        // A culture with non-ASCII digits must not change the token.
        Globalization.CultureInfo.CurrentCulture <- Globalization.CultureInfo.GetCultureInfo "ar-SA"
        let token = ProtocolToken.request "chat.room" 1 "join"

        test <@ ProtocolToken.toHex token = "525f112d5114016be421e973fee8aa7e4b439b560f29b419fd374e48336c430e" @>
    finally
        Globalization.CultureInfo.CurrentCulture <- previous

[<Fact>]
let ``token equality rejects null, short, and different tokens`` () =
    let token = ProtocolToken.request "chat.room" 1 "join"

    test <@ ProtocolToken.equal token (ProtocolToken.request "chat.room" 1 "join") @>
    test <@ not (ProtocolToken.equal token null) @>
    test <@ not (ProtocolToken.equal null token) @>
    test <@ not (ProtocolToken.equal token (Array.zeroCreate 31)) @>
    test <@ ProtocolToken.toHex null = "<null>" @>

// ──────────────────────────────────────────────────────────────────────────────
// Admission flags
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the admission-flag layout matches the specification`` () =
    test <@ AdmissionFlags.None = 0uy @>
    test <@ AdmissionFlags.ReadOnly = 0x01uy @>
    test <@ AdmissionFlags.OneWay = 0x02uy @>
    test <@ AdmissionFlags.AlwaysInterleave = 0x04uy @>
    test <@ AdmissionFlags.Reserved = 0xF8uy @>

[<Fact>]
let ``every policy combination composes the expected flag byte`` () =
    let cases =
        [ (false, false, false), 0x00uy
          (true, false, false), 0x01uy
          (false, true, false), 0x02uy
          (true, true, false), 0x03uy
          (false, false, true), 0x04uy
          (true, false, true), 0x05uy
          (false, true, true), 0x06uy
          (true, true, true), 0x07uy ]

    for (readOnly, oneWay, alwaysInterleave), expected in cases do
        test <@ AdmissionFlags.compose readOnly oneWay alwaysInterleave = expected @>

[<Fact>]
let ``reserved bits are detected and valid combinations are not`` () =
    for flags in 0uy .. 7uy do
        test <@ not (AdmissionFlags.hasReserved flags) @>

    for bit in 3 .. 7 do
        let flags = 1uy <<< bit
        test <@ AdmissionFlags.hasReserved flags @>

    test <@ AdmissionFlags.hasReserved 0xFFuy @>

// ──────────────────────────────────────────────────────────────────────────────
// Payload limits — all four boundaries
// ──────────────────────────────────────────────────────────────────────────────

let private boundaries =
    [ PayloadBoundary.CallerRequestSend, "caller request send", "request"
      PayloadBoundary.SiloRequestReceive, "silo request receive", "request"
      PayloadBoundary.SiloReplySend, "silo reply send", "reply"
      PayloadBoundary.CallerReplyReceive, "caller reply receive", "reply" ]

[<Fact>]
let ``each boundary reports its own name and direction`` () =
    for boundary, name, direction in boundaries do
        test <@ boundary.Name = name @>
        test <@ boundary.Direction = direction @>

[<Fact>]
let ``a payload at the limit is accepted at every boundary`` () =
    for boundary, _, _ in boundaries do
        PayloadLimit.ensure boundary "chat.room" "join" 1024 1024

[<Fact>]
let ``an oversized payload fails at every boundary with a complete diagnostic`` () =
    for boundary, name, direction in boundaries do
        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                PayloadLimit.ensure boundary "chat.room" "join" 1025 1024)

        test <@ error.Message.Contains "Orleans.FSharp functional transport" @>
        test <@ error.Message.Contains "chat.room" @>
        test <@ error.Message.Contains "join" @>
        test <@ error.Message.Contains direction @>
        test <@ error.Message.Contains name @>
        test <@ error.Message.Contains "1025" @>
        test <@ error.Message.Contains "1024" @>

[<Fact>]
let ``a non-positive payload limit is a configuration error`` () =
    test <@ PayloadLimit.validateLimit 1 = 1 @>

    for invalid in [ 0; -1; Int32.MinValue ] do
        Assert.Throws<InvalidOperationException>(fun () -> PayloadLimit.validateLimit invalid |> ignore)
        |> ignore

[<Fact>]
let ``the default payload limit is 16 MiB`` () =
    test <@ FunctionalGrainTransportOptions.DefaultMaxPayloadBytes = 16 * 1024 * 1024 @>
    test <@ FunctionalGrainTransportOptions().MaxPayloadBytes = 16 * 1024 * 1024 @>

// ──────────────────────────────────────────────────────────────────────────────
// Reserved identifiers
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the functional interface id is the reserved prefix plus the grain type`` () =
    test <@ FunctionalIds.interfaceId "chat.room" = "orleans.fsharp.functional/chat.room" @>
    test <@ FunctionalIds.grainInterfaceType("chat.room").ToString() = "orleans.fsharp.functional/chat.room" @>
    test <@ FunctionalIds.InterfaceVersion = 1us @>
