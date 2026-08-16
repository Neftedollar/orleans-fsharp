namespace Orleans.FSharp

open System
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>The five native Orleans key shapes a functional contract can use.</summary>
type internal KeyKind =
    | StringKeyKind
    | GuidKeyKind
    | Int64KeyKind
    | GuidCompoundKeyKind
    | Int64CompoundKeyKind

    /// <summary>The custom-operation name which selects this native shape.</summary>
    member this.OperationName =
        match this with
        | StringKeyKind -> "stringKey"
        | GuidKeyKind -> "guidKey"
        | Int64KeyKind -> "int64Key"
        | GuidCompoundKeyKind -> "guidCompoundKey"
        | Int64CompoundKeyKind -> "int64CompoundKey"

/// <summary>
/// A key codec maps the domain key type to the canonical Orleans <c>IdSpan</c> representation
/// of its native key shape and back.
/// </summary>
[<ReferenceEquality>]
type internal KeyCodec<'Key> =
    {
        /// The native Orleans key shape this codec encodes into.
        Kind: KeyKind
        /// The custom-operation name which configured this codec.
        OperationName: string
        /// True when a domain conversion is composed with the native representation.
        IsMapped: bool
        /// Encode a domain key into the canonical Orleans key representation.
        EncodeKey: 'Key -> IdSpan
        /// Decode a domain key from a grain identity, rejecting malformed and non-canonical keys.
        DecodeKey: GrainId -> 'Key
    }

/// <summary>
/// Native and mapped Orleans key codecs. Every native codec delegates to the stock Orleans
/// key helpers, so encoded keys are byte-identical to <c>GrainIdKeyExtensions</c> output.
/// </summary>
module internal KeyCodecs =

    /// <summary>The grain type used when a decode diagnostic has to be produced without one.</summary>
    let private describeKey (grainId: GrainId) = grainId.Key.ToString()

    let private failDecode<'T> (kind: KeyKind) (grainId: GrainId) (reason: string) : 'T =
        fail
            ContractStage
            $"the key '{describeKey grainId}' of grain '{grainId.Type.ToString()}' is not a valid {kind.OperationName} key: {reason}"

    // ── native string ────────────────────────────────────────────────────────

    /// <summary>
    /// True when a string survives the UTF-8 round trip Orleans puts a string key through.
    /// </summary>
    /// <remarks>
    /// <c>IdSpan.Create</c> is <c>Encoding.UTF8.GetBytes</c>, and that encoding is not injective
    /// over arbitrary .NET strings: an unpaired surrogate has no UTF-8 representation, so the
    /// default replacement fallback turns it into U+FFFD. Two domain keys differing only in
    /// which unpaired surrogate they carry therefore encode to identical bytes and collapse onto
    /// one grain identity, and decoding either one returns neither.
    /// </remarks>
    let private roundTripsUtf8 (value: string) =
        String.Equals(
            Text.Encoding.UTF8.GetString(Text.Encoding.UTF8.GetBytes value),
            value,
            StringComparison.Ordinal
        )

    /// <summary>
    /// Orleans' own <c>GrainId.Create(GrainType, string)</c> rejects a null, empty, or
    /// white-space string key, so the string codec follows the same validation rule, and adds
    /// the well-formedness rule the specification's injectivity law needs on top of it.
    /// </summary>
    let private encodeStringKey (value: string) =
        if isNull value then
            fail ContractStage "a string grain key must not be null."

        if isBlank value then
            fail
                ContractStage
                "a string grain key must not be empty or white-space, matching Orleans' own string key validation."

        if not (roundTripsUtf8 value) then
            fail
                ContractStage
                $"the string grain key '{value}' is not well-formed UTF-8: it carries an unpaired surrogate, which Orleans' UTF-8 key encoding replaces with U+FFFD, so distinct keys would collapse onto one grain identity."

        IdSpan.Create value

    let private decodeStringKey (grainId: GrainId) =
        let value = grainId.Key.ToString()

        if isBlank value then
            fail
                ContractStage
                $"the key '{value}' of grain '{grainId.Type.ToString()}' is not a valid stringKey key: it is empty or white-space."

        // The same re-encode guard the other four native codecs carry: key bytes that are not
        // valid UTF-8 decode lossily, so the string this returns would not be the key that was
        // encoded, and encoding it again would not produce the key that arrived.
        if encodeStringKey value <> grainId.Key then
            failDecode StringKeyKind grainId "it is not the canonical Orleans representation of that string."

        value

    // ── native Guid ──────────────────────────────────────────────────────────

    let private encodeGuidKey (value: Guid) = GrainIdKeyExtensions.CreateGuidKey value

    let private decodeGuidKey (grainId: GrainId) =
        let mutable value = Guid.Empty
        let mutable extension: string = null

        if not (GrainIdKeyExtensions.TryGetGuidKey(grainId, &value, &extension)) then
            failDecode GuidKeyKind grainId "it is not a Guid key."

        if not (isNull extension) then
            failDecode GuidKeyKind grainId "it carries a key extension."

        if encodeGuidKey value <> grainId.Key then
            failDecode GuidKeyKind grainId "it is not the canonical Orleans representation of that Guid."

        value

    // ── native int64 ─────────────────────────────────────────────────────────

    let private encodeInt64Key (value: int64) = GrainIdKeyExtensions.CreateIntegerKey value

    let private decodeInt64Key (grainId: GrainId) =
        let mutable value = 0L
        let mutable extension: string = null

        if not (GrainIdKeyExtensions.TryGetIntegerKey(grainId, &value, &extension)) then
            failDecode Int64KeyKind grainId "it is not an integer key."

        if not (isNull extension) then
            failDecode Int64KeyKind grainId "it carries a key extension."

        if encodeInt64Key value <> grainId.Key then
            failDecode Int64KeyKind grainId "it is not the canonical Orleans representation of that integer."

        value

    // ── compound extensions ──────────────────────────────────────────────────

    /// <summary>
    /// Orleans drops a null, empty, or white-space key extension, which would break the
    /// compound round-trip law, so a compound key requires a significant extension.
    /// </summary>
    let private checkExtension (kind: KeyKind) (extension: string) =
        if isNull extension then
            fail ContractStage $"a {kind.OperationName} key extension must not be null."

        if isBlank extension then
            fail
                ContractStage
                $"a {kind.OperationName} key extension must not be empty or white-space, because Orleans drops such an extension."

        if containsNul extension then
            fail ContractStage $"a {kind.OperationName} key extension must not contain a NUL character."

    let private encodeGuidCompoundKey (value: Guid, extension: string) =
        checkExtension GuidCompoundKeyKind extension
        GrainIdKeyExtensions.CreateGuidKey(value, extension)

    let private decodeGuidCompoundKey (grainId: GrainId) =
        let mutable value = Guid.Empty
        let mutable extension: string = null

        if not (GrainIdKeyExtensions.TryGetGuidKey(grainId, &value, &extension)) then
            failDecode GuidCompoundKeyKind grainId "it is not a Guid key."

        if isNull extension then
            failDecode GuidCompoundKeyKind grainId "it carries no key extension."

        if encodeGuidCompoundKey (value, extension) <> grainId.Key then
            failDecode
                GuidCompoundKeyKind
                grainId
                "it is not the canonical Orleans representation of that Guid and extension."

        (value, extension)

    let private encodeInt64CompoundKey (value: int64, extension: string) =
        checkExtension Int64CompoundKeyKind extension
        GrainIdKeyExtensions.CreateIntegerKey(value, extension)

    let private decodeInt64CompoundKey (grainId: GrainId) =
        let mutable value = 0L
        let mutable extension: string = null

        if not (GrainIdKeyExtensions.TryGetIntegerKey(grainId, &value, &extension)) then
            failDecode Int64CompoundKeyKind grainId "it is not an integer key."

        if isNull extension then
            failDecode Int64CompoundKeyKind grainId "it carries no key extension."

        if encodeInt64CompoundKey (value, extension) <> grainId.Key then
            failDecode
                Int64CompoundKeyKind
                grainId
                "it is not the canonical Orleans representation of that integer and extension."

        (value, extension)

    // ── native codecs ────────────────────────────────────────────────────────

    /// <summary>The native string key codec.</summary>
    let stringKey: KeyCodec<string> =
        { Kind = StringKeyKind
          OperationName = "stringKey"
          IsMapped = false
          EncodeKey = encodeStringKey
          DecodeKey = decodeStringKey }

    /// <summary>The native Guid key codec.</summary>
    let guidKey: KeyCodec<Guid> =
        { Kind = GuidKeyKind
          OperationName = "guidKey"
          IsMapped = false
          EncodeKey = encodeGuidKey
          DecodeKey = decodeGuidKey }

    /// <summary>The native int64 key codec.</summary>
    let int64Key: KeyCodec<int64> =
        { Kind = Int64KeyKind
          OperationName = "int64Key"
          IsMapped = false
          EncodeKey = encodeInt64Key
          DecodeKey = decodeInt64Key }

    /// <summary>The native Guid compound key codec.</summary>
    let guidCompoundKey: KeyCodec<Guid * string> =
        { Kind = GuidCompoundKeyKind
          OperationName = "guidCompoundKey"
          IsMapped = false
          EncodeKey = encodeGuidCompoundKey
          DecodeKey = decodeGuidCompoundKey }

    /// <summary>The native int64 compound key codec.</summary>
    let int64CompoundKey: KeyCodec<int64 * string> =
        { Kind = Int64CompoundKeyKind
          OperationName = "int64CompoundKey"
          IsMapped = false
          EncodeKey = encodeInt64CompoundKey
          DecodeKey = decodeInt64CompoundKey }

    // ── mapped codecs ────────────────────────────────────────────────────────

    let private checkConversion (operationName: string) (name: string) (value: obj) =
        if isNull value then
            fail ContractStage $"the {name} function supplied to '{operationName}' must not be null."

    let private checkClosedKey<'Key> (operationName: string) =
        if typeof<'Key>.ContainsGenericParameters then
            fail
                ContractStage
                $"the domain key type '{typeof<'Key>.FullName}' supplied to '{operationName}' must be a closed type."

    /// <summary>Compose a domain conversion pair with a native codec.</summary>
    let private mapped<'Key, 'Native>
        (operationName: string)
        (native: KeyCodec<'Native>)
        (encode: 'Key -> 'Native)
        (decode: 'Native -> 'Key)
        : KeyCodec<'Key> =
        checkClosedKey<'Key> operationName
        checkConversion operationName "encode" (box encode)
        checkConversion operationName "decode" (box decode)

        { Kind = native.Kind
          OperationName = operationName
          IsMapped = true
          EncodeKey = encode >> native.EncodeKey
          DecodeKey = native.DecodeKey >> decode }

    /// <summary>A mapped string key codec.</summary>
    let stringKeyMapped (encode: 'Key -> string) (decode: string -> 'Key) : KeyCodec<'Key> =
        mapped "stringKeyMapped" stringKey encode decode

    /// <summary>A mapped Guid key codec.</summary>
    let guidKeyMapped (encode: 'Key -> Guid) (decode: Guid -> 'Key) : KeyCodec<'Key> =
        mapped "guidKeyMapped" guidKey encode decode

    /// <summary>A mapped int64 key codec.</summary>
    let int64KeyMapped (encode: 'Key -> int64) (decode: int64 -> 'Key) : KeyCodec<'Key> =
        mapped "int64KeyMapped" int64Key encode decode

    /// <summary>A mapped Guid compound key codec with a curried decoder.</summary>
    let guidCompoundKeyMapped (encode: 'Key -> Guid * string) (decode: Guid -> string -> 'Key) : KeyCodec<'Key> =
        mapped "guidCompoundKeyMapped" guidCompoundKey encode (fun (value, extension) -> decode value extension)

    /// <summary>A mapped int64 compound key codec with a curried decoder.</summary>
    let int64CompoundKeyMapped (encode: 'Key -> int64 * string) (decode: int64 -> string -> 'Key) : KeyCodec<'Key> =
        mapped "int64CompoundKeyMapped" int64CompoundKey encode (fun (value, extension) -> decode value extension)
