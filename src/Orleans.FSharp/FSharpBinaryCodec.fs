namespace Orleans.FSharp

open System
open System.Collections.Concurrent
open System.IO
open Microsoft.FSharp.Reflection
open TypeShape.Core
open TypeShape.Core.Core
open Orleans.Serialization
open Orleans.Serialization.Buffers
open Orleans.Serialization.Cloning
open Orleans.Serialization.Codecs
open Orleans.Serialization.Serializers
open Orleans.Serialization.WireProtocol

/// <summary>
/// Binary serialization for F# and POCO types using TypeShape for codec dispatch.
/// Builds a per-type <c>TypeCodec</c> record (Write + Read pair) on first use and
/// caches it. Supports: DU, record, option, list, map, set, array, tuple, POCO classes,
/// and all common primitive types. No [GenerateSerializer] or [Id] attributes required.
/// </summary>
[<RequireQualifiedAccess>]
module internal FSharpBinaryFormat =

    // ── Binary format description ───────────────────────────────────────────
    // Each codec owns its whole serialized representation — no outer TypeTag byte.
    //
    //   Unit        :  (nothing — 0 bytes)
    //   Bool/Byte…  :  raw value bytes
    //   String      :  [bool has_value] [utf8 string if true]
    //   Option None :  [0x00]
    //   Option Some :  [0x01] [inner value]
    //   List/Set    :  [int32 count] [elements…]
    //   Map         :  [int32 count] [key value pairs…]
    //   Array       :  [int32 count] [elements…]
    //   Tuple       :  [elements…] (count implicit from type)
    //   Record      :  [int32 field-count] [fields…]
    //   DU          :  [int32 case-tag] [int32 field-count] [fields…]
    //   POCO/Null   :  [byte: 0=null,1=present] [int32 prop-count] [props…]
    //   top-level null (serialize null typeof<_>) :  handled by String/POCO codecs
    //
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>A matched pair of write/read functions for one concrete type.</summary>
    type TypeCodec = {
        Write: BinaryWriter -> obj -> unit
        Read:  BinaryReader -> obj
    }

    // ── Cache ───────────────────────────────────────────────────────────────
    // Two-level cache for thread-safety and recursive-type support:
    //   codecRefs   – mutable ref cells, one per type, installed BEFORE building
    //                 so that self-referential types (e.g. Tree = Leaf | Branch of Tree*Tree)
    //                 find a forwarding codec instead of looping infinitely.
    //   builtCodecs – immutable map of fully-built codecs for fast lookup.

    let private codecRefs  = ConcurrentDictionary<Type, TypeCodec option ref>()
    let private builtCodecs = ConcurrentDictionary<Type, TypeCodec>()

    /// <summary>
    /// Returns the codec for <paramref name="t"/>, building it on first access.
    /// Self-referential types are handled via a forwarding ref that is populated
    /// after the real codec is built.
    /// </summary>
    let rec getCodec (t: Type) : TypeCodec =
        // Fast path: fully built
        match builtCodecs.TryGetValue(t) with
        | true, c -> c
        | _ ->
            let r = codecRefs.GetOrAdd(t, fun _ -> ref None)
            match r.Value with
            | Some c -> c  // forwarding (during recursive build) or real (concurrent thread)
            | None ->
                // Install a forwarding codec BEFORE building so any re-entrant call
                // for the same type returns this forwarding instead of looping.
                let fwd = {
                    Write = fun bw v -> r.Value.Value.Write bw v
                    Read  = fun br  -> r.Value.Value.Read br
                }
                r.Value <- Some fwd
                let shape = TypeShape.Create(t)
                let realCodec =
                    shape.Accept { new ITypeVisitor<TypeCodec> with
                        member _.Visit<'T>() = buildCodecFor<'T>() }
                // Populate the forwarding ref and add to fast cache
                r.Value <- Some realCodec
                builtCodecs.TryAdd(t, realCodec) |> ignore
                realCodec

    and private buildCodecFor<'T>() : TypeCodec =
        let shape = TypeShape.Create<'T>() :> TypeShape

        match shape with
        // ── unit ──────────────────────────────────────────────────────────
        | Shape.Unit ->
            { Write = fun _  _  -> ()
              Read  = fun _  -> () :> obj }

        // ── bool ──────────────────────────────────────────────────────────
        | Shape.Bool ->
            { Write = fun bw v -> bw.Write(v :?> bool)
              Read  = fun br  -> br.ReadBoolean() :> obj }

        // ── byte / sbyte ──────────────────────────────────────────────────
        | Shape.Byte ->
            { Write = fun bw v -> bw.Write(v :?> byte)
              Read  = fun br  -> br.ReadByte() :> obj }

        | Shape.SByte ->
            { Write = fun bw v -> bw.Write(v :?> sbyte)
              Read  = fun br  -> br.ReadSByte() :> obj }

        // ── int16 / uint16 ────────────────────────────────────────────────
        | Shape.Int16 ->
            { Write = fun bw v -> bw.Write(v :?> int16)
              Read  = fun br  -> br.ReadInt16() :> obj }

        | Shape.UInt16 ->
            { Write = fun bw v -> bw.Write(v :?> uint16)
              Read  = fun br  -> br.ReadUInt16() :> obj }

        // ── int32 / uint32 ────────────────────────────────────────────────
        | Shape.Int32 ->
            { Write = fun bw v -> bw.Write(v :?> int)
              Read  = fun br  -> br.ReadInt32() :> obj }

        | Shape.UInt32 ->
            { Write = fun bw v -> bw.Write(v :?> uint32)
              Read  = fun br  -> br.ReadUInt32() :> obj }

        // ── int64 / uint64 ────────────────────────────────────────────────
        | Shape.Int64 ->
            { Write = fun bw v -> bw.Write(v :?> int64)
              Read  = fun br  -> br.ReadInt64() :> obj }

        | Shape.UInt64 ->
            { Write = fun bw v -> bw.Write(v :?> uint64)
              Read  = fun br  -> br.ReadUInt64() :> obj }

        // ── float / float32 ───────────────────────────────────────────────
        | Shape.Double ->
            { Write = fun bw v -> bw.Write(v :?> float)
              Read  = fun br  -> br.ReadDouble() :> obj }

        | Shape.Single ->
            { Write = fun bw v -> bw.Write(v :?> float32)
              Read  = fun br  -> br.ReadSingle() :> obj }

        // ── decimal ───────────────────────────────────────────────────────
        | Shape.Decimal ->
            { Write = fun bw v -> bw.Write(v :?> decimal)
              Read  = fun br  -> br.ReadDecimal() :> obj }

        // ── char ──────────────────────────────────────────────────────────
        | Shape.Char ->
            { Write = fun bw v -> bw.Write(v :?> char)
              Read  = fun br  -> br.ReadChar() :> obj }

        // ── string (nullable) ─────────────────────────────────────────────
        | Shape.String ->
            { Write = fun bw v ->
                let s = v :?> string
                if isNull s then
                    bw.Write(false)
                else
                    bw.Write(true)
                    bw.Write(s)
              Read = fun br ->
                if br.ReadBoolean() then br.ReadString() :> obj
                else null }

        // ── Guid ──────────────────────────────────────────────────────────
        | Shape.Guid ->
            { Write = fun bw v -> bw.Write((v :?> Guid).ToByteArray())
              Read  = fun br  -> Guid(br.ReadBytes(16)) :> obj }

        // ── DateTime ──────────────────────────────────────────────────────
        | Shape.DateTime ->
            { Write = fun bw v ->
                let dt = v :?> DateTime
                bw.Write(dt.Ticks)
                bw.Write(int dt.Kind)
              Read = fun br ->
                let ticks = br.ReadInt64()
                let kind  = br.ReadInt32() |> enum<DateTimeKind>
                DateTime(ticks, kind) :> obj }

        // ── DateTimeOffset ────────────────────────────────────────────────
        | Shape.DateTimeOffset ->
            { Write = fun bw v ->
                let dto = v :?> DateTimeOffset
                bw.Write(dto.Ticks)
                bw.Write(dto.Offset.Ticks)
              Read = fun br ->
                let ticks  = br.ReadInt64()
                let offset = br.ReadInt64()
                DateTimeOffset(ticks, TimeSpan(offset)) :> obj }

        // ── TimeSpan ──────────────────────────────────────────────────────
        | Shape.TimeSpan ->
            { Write = fun bw v -> bw.Write((v :?> TimeSpan).Ticks)
              Read  = fun br  -> TimeSpan(br.ReadInt64()) :> obj }

        // ── byte array ────────────────────────────────────────────────────
        | Shape.ByteArray ->
            { Write = fun bw v ->
                let arr = v :?> byte array
                bw.Write(arr.Length)
                bw.Write(arr)
              Read = fun br ->
                let len = br.ReadInt32()
                br.ReadBytes(len) :> obj }

        // ── F# Option ─────────────────────────────────────────────────────
        | Shape.FSharpOption optShape ->
            let innerType  = optShape.Element.Type
            let innerCodec = getCodec innerType
            let cases     = FSharpType.GetUnionCases(typeof<'T>, true)
            let noneCase  = cases |> Array.find (fun c -> c.Name = "None")
            let someCase  = cases |> Array.find (fun c -> c.Name = "Some")
            { Write = fun bw value ->
                let case, fields = FSharpValue.GetUnionFields(value, typeof<'T>, true)
                if case.Name = "None" then
                    bw.Write(0uy)
                else
                    bw.Write(1uy)
                    innerCodec.Write bw fields.[0]
              Read = fun br ->
                let tag = br.ReadByte()
                if tag = 0uy then
                    FSharpValue.MakeUnion(noneCase, [||], true)
                else
                    let inner = innerCodec.Read br
                    FSharpValue.MakeUnion(someCase, [| inner |], true) }

        // ── F# list ───────────────────────────────────────────────────────
        | Shape.FSharpList lstShape ->
            let elemType  = lstShape.Element.Type
            let elemCodec = getCodec elemType
            { Write = fun bw value ->
                let items =
                    (value :?> System.Collections.IEnumerable)
                    |> Seq.cast<obj>
                    |> Array.ofSeq
                bw.Write(items.Length)
                for item in items do elemCodec.Write bw item
              Read = fun br ->
                let count   = br.ReadInt32()
                let items   = Array.init count (fun _ -> elemCodec.Read br)
                let typedArr = Array.CreateInstance(elemType, count)
                for i in 0 .. count - 1 do typedArr.SetValue(items.[i], i)
                let listModule = typeof<list<int>>.Assembly.GetType("Microsoft.FSharp.Collections.ListModule")
                let ofArray    = listModule.GetMethod("OfArray").MakeGenericMethod(elemType)
                ofArray.Invoke(null, [| typedArr |]) }

        // ── F# Map ────────────────────────────────────────────────────────
        | Shape.FSharpMap mapShape ->
            let keyType   = mapShape.Key.Type
            let valType   = mapShape.Value.Type
            let keyCodec  = getCodec keyType
            let valCodec  = getCodec valType
            let kvpType   = typedefof<Collections.Generic.KeyValuePair<_,_>>.MakeGenericType(keyType, valType)
            let keyProp   = kvpType.GetProperty("Key")
            let valProp   = kvpType.GetProperty("Value")
            let tupleType = FSharpType.MakeTupleType([| keyType; valType |])
            { Write = fun bw value ->
                let items =
                    (value :?> System.Collections.IEnumerable)
                    |> Seq.cast<obj>
                    |> Array.ofSeq
                bw.Write(items.Length)
                for kvp in items do
                    keyCodec.Write bw (keyProp.GetValue(kvp))
                    valCodec.Write bw (valProp.GetValue(kvp))
              Read = fun br ->
                let count = br.ReadInt32()
                let pairs = Array.init count (fun _ ->
                    let k = keyCodec.Read br
                    let v = valCodec.Read br
                    FSharpValue.MakeTuple([| k; v |], tupleType))
                let typedArr = Array.CreateInstance(tupleType, count)
                for i in 0 .. count - 1 do typedArr.SetValue(pairs.[i], i)
                let mapModule = typeof<Map<int,int>>.Assembly.GetType("Microsoft.FSharp.Collections.MapModule")
                let ofArray   = mapModule.GetMethod("OfArray").MakeGenericMethod(keyType, valType)
                ofArray.Invoke(null, [| typedArr |]) }

        // ── F# Set ────────────────────────────────────────────────────────
        | Shape.FSharpSet setShape ->
            let elemType  = setShape.Element.Type
            let elemCodec = getCodec elemType
            { Write = fun bw value ->
                let items =
                    (value :?> System.Collections.IEnumerable)
                    |> Seq.cast<obj>
                    |> Array.ofSeq
                bw.Write(items.Length)
                for item in items do elemCodec.Write bw item
              Read = fun br ->
                let count   = br.ReadInt32()
                let items   = Array.init count (fun _ -> elemCodec.Read br)
                let typedArr = Array.CreateInstance(elemType, count)
                for i in 0 .. count - 1 do typedArr.SetValue(items.[i], i)
                let setModule = typeof<Set<int>>.Assembly.GetType("Microsoft.FSharp.Collections.SetModule")
                let ofArray   = setModule.GetMethod("OfArray").MakeGenericMethod(elemType)
                ofArray.Invoke(null, [| typedArr |]) }

        // ── CLR array ─────────────────────────────────────────────────────
        | Shape.Array arrShape when arrShape.Rank = 1 ->
            let elemType  = arrShape.Element.Type
            let elemCodec = getCodec elemType
            { Write = fun bw value ->
                let arr = value :?> Array
                bw.Write(arr.Length)
                for i in 0 .. arr.Length - 1 do elemCodec.Write bw (arr.GetValue(i))
              Read = fun br ->
                let count = br.ReadInt32()
                let arr   = Array.CreateInstance(elemType, count)
                for i in 0 .. count - 1 do arr.SetValue(elemCodec.Read br, i)
                arr :> obj }

        // ── Tuple ─────────────────────────────────────────────────────────
        | Shape.Tuple (:? ShapeTuple<'T> as tupleShape) ->
            let elemPairs = tupleShape.Elements |> Array.map (fun elem ->
                elem.Accept { new IReadOnlyMemberVisitor<'T, ('T -> obj) * TypeCodec> with
                    member _.Visit<'F>(m: ReadOnlyMember<'T,'F>) =
                        let getter: 'T -> obj = fun v -> m.Get(v) :> obj
                        getter, getCodec typeof<'F>
                })
            { Write = fun bw value ->
                let v = value :?> 'T
                for getter, fc in elemPairs do fc.Write bw (getter v)
              Read = fun br ->
                let values = elemPairs |> Array.map (fun (_, fc) -> fc.Read br)
                FSharpValue.MakeTuple(values, typeof<'T>) }

        // ── F# Record ─────────────────────────────────────────────────────
        | Shape.FSharpRecord (:? ShapeFSharpRecord<'T> as recordShape) ->
            let fieldPairs = recordShape.Fields |> Array.map (fun field ->
                field.Accept { new IReadOnlyMemberVisitor<'T, ('T -> obj) * TypeCodec> with
                    member _.Visit<'F>(m: ReadOnlyMember<'T,'F>) =
                        let getter: 'T -> obj = fun v -> m.Get(v) :> obj
                        getter, getCodec typeof<'F>
                })
            { Write = fun bw value ->
                let v = value :?> 'T
                bw.Write(fieldPairs.Length)
                for getter, fc in fieldPairs do fc.Write bw (getter v)
              Read = fun br ->
                let count  = br.ReadInt32()
                let values = Array.init count (fun i -> snd fieldPairs.[i] |> fun fc -> fc.Read br)
                FSharpValue.MakeRecord(typeof<'T>, values, true) }

        // ── F# Discriminated Union ─────────────────────────────────────────
        | Shape.FSharpUnion (:? ShapeFSharpUnion<'T> as unionShape) ->
            let caseFieldPairs = unionShape.UnionCases |> Array.map (fun ucase ->
                ucase.Fields |> Array.map (fun field ->
                    field.Accept { new IReadOnlyMemberVisitor<'T, ('T -> obj) * TypeCodec> with
                        member _.Visit<'F>(m: ReadOnlyMember<'T,'F>) =
                            let getter: 'T -> obj = fun v -> m.Get(v) :> obj
                            getter, getCodec typeof<'F>
                    }
                )
            )
            let reflCases = FSharpType.GetUnionCases(typeof<'T>, true)
            { Write = fun bw value ->
                let v = value :?> 'T
                let case, _ = FSharpValue.GetUnionFields(v, typeof<'T>, true)
                bw.Write(case.Tag)
                let pairs = caseFieldPairs.[case.Tag]
                bw.Write(pairs.Length)
                for getter, fc in pairs do fc.Write bw (getter v)
              Read = fun br ->
                let caseTag   = br.ReadInt32()
                let unionCase = reflCases |> Array.find (fun c -> c.Tag = caseTag)
                let count     = br.ReadInt32()
                let pairs     = caseFieldPairs.[caseTag]
                let fields    = Array.init count (fun i -> snd pairs.[i] |> fun fc -> fc.Read br)
                FSharpValue.MakeUnion(unionCase, fields, true) }

        // ── POCO class (mutable properties) ───────────────────────────────
        | Shape.Poco (:? ShapePoco<'T> as pocoShape) ->
            // Properties: public readable (use for write)
            // Fields: backing fields, in same order as Properties (use for read/set)
            let propGetters = pocoShape.Properties |> Array.map (fun prop ->
                prop.Accept { new IReadOnlyMemberVisitor<'T, ('T -> obj) * TypeCodec> with
                    member _.Visit<'F>(m: ReadOnlyMember<'T,'F>) =
                        let getter: 'T -> obj = fun v -> m.Get(v) :> obj
                        getter, getCodec typeof<'F>
                })
            let fieldSetters = pocoShape.Fields |> Array.map (fun field ->
                field.Accept { new IMemberVisitor<'T, 'T -> obj -> 'T> with
                    member _.Visit<'F>(m: ShapeMember<'T,'F>) =
                        fun (target: 'T) (value: obj) -> m.Set target (value :?> 'F)
                })
            { Write = fun bw value ->
                if isNull value then
                    bw.Write(0uy)
                else
                    bw.Write(1uy)
                    let v = value :?> 'T
                    bw.Write(propGetters.Length)
                    for getter, fc in propGetters do fc.Write bw (getter v)
              Read = fun br ->
                let tag = br.ReadByte()
                if tag = 0uy then null
                else
                    let count = br.ReadInt32()
                    let mutable inst = pocoShape.CreateUninitialized()
                    for i in 0 .. count - 1 do
                        let value = snd propGetters.[i] |> fun fc -> fc.Read br
                        inst <- fieldSetters.[i] inst value
                    inst :> obj }

        // ── CliMutable (F# record with [<CLIMutable>]) ─────────────────────
        | Shape.CliMutable (:? ShapeCliMutable<'T> as cliShape) ->
            let propPairs = cliShape.Properties |> Array.map (fun prop ->
                prop.Accept { new IReadOnlyMemberVisitor<'T, ('T -> obj) * TypeCodec> with
                    member _.Visit<'F>(m: ReadOnlyMember<'T,'F>) =
                        let getter: 'T -> obj = fun v -> m.Get(v) :> obj
                        getter, getCodec typeof<'F>
                })
            let setterFns = cliShape.Properties |> Array.map (fun prop ->
                prop.Accept { new IMemberVisitor<'T, 'T -> obj -> 'T> with
                    member _.Visit<'F>(m: ShapeMember<'T,'F>) =
                        fun (target: 'T) (value: obj) -> m.Set target (value :?> 'F)
                })
            { Write = fun bw value ->
                let v = value :?> 'T
                bw.Write(propPairs.Length)
                for getter, fc in propPairs do fc.Write bw (getter v)
              Read = fun br ->
                let count = br.ReadInt32()
                let mutable inst = cliShape.CreateUninitialized()
                for i in 0 .. count - 1 do
                    let value = snd propPairs.[i] |> fun fc -> fc.Read br
                    inst <- setterFns.[i] inst value
                inst :> obj }

        | _ ->
            invalidOp $"FSharpBinaryCodec: unsupported type '{typeof<'T>.FullName}'"

    /// <summary>
    /// Returns true if the given type is a supported F# composite type or user-defined POCO class.
    /// Primitives and primitive-like system types (int, string, bool, Guid, DateTime, etc.)
    /// are handled by Orleans' built-in codecs and are excluded here.
    /// Note: TypeShape.Shape.Poco also matches some system classes like string, so we
    /// explicitly exclude those first.
    /// </summary>
    let isSupportedType (t: Type) : bool =
        if isNull t then false
        else
            let shape = TypeShape.Create(t)
            match shape with
            // Primitive-like classes that TypeShape also classifies as Poco — exclude them.
            | Shape.String
            | Shape.Guid
            | Shape.DateTime
            | Shape.DateTimeOffset
            | Shape.TimeSpan
            | Shape.ByteArray -> false
            // F# composite types
            | Shape.FSharpOption _
            | Shape.FSharpList _
            | Shape.FSharpMap _
            | Shape.FSharpSet _
            | Shape.Array _
            | Shape.Tuple _
            | Shape.FSharpRecord _
            | Shape.FSharpUnion _
            | Shape.Poco _
            | Shape.CliMutable _ -> true
            | _ -> false

    /// <summary>Serializes a value to a byte array using the F# binary format.</summary>
    let serialize (value: obj) (valueType: Type) : byte array =
        use ms = new MemoryStream()
        use bw = new BinaryWriter(ms, Text.Encoding.UTF8, true)
        let codec = getCodec valueType
        codec.Write bw value
        bw.Flush()
        ms.ToArray()

    /// <summary>Deserializes a value from a byte array using the F# binary format.</summary>
    let deserialize (data: byte array) (expectedType: Type) : obj =
        use ms = new MemoryStream(data)
        use br = new BinaryReader(ms, Text.Encoding.UTF8, true)
        let codec = getCodec expectedType
        codec.Read br

/// <summary>
/// Orleans generalized codec that serializes F# types and POCO classes in binary format
/// without requiring [GenerateSerializer] or [Id] attributes.
/// </summary>
type FSharpBinaryCodec() =

    interface IGeneralizedCodec with
        member _.IsSupportedType(``type``: Type) =
            FSharpBinaryFormat.isSupportedType ``type``

    interface IFieldCodec with
        member _.WriteField<'TBufferWriter when 'TBufferWriter :> System.Buffers.IBufferWriter<byte>>
            (writer: byref<Writer<'TBufferWriter>>, fieldIdDelta: uint32, expectedType: Type, value: obj) =
            if ReferenceCodec.TryWriteReferenceField(&writer, fieldIdDelta, expectedType, value) then
                ()
            else
                let actualType = if isNull value then expectedType else value.GetType()
                let bytes = FSharpBinaryFormat.serialize value actualType
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.LengthPrefixed)
                writer.WriteVarUInt32(uint32 bytes.Length)
                writer.Write(ReadOnlySpan<byte>(bytes))

        member _.ReadValue<'TInput>(reader: byref<Reader<'TInput>>, field: Field) : obj =
            if field.IsReference then
                ReferenceCodec.ReadReference<obj, 'TInput>(&reader, field)
            else
                let length = reader.ReadVarUInt32()
                let bytes  = reader.ReadBytes(length)
                let fieldType = field.FieldType

                if isNull fieldType then
                    invalidOp "Cannot deserialize F# binary codec value without field type information"

                FSharpBinaryFormat.deserialize bytes fieldType

    interface IGeneralizedCopier with
        member _.IsSupportedType(``type``: Type) =
            FSharpBinaryFormat.isSupportedType ``type``

    interface IDeepCopier with
        member _.DeepCopy(input: obj, _context: CopyContext) : obj =
            if isNull input then null
            else
                let t = input.GetType()
                if t.IsClass
                   && not (FSharpType.IsUnion(t, true))
                   && not (FSharpType.IsRecord(t, true))
                   && not (t.IsGenericType
                           && (t.GetGenericTypeDefinition() = typedefof<option<_>>
                               || t.GetGenericTypeDefinition() = typedefof<list<_>>)) then
                    // POCO — deep copy via round-trip serialization
                    FSharpBinaryFormat.deserialize (FSharpBinaryFormat.serialize input t) t
                else
                    input // immutable F# types — return as-is

    interface ITypeFilter with
        member _.IsTypeAllowed(``type``: Type) : Nullable<bool> =
            if FSharpBinaryFormat.isSupportedType ``type`` then
                Nullable<bool>(true)
            else
                Nullable<bool>()

/// <summary>
/// Registration helpers for FSharpBinaryCodec.
/// </summary>
[<RequireQualifiedAccess>]
module FSharpBinaryCodecRegistration =

    open Microsoft.Extensions.DependencyInjection

    /// <summary>
    /// Registers the FSharpBinaryCodec as a generalized codec, copier, and type filter
    /// with the Orleans serializer builder.
    /// </summary>
    let addToSerializerBuilder (builder: ISerializerBuilder) : ISerializerBuilder =
        builder.Services.AddSingleton<FSharpBinaryCodec>() |> ignore

        builder.Services.AddSingleton<IGeneralizedCodec>(
            Func<IServiceProvider, IGeneralizedCodec>(fun sp -> sp.GetRequiredService<FSharpBinaryCodec>()))
        |> ignore

        builder.Services.AddSingleton<IGeneralizedCopier>(
            Func<IServiceProvider, IGeneralizedCopier>(fun sp -> sp.GetRequiredService<FSharpBinaryCodec>()))
        |> ignore

        builder.Services.AddSingleton<ITypeFilter>(
            Func<IServiceProvider, ITypeFilter>(fun sp -> sp.GetRequiredService<FSharpBinaryCodec>()))
        |> ignore

        builder
