/// <summary>
/// Key codec tests for spec 003 Phase 1: the five native Orleans key shapes produce the same
/// canonical <c>IdSpan</c> representation as the stock Orleans helpers, and the five mapped
/// shapes satisfy the four codec laws.
/// </summary>
module Orleans.FSharp.Tests.FunctionalKeyCodecTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Runtime
open Orleans.FSharp

let private grainType = GrainType.Create "codec.test"

let private grainIdOf (key: IdSpan) = GrainId.Create(grainType, key)

let private keyText (key: IdSpan) = key.ToString()

let private throws (action: unit -> unit) =
    Assert.Throws<InvalidOperationException>(action)

let private sampleGuid = Guid.Parse "01234567-89ab-cdef-0123-456789abcdef"

/// <summary>True when a string survives the UTF-8 round trip an Orleans string key goes through.</summary>
let private roundTripsUtf8 (value: string) =
    String.Equals(
        Text.Encoding.UTF8.GetString(Text.Encoding.UTF8.GetBytes value),
        value,
        StringComparison.Ordinal
    )

/// <summary>
/// Orleans rejects a null, empty, or white-space string key, and the codec additionally rejects
/// one that is not well-formed UTF-8, so properties about accepted keys skip both.
/// </summary>
let private significant (value: NonNull<string>) =
    not (String.IsNullOrWhiteSpace value.Get) && roundTripsUtf8 value.Get

/// <summary>An unpaired high surrogate: valid UTF-16, no UTF-8 representation.</summary>
/// <remarks>
/// Built from a char array rather than written as a <c>\uD800</c> escape: F# source is UTF-8, so
/// the compiler folds an unpaired surrogate in a string literal to U+FFFD and the two constants
/// below would come out equal — which is the very collision these tests exist to distinguish.
/// </remarks>
let private loneSurrogate = String [| 'r'; 'o'; 'o'; 'm'; '-'; char 0xD800 |]

/// <summary>A different unpaired surrogate — a distinct key that encodes to the same bytes.</summary>
let private otherLoneSurrogate = String [| 'r'; 'o'; 'o'; 'm'; '-'; char 0xDBFF |]

// ──────────────────────────────────────────────────────────────────────────────
// Native encodings equal the stock Orleans helpers
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the native string codec matches the stock Orleans string key`` () =
    let ours = KeyCodecs.stringKey.EncodeKey "general"
    let stock = GrainId.Create(grainType, "general").Key

    test <@ keyText ours = keyText stock @>
    test <@ keyText ours = "general" @>

[<Property>]
let ``the native string codec matches the stock Orleans string key for any value`` (value: NonNull<string>) =
    not (significant value)
    || keyText (KeyCodecs.stringKey.EncodeKey value.Get) = keyText (GrainId.Create(grainType, value.Get).Key)

// ──────────────────────────────────────────────────────────────────────────────
// String keys must be injective, which UTF-8 encoding is not
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the stock Orleans string key is not injective over unpaired surrogates`` () =
    // Ground truth for the two tests below, re-derived from Orleans rather than asserted: two
    // distinct .NET strings really do produce one identical key, because UTF-8 has no encoding
    // for an unpaired surrogate and the default fallback substitutes U+FFFD for each one.
    test <@ loneSurrogate <> otherLoneSurrogate @>

    let first = keyText (GrainId.Create(grainType, loneSurrogate).Key)
    let second = keyText (GrainId.Create(grainType, otherLoneSurrogate).Key)
    test <@ first = second @>

[<Fact>]
let ``a string key that is not well-formed UTF-8 is rejected on encode`` () =
    let first = throws (fun () -> KeyCodecs.stringKey.EncodeKey loneSurrogate |> ignore)
    let second = throws (fun () -> KeyCodecs.stringKey.EncodeKey otherLoneSurrogate |> ignore)

    test <@ first.Message.Contains "not well-formed UTF-8" @>
    test <@ second.Message.Contains "unpaired surrogate" @>

    // The mapped codec composes onto the native one, so it inherits the rule rather than
    // needing its own.
    let mapped = KeyCodecs.stringKeyMapped id id
    let viaMapped = throws (fun () -> mapped.EncodeKey loneSurrogate |> ignore)
    test <@ viaMapped.Message.Contains "not well-formed UTF-8" @>

[<Fact>]
let ``a string key whose bytes are not valid UTF-8 is rejected on decode`` () =
    // 0xFF can never appear in well-formed UTF-8, so these bytes cannot have come from any
    // string this codec encoded; decoding them lossily would return a key nobody asked for.
    let malformed = IdSpan(Array.append (Text.Encoding.UTF8.GetBytes "room-") [| 0xFFuy |])

    let error = throws (fun () -> KeyCodecs.stringKey.DecodeKey(grainIdOf malformed) |> ignore)

    test <@ error.Message.Contains "not the canonical Orleans representation" @>

[<Fact>]
let ``a well-formed string key still decodes`` () =
    // The counterweight: the canonicalization guard must not reject an ordinary key, including
    // one carrying non-ASCII text that UTF-8 encodes in several bytes.
    for value in [ "general"; "комната"; "éèê"; "😀" ] do
        test <@ KeyCodecs.stringKey.DecodeKey(grainIdOf (KeyCodecs.stringKey.EncodeKey value)) = value @>

[<Fact>]
let ``the native Guid codec matches the stock Orleans Guid key`` () =
    let ours = KeyCodecs.guidKey.EncodeKey sampleGuid
    let stock = GrainIdKeyExtensions.CreateGuidKey sampleGuid

    test <@ keyText ours = keyText stock @>
    // Independent golden vector: Orleans renders a Guid key as lower-case "N" format.
    test <@ keyText ours = "0123456789abcdef0123456789abcdef" @>

[<Property>]
let ``the native Guid codec matches the stock Orleans Guid key for any value`` (value: Guid) =
    keyText (KeyCodecs.guidKey.EncodeKey value) = keyText (GrainIdKeyExtensions.CreateGuidKey value)

[<Fact>]
let ``the native int64 codec matches the stock Orleans integer key`` () =
    let ours = KeyCodecs.int64Key.EncodeKey 1234567890123L
    let stock = GrainIdKeyExtensions.CreateIntegerKey 1234567890123L

    test <@ keyText ours = keyText stock @>
    // Independent golden vector: Orleans renders an integer key as unpadded upper-case hex.
    test <@ keyText ours = "11F71FB04CB" @>
    test <@ keyText (KeyCodecs.int64Key.EncodeKey -5L) = "FFFFFFFFFFFFFFFB" @>
    test <@ keyText (KeyCodecs.int64Key.EncodeKey 0L) = "0" @>

[<Property>]
let ``the native int64 codec matches the stock Orleans integer key for any value`` (value: int64) =
    keyText (KeyCodecs.int64Key.EncodeKey value) = keyText (GrainIdKeyExtensions.CreateIntegerKey value)

[<Fact>]
let ``the native Guid compound codec matches the stock Orleans compound key`` () =
    let ours = KeyCodecs.guidCompoundKey.EncodeKey(sampleGuid, "tenant")
    let stock = GrainIdKeyExtensions.CreateGuidKey(sampleGuid, "tenant")

    test <@ keyText ours = keyText stock @>
    test <@ keyText ours = "0123456789abcdef0123456789abcdef+tenant" @>

[<Fact>]
let ``the native int64 compound codec matches the stock Orleans compound key`` () =
    let ours = KeyCodecs.int64CompoundKey.EncodeKey(42L, "tenant")
    let stock = GrainIdKeyExtensions.CreateIntegerKey(42L, "tenant")

    test <@ keyText ours = keyText stock @>
    test <@ keyText ours = "2A+tenant" @>

// ──────────────────────────────────────────────────────────────────────────────
// Native decoding
// ──────────────────────────────────────────────────────────────────────────────

[<Property>]
let ``the native string codec round-trips any significant value`` (value: NonNull<string>) =
    not (significant value)
    || KeyCodecs.stringKey.DecodeKey(grainIdOf (KeyCodecs.stringKey.EncodeKey value.Get)) = value.Get

[<Property>]
let ``the native Guid codec round-trips any value`` (value: Guid) =
    KeyCodecs.guidKey.DecodeKey(grainIdOf (KeyCodecs.guidKey.EncodeKey value)) = value

[<Property>]
let ``the native int64 codec round-trips any value`` (value: int64) =
    KeyCodecs.int64Key.DecodeKey(grainIdOf (KeyCodecs.int64Key.EncodeKey value)) = value

[<Fact>]
let ``a null string key is rejected`` () =
    let error = throws (fun () -> KeyCodecs.stringKey.EncodeKey null |> ignore)
    test <@ error.Message.Contains "must not be null" @>

[<Fact>]
let ``a blank string key is rejected, matching Orleans' own string key validation`` () =
    // GrainId.Create(GrainType, string) throws ArgumentException for an empty or white-space key.
    Assert.Throws<ArgumentException>(fun () -> GrainId.Create(grainType, "") |> ignore) |> ignore

    let empty = throws (fun () -> KeyCodecs.stringKey.EncodeKey "" |> ignore)
    let blank = throws (fun () -> KeyCodecs.stringKey.EncodeKey "  " |> ignore)

    test <@ empty.Message.Contains "empty or white-space" @>
    test <@ blank.Message.Contains "empty or white-space" @>

[<Fact>]
let ``a Guid codec rejects a non-Guid key`` () =
    let error =
        throws (fun () -> KeyCodecs.guidKey.DecodeKey(grainIdOf (IdSpan.Create "general")) |> ignore)

    test <@ error.Message.Contains "not a valid guidKey key" @>

[<Fact>]
let ``a Guid codec rejects a non-canonical upper-case Guid key`` () =
    let error =
        throws (fun () ->
            KeyCodecs.guidKey.DecodeKey(grainIdOf (IdSpan.Create "0123456789ABCDEF0123456789ABCDEF"))
            |> ignore)

    test <@ error.Message.Contains "not the canonical Orleans representation" @>

[<Fact>]
let ``a Guid codec rejects a compound key`` () =
    let error =
        throws (fun () ->
            KeyCodecs.guidKey.DecodeKey(grainIdOf (GrainIdKeyExtensions.CreateGuidKey(sampleGuid, "ext")))
            |> ignore)

    test <@ error.Message.Contains "carries a key extension" @>

[<Fact>]
let ``an int64 codec rejects a non-canonical zero-padded key`` () =
    let error =
        throws (fun () -> KeyCodecs.int64Key.DecodeKey(grainIdOf (IdSpan.Create "00000000000000FF")) |> ignore)

    test <@ error.Message.Contains "not the canonical Orleans representation" @>

[<Fact>]
let ``an int64 codec rejects a non-integer key`` () =
    let error =
        throws (fun () -> KeyCodecs.int64Key.DecodeKey(grainIdOf (IdSpan.Create "zz")) |> ignore)

    test <@ error.Message.Contains "not a valid int64Key key" @>

// ──────────────────────────────────────────────────────────────────────────────
// Compound keys
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a compound codec rejects a blank key extension`` () =
    // Orleans silently drops a null, empty, or white-space extension, which would break the
    // compound round-trip law, so the codec rejects it up front.
    let empty = throws (fun () -> KeyCodecs.guidCompoundKey.EncodeKey(sampleGuid, "") |> ignore)
    let blank = throws (fun () -> KeyCodecs.guidCompoundKey.EncodeKey(sampleGuid, " ") |> ignore)
    let missing = throws (fun () -> KeyCodecs.int64CompoundKey.EncodeKey(1L, null) |> ignore)

    test <@ empty.Message.Contains "empty or white-space" @>
    test <@ blank.Message.Contains "empty or white-space" @>
    test <@ missing.Message.Contains "must not be null" @>

[<Fact>]
let ``a compound codec rejects a NUL-containing key extension`` () =
    let error =
        throws (fun () -> KeyCodecs.guidCompoundKey.EncodeKey(sampleGuid, "a\000b") |> ignore)

    test <@ error.Message.Contains "NUL" @>

[<Fact>]
let ``a compound codec rejects a key without an extension`` () =
    let error =
        throws (fun () ->
            KeyCodecs.guidCompoundKey.DecodeKey(grainIdOf (GrainIdKeyExtensions.CreateGuidKey sampleGuid))
            |> ignore)

    test <@ error.Message.Contains "carries no key extension" @>

[<Property>]
let ``the Guid compound codec round-trips any Guid and significant extension``
    (value: Guid)
    (extension: NonWhiteSpaceString)
    =
    let text = extension.Get.Replace("\000", "x")

    String.IsNullOrWhiteSpace text
    || KeyCodecs.guidCompoundKey.DecodeKey(grainIdOf (KeyCodecs.guidCompoundKey.EncodeKey(value, text))) = (value, text)

[<Property>]
let ``the int64 compound codec round-trips any integer and significant extension``
    (value: int64)
    (extension: NonWhiteSpaceString)
    =
    let text = extension.Get.Replace("\000", "x")

    String.IsNullOrWhiteSpace text
    || KeyCodecs.int64CompoundKey.DecodeKey(grainIdOf (KeyCodecs.int64CompoundKey.EncodeKey(value, text))) = (value, text)

// ──────────────────────────────────────────────────────────────────────────────
// Mapped codecs: the four laws
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>An ordinary domain wrapper, as in the specification's <c>RoomId</c>.</summary>
[<Struct>]
type RoomId =
    | RoomId of string

    static member value(RoomId value) = value

/// <summary>A Guid-backed domain wrapper.</summary>
[<Struct>]
type SessionId =
    | SessionId of Guid

    static member value(SessionId value) = value

/// <summary>An int64-backed domain wrapper.</summary>
[<Struct>]
type OrderId =
    | OrderId of int64

    static member value(OrderId value) = value

/// <summary>A compound domain wrapper.</summary>
type TenantItem = { tenant: Guid; item: string }

let private roomCodec = KeyCodecs.stringKeyMapped RoomId.value RoomId

let private sessionCodec = KeyCodecs.guidKeyMapped SessionId.value SessionId

let private orderCodec = KeyCodecs.int64KeyMapped OrderId.value OrderId

let private tenantCodec =
    KeyCodecs.guidCompoundKeyMapped
        (fun item -> item.tenant, item.item)
        (fun tenant item -> { tenant = tenant; item = item })

// Law 1: decode (encode key) = key under domain equality.

[<Property>]
let ``mapped string codec round-trips the domain key`` (value: NonNull<string>) =
    not (significant value)
    || roomCodec.DecodeKey(grainIdOf (roomCodec.EncodeKey(RoomId value.Get))) = RoomId value.Get

[<Property>]
let ``mapped Guid codec round-trips the domain key`` (value: Guid) =
    sessionCodec.DecodeKey(grainIdOf (sessionCodec.EncodeKey(SessionId value))) = SessionId value

[<Property>]
let ``mapped int64 codec round-trips the domain key`` (value: int64) =
    orderCodec.DecodeKey(grainIdOf (orderCodec.EncodeKey(OrderId value))) = OrderId value

[<Property>]
let ``mapped compound codec round-trips the domain key`` (tenant: Guid) (item: NonWhiteSpaceString) =
    let text = item.Get.Replace("\000", "x")

    if String.IsNullOrWhiteSpace text then
        true
    else
        let key = { tenant = tenant; item = text }
        tenantCodec.DecodeKey(grainIdOf (tenantCodec.EncodeKey key)) = key

// Law 2: encode (decode native) = native in canonical Orleans representation.

[<Property>]
let ``mapped string codec preserves the canonical native representation`` (value: NonNull<string>) =
    not (significant value)
    || (let native = IdSpan.Create value.Get
        keyText (roomCodec.EncodeKey(roomCodec.DecodeKey(grainIdOf native))) = keyText native)

[<Property>]
let ``mapped Guid codec preserves the canonical native representation`` (value: Guid) =
    let native = GrainIdKeyExtensions.CreateGuidKey value
    keyText (sessionCodec.EncodeKey(sessionCodec.DecodeKey(grainIdOf native))) = keyText native

[<Property>]
let ``mapped int64 codec preserves the canonical native representation`` (value: int64) =
    let native = GrainIdKeyExtensions.CreateIntegerKey value
    keyText (orderCodec.EncodeKey(orderCodec.DecodeKey(grainIdOf native))) = keyText native

/// <summary>A compound extension Orleans keeps verbatim: significant, NUL-free, UTF-8 clean.</summary>
let private usableExtension (text: NonWhiteSpaceString) =
    let value = text.Get
    not (String.IsNullOrWhiteSpace value) && not (value.Contains '\000') && roundTripsUtf8 value

[<Property>]
let ``mapped compound codec preserves the canonical native representation``
    (tenant: Guid)
    (item: NonWhiteSpaceString)
    =
    // Law 2 for the compound shape, which had only law 1 and law 4: the native key rebuilt from
    // a decoded domain key must be byte-identical to the one that was decoded.
    not (usableExtension item)
    || (let native = GrainIdKeyExtensions.CreateGuidKey(tenant, item.Get)
        keyText (tenantCodec.EncodeKey(tenantCodec.DecodeKey(grainIdOf native))) = keyText native)

// Law 3: injective encoding in the selected Orleans key space.

[<Property>]
let ``mapped string codec is injective`` (left: NonNull<string>) (right: NonNull<string>) =
    not (significant left)
    || not (significant right)
    || left.Get = right.Get
    || keyText (roomCodec.EncodeKey(RoomId left.Get)) <> keyText (roomCodec.EncodeKey(RoomId right.Get))

[<Property>]
let ``mapped Guid codec is injective`` (left: Guid) (right: Guid) =
    left = right
    || keyText (sessionCodec.EncodeKey(SessionId left)) <> keyText (sessionCodec.EncodeKey(SessionId right))

[<Property>]
let ``mapped int64 codec is injective`` (left: int64) (right: int64) =
    left = right
    || keyText (orderCodec.EncodeKey(OrderId left)) <> keyText (orderCodec.EncodeKey(OrderId right))

[<Property>]
let ``mapped compound codec is injective``
    (leftTenant: Guid)
    (leftItem: NonWhiteSpaceString)
    (rightTenant: Guid)
    (rightItem: NonWhiteSpaceString)
    =
    // Law 3 for the compound shape. A compound key has two components, so this is where a
    // separator that the extension could also contain would show up as a collision.
    not (usableExtension leftItem)
    || not (usableExtension rightItem)
    || (leftTenant = rightTenant && leftItem.Get = rightItem.Get)
    || (let left = { tenant = leftTenant; item = leftItem.Get }
        let right = { tenant = rightTenant; item = rightItem.Get }
        keyText (tenantCodec.EncodeKey left) <> keyText (tenantCodec.EncodeKey right))

[<Fact>]
let ``a compound key extension containing the separator stays injective`` () =
    // The adversarial case a random property is unlikely to draw: the extension itself carries
    // the '+' Orleans uses to separate the two components.
    let left = { tenant = sampleGuid; item = "a+b" }
    let right = { tenant = sampleGuid; item = "a" }

    test <@ keyText (tenantCodec.EncodeKey left) <> keyText (tenantCodec.EncodeKey right) @>
    test <@ tenantCodec.DecodeKey(grainIdOf (tenantCodec.EncodeKey left)) = left @>
    test <@ tenantCodec.DecodeKey(grainIdOf (tenantCodec.EncodeKey right)) = right @>

// Law 4: rejection of malformed or non-canonical native values.

[<Fact>]
let ``mapped Guid codec rejects a malformed native key`` () =
    let error =
        throws (fun () -> sessionCodec.DecodeKey(grainIdOf (IdSpan.Create "not-a-guid")) |> ignore)

    test <@ error.Message.Contains "not a valid guidKey key" @>

[<Fact>]
let ``mapped int64 codec rejects a non-canonical native key`` () =
    let error =
        throws (fun () -> orderCodec.DecodeKey(grainIdOf (IdSpan.Create "007B")) |> ignore)

    test <@ error.Message.Contains "not the canonical Orleans representation" @>

[<Fact>]
let ``mapped compound codec rejects a native key without an extension`` () =
    let error =
        throws (fun () ->
            tenantCodec.DecodeKey(grainIdOf (GrainIdKeyExtensions.CreateGuidKey sampleGuid))
            |> ignore)

    test <@ error.Message.Contains "carries no key extension" @>

// ──────────────────────────────────────────────────────────────────────────────
// Contract-level identity for every key operation
// ──────────────────────────────────────────────────────────────────────────────

type KeyActor = private KeyActor of unit

[<NoEquality; NoComparison>]
type KeyApi = { ping: unit -> System.Threading.Tasks.Task<unit> }

[<Fact>]
let ``all five native key operations compile and encode the stock representation`` () =
    let stringContract =
        grainContract<KeyActor, string, KeyApi> {
            grainType "keys.string"
            stringKey
        }

    let guidContract =
        grainContract<KeyActor, Guid, KeyApi> {
            grainType "keys.guid"
            guidKey
        }

    let int64Contract =
        grainContract<KeyActor, int64, KeyApi> {
            grainType "keys.int64"
            int64Key
        }

    let guidCompoundContract =
        grainContract<KeyActor, Guid * string, KeyApi> {
            grainType "keys.guidCompound"
            guidCompoundKey
        }

    let int64CompoundContract =
        grainContract<KeyActor, int64 * string, KeyApi> {
            grainType "keys.int64Compound"
            int64CompoundKey
        }

    test <@ keyText (stringContract.GrainIdOf "abc").Key = "abc" @>
    test <@ keyText (guidContract.GrainIdOf sampleGuid).Key = "0123456789abcdef0123456789abcdef" @>
    test <@ keyText (int64Contract.GrainIdOf 42L).Key = "2A" @>

    test
        <@
            keyText (guidCompoundContract.GrainIdOf(sampleGuid, "ext")).Key = "0123456789abcdef0123456789abcdef+ext"
        @>

    test <@ keyText (int64CompoundContract.GrainIdOf(42L, "ext")).Key = "2A+ext" @>

[<Fact>]
let ``all five mapped key operations preserve the domain key type`` () =
    let stringContract =
        grainContract<KeyActor, RoomId, KeyApi> {
            grainType "keys.mapped.string"
            stringKeyMapped RoomId.value RoomId
        }

    let guidContract =
        grainContract<KeyActor, SessionId, KeyApi> {
            grainType "keys.mapped.guid"
            guidKeyMapped SessionId.value SessionId
        }

    let int64Contract =
        grainContract<KeyActor, OrderId, KeyApi> {
            grainType "keys.mapped.int64"
            int64KeyMapped OrderId.value OrderId
        }

    let guidCompoundContract =
        grainContract<KeyActor, TenantItem, KeyApi> {
            grainType "keys.mapped.guidCompound"
            guidCompoundKeyMapped (fun item -> item.tenant, item.item) (fun tenant item ->
                { tenant = tenant; item = item })
        }

    let int64CompoundContract =
        grainContract<KeyActor, struct (int64 * string), KeyApi> {
            grainType "keys.mapped.int64Compound"
            int64CompoundKeyMapped (fun (struct (value, ext)) -> value, ext) (fun value ext -> struct (value, ext))
        }

    test <@ stringContract.KeyOf(stringContract.GrainIdOf(RoomId "general")) = RoomId "general" @>
    test <@ guidContract.KeyOf(guidContract.GrainIdOf(SessionId sampleGuid)) = SessionId sampleGuid @>
    test <@ int64Contract.KeyOf(int64Contract.GrainIdOf(OrderId 9L)) = OrderId 9L @>

    let tenantItem = { tenant = sampleGuid; item = "widget" }
    test <@ guidCompoundContract.KeyOf(guidCompoundContract.GrainIdOf tenantItem) = tenantItem @>

    let compound = struct (7L, "zone")
    test <@ int64CompoundContract.KeyOf(int64CompoundContract.GrainIdOf compound) = compound @>

[<Fact>]
let ``equal keys under different grain types produce distinct grain identities`` () =
    let first =
        grainContract<KeyActor, string, KeyApi> {
            grainType "keys.first"
            stringKey
        }

    let second =
        grainContract<KeyActor, string, KeyApi> {
            grainType "keys.second"
            stringKey
        }

    test <@ first.GrainIdOf "same" <> second.GrainIdOf "same" @>
