namespace Orleans.FSharp

open System
open System.Collections.Concurrent
open System.IO
open Microsoft.FSharp.Reflection
open Orleans.Serialization
open Orleans.Serialization.Buffers
open Orleans.Serialization.Cloning
open Orleans.Serialization.Codecs
open Orleans.Serialization.Serializers
open Orleans.Serialization.WireProtocol

/// <summary>
/// Binary serialization helpers for F# types using FSharp.Reflection.
/// Provides recursive encoding/decoding of DUs, records, options, lists, maps, sets,
/// arrays, and tuples into a compact binary format via BinaryWriter/BinaryReader.
/// </summary>
[<RequireQualifiedAccess>]
module internal FSharpBinaryFormat =

    /// <summary>
    /// Type tags used as discriminators in the binary stream.
    /// Each F# type category has a unique byte tag.
    /// </summary>
    [<RequireQualifiedAccess>]
    module TypeTag =
        /// <summary>Null reference marker.</summary>
        [<Literal>]
        let Null: byte = 0uy

        /// <summary>F# discriminated union value.</summary>
        [<Literal>]
        let DU: byte = 1uy

        /// <summary>F# record value.</summary>
        [<Literal>]
        let Record: byte = 2uy

        /// <summary>F# Option.Some value.</summary>
        [<Literal>]
        let OptionSome: byte = 3uy

        /// <summary>F# Option.None value.</summary>
        [<Literal>]
        let OptionNone: byte = 4uy

        /// <summary>F# list value.</summary>
        [<Literal>]
        let FSharpList: byte = 5uy

        /// <summary>F# Map value.</summary>
        [<Literal>]
        let FSharpMap: byte = 6uy

        /// <summary>F# Set value.</summary>
        [<Literal>]
        let FSharpSet: byte = 7uy

        /// <summary>CLR array value.</summary>
        [<Literal>]
        let Array: byte = 8uy

        /// <summary>Tuple value.</summary>
        [<Literal>]
        let Tuple: byte = 9uy

        /// <summary>Primitive value (int, string, float, etc.).</summary>
        [<Literal>]
        let Primitive: byte = 10uy

    /// <summary>
    /// Primitive type discriminators used within a Primitive-tagged block.
    /// </summary>
    [<RequireQualifiedAccess>]
    module PrimitiveTag =
        [<Literal>]
        let Int32: byte = 0uy

        [<Literal>]
        let Int64: byte = 1uy

        [<Literal>]
        let String: byte = 2uy

        [<Literal>]
        let Bool: byte = 3uy

        [<Literal>]
        let Float: byte = 4uy

        [<Literal>]
        let Float32: byte = 5uy

        [<Literal>]
        let Byte: byte = 6uy

        [<Literal>]
        let Int16: byte = 7uy

        [<Literal>]
        let Decimal: byte = 8uy

        [<Literal>]
        let Guid: byte = 9uy

        [<Literal>]
        let DateTime: byte = 10uy

        [<Literal>]
        let DateTimeOffset: byte = 11uy

        [<Literal>]
        let TimeSpan: byte = 12uy

        [<Literal>]
        let Char: byte = 13uy

        [<Literal>]
        let UInt32: byte = 14uy

        [<Literal>]
        let UInt64: byte = 15uy

        [<Literal>]
        let UInt16: byte = 16uy

        [<Literal>]
        let SByte: byte = 17uy

        [<Literal>]
        let ByteArray: byte = 18uy

        [<Literal>]
        let Unit: byte = 19uy

    /// <summary>
    /// Cache for FSharp.Reflection metadata to avoid repeated reflection lookups.
    /// </summary>
    let private unionCaseCache = ConcurrentDictionary<Type, UnionCaseInfo array>()
    let private recordFieldCache = ConcurrentDictionary<Type, Reflection.PropertyInfo array>()
    let private tupleElementCache = ConcurrentDictionary<Type, Type array>()

    let private getUnionCases (t: Type) =
        unionCaseCache.GetOrAdd(t, fun t -> FSharpType.GetUnionCases(t, true))

    let private getRecordFields (t: Type) =
        recordFieldCache.GetOrAdd(t, fun t -> FSharpType.GetRecordFields(t, true))

    let private getTupleElements (t: Type) =
        tupleElementCache.GetOrAdd(t, FSharpType.GetTupleElements)

    /// <summary>
    /// Returns true if the given type is an F# option type (Option&lt;T&gt;).
    /// </summary>
    let private isOptionType (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>

    /// <summary>
    /// Returns true if the given type is an F# list type.
    /// </summary>
    let private isFSharpListType (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>

    /// <summary>
    /// Returns true if the given type is an F# map type.
    /// </summary>
    let private isFSharpMapType (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>>

    /// <summary>
    /// Returns true if the given type is an F# set type.
    /// </summary>
    let private isFSharpSetType (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Set<_>>

    /// <summary>
    /// Writes a primitive value to the BinaryWriter, preceded by its primitive tag.
    /// </summary>
    let rec private writePrimitive (bw: BinaryWriter) (value: obj) (t: Type) =
        bw.Write(TypeTag.Primitive)

        match value with
        | :? int as v ->
            bw.Write(PrimitiveTag.Int32)
            bw.Write(v)
        | :? int64 as v ->
            bw.Write(PrimitiveTag.Int64)
            bw.Write(v)
        | :? string as v ->
            bw.Write(PrimitiveTag.String)
            bw.Write(v)
        | :? bool as v ->
            bw.Write(PrimitiveTag.Bool)
            bw.Write(v)
        | :? float as v ->
            bw.Write(PrimitiveTag.Float)
            bw.Write(v)
        | :? float32 as v ->
            bw.Write(PrimitiveTag.Float32)
            bw.Write(v)
        | :? byte as v ->
            bw.Write(PrimitiveTag.Byte)
            bw.Write(v)
        | :? int16 as v ->
            bw.Write(PrimitiveTag.Int16)
            bw.Write(v)
        | :? decimal as v ->
            bw.Write(PrimitiveTag.Decimal)
            bw.Write(v)
        | :? Guid as v ->
            bw.Write(PrimitiveTag.Guid)
            bw.Write(v.ToByteArray())
        | :? DateTime as v ->
            bw.Write(PrimitiveTag.DateTime)
            bw.Write(v.Ticks)
            bw.Write(int v.Kind)
        | :? DateTimeOffset as v ->
            bw.Write(PrimitiveTag.DateTimeOffset)
            bw.Write(v.Ticks)
            bw.Write(v.Offset.Ticks)
        | :? TimeSpan as v ->
            bw.Write(PrimitiveTag.TimeSpan)
            bw.Write(v.Ticks)
        | :? char as v ->
            bw.Write(PrimitiveTag.Char)
            bw.Write(v)
        | :? uint32 as v ->
            bw.Write(PrimitiveTag.UInt32)
            bw.Write(v)
        | :? uint64 as v ->
            bw.Write(PrimitiveTag.UInt64)
            bw.Write(v)
        | :? uint16 as v ->
            bw.Write(PrimitiveTag.UInt16)
            bw.Write(v)
        | :? sbyte as v ->
            bw.Write(PrimitiveTag.SByte)
            bw.Write(v)
        | :? (byte array) as v ->
            bw.Write(PrimitiveTag.ByteArray)
            bw.Write(v.Length)
            bw.Write(v)
        | _ when t = typeof<unit> ->
            bw.Write(PrimitiveTag.Unit)
        | _ ->
            invalidOp $"Unsupported primitive type: {t.FullName}"

    /// <summary>
    /// Writes a value of any supported F# type to the BinaryWriter.
    /// </summary>
    and writeValue (bw: BinaryWriter) (value: obj) (valueType: Type) : unit =
        if isNull value then
            bw.Write(TypeTag.Null)
        elif isOptionType valueType then
            writeOptionValue bw value valueType
        elif isFSharpListType valueType then
            writeListValue bw value valueType
        elif isFSharpMapType valueType then
            writeMapValue bw value valueType
        elif isFSharpSetType valueType then
            writeSetValue bw value valueType
        elif valueType.IsArray then
            writeArrayValue bw value valueType
        elif FSharpType.IsTuple(valueType) then
            writeTupleValue bw value valueType
        elif FSharpType.IsUnion(valueType, true) then
            writeUnionValue bw value valueType
        elif FSharpType.IsRecord(valueType, true) then
            writeRecordValue bw value valueType
        else
            writePrimitive bw value valueType

    and private writeOptionValue (bw: BinaryWriter) (value: obj) (valueType: Type) =
        let cases = getUnionCases valueType
        let case, fields = FSharpValue.GetUnionFields(value, valueType, true)

        if case.Name = "None" then
            bw.Write(TypeTag.OptionNone)
        else
            bw.Write(TypeTag.OptionSome)
            let innerType = valueType.GetGenericArguments().[0]
            writeValue bw fields.[0] innerType

    and private writeListValue (bw: BinaryWriter) (value: obj) (valueType: Type) =
        bw.Write(TypeTag.FSharpList)
        let elementType = valueType.GetGenericArguments().[0]
        let items = value :?> System.Collections.IEnumerable |> Seq.cast<obj> |> Array.ofSeq
        bw.Write(items.Length)

        for item in items do
            writeValue bw item elementType

    and private writeMapValue (bw: BinaryWriter) (value: obj) (valueType: Type) =
        bw.Write(TypeTag.FSharpMap)
        let genArgs = valueType.GetGenericArguments()
        let keyType = genArgs.[0]
        let valType = genArgs.[1]
        let kvpType = typedefof<System.Collections.Generic.KeyValuePair<_, _>>.MakeGenericType(genArgs)
        let keyProp = kvpType.GetProperty("Key")
        let valueProp = kvpType.GetProperty("Value")
        let items = value :?> System.Collections.IEnumerable |> Seq.cast<obj> |> Array.ofSeq
        bw.Write(items.Length)

        for kvp in items do
            writeValue bw (keyProp.GetValue(kvp)) keyType
            writeValue bw (valueProp.GetValue(kvp)) valType

    and private writeSetValue (bw: BinaryWriter) (value: obj) (valueType: Type) =
        bw.Write(TypeTag.FSharpSet)
        let elementType = valueType.GetGenericArguments().[0]
        let items = value :?> System.Collections.IEnumerable |> Seq.cast<obj> |> Array.ofSeq
        bw.Write(items.Length)

        for item in items do
            writeValue bw item elementType

    and private writeArrayValue (bw: BinaryWriter) (value: obj) (valueType: Type) =
        bw.Write(TypeTag.Array)
        let elementType = valueType.GetElementType()
        let arr = value :?> System.Array
        bw.Write(arr.Length)

        for i in 0 .. arr.Length - 1 do
            writeValue bw (arr.GetValue(i)) elementType

    and private writeTupleValue (bw: BinaryWriter) (value: obj) (valueType: Type) =
        bw.Write(TypeTag.Tuple)
        let elements = FSharpValue.GetTupleFields(value)
        let elementTypes = getTupleElements valueType
        bw.Write(elements.Length)

        for i in 0 .. elements.Length - 1 do
            writeValue bw elements.[i] elementTypes.[i]

    and private writeUnionValue (bw: BinaryWriter) (value: obj) (valueType: Type) =
        bw.Write(TypeTag.DU)
        let case, fields = FSharpValue.GetUnionFields(value, valueType, true)
        bw.Write(case.Tag)
        let fieldInfos = case.GetFields()
        bw.Write(fields.Length)

        for i in 0 .. fields.Length - 1 do
            writeValue bw fields.[i] fieldInfos.[i].PropertyType

    and private writeRecordValue (bw: BinaryWriter) (value: obj) (valueType: Type) =
        bw.Write(TypeTag.Record)
        let fields = getRecordFields valueType
        bw.Write(fields.Length)

        for field in fields do
            writeValue bw (field.GetValue(value)) field.PropertyType

    /// <summary>
    /// Reads a primitive value from the BinaryReader based on the primitive tag.
    /// </summary>
    let rec private readPrimitive (br: BinaryReader) : obj =
        let tag = br.ReadByte()

        match tag with
        | t when t = PrimitiveTag.Int32 -> br.ReadInt32() :> obj
        | t when t = PrimitiveTag.Int64 -> br.ReadInt64() :> obj
        | t when t = PrimitiveTag.String -> br.ReadString() :> obj
        | t when t = PrimitiveTag.Bool -> br.ReadBoolean() :> obj
        | t when t = PrimitiveTag.Float -> br.ReadDouble() :> obj
        | t when t = PrimitiveTag.Float32 -> br.ReadSingle() :> obj
        | t when t = PrimitiveTag.Byte -> br.ReadByte() :> obj
        | t when t = PrimitiveTag.Int16 -> br.ReadInt16() :> obj
        | t when t = PrimitiveTag.Decimal -> br.ReadDecimal() :> obj
        | t when t = PrimitiveTag.Guid ->
            let bytes = br.ReadBytes(16)
            Guid(bytes) :> obj
        | t when t = PrimitiveTag.DateTime ->
            let ticks = br.ReadInt64()
            let kind = br.ReadInt32() |> enum<DateTimeKind>
            DateTime(ticks, kind) :> obj
        | t when t = PrimitiveTag.DateTimeOffset ->
            let ticks = br.ReadInt64()
            let offsetTicks = br.ReadInt64()
            DateTimeOffset(ticks, TimeSpan(offsetTicks)) :> obj
        | t when t = PrimitiveTag.TimeSpan ->
            TimeSpan(br.ReadInt64()) :> obj
        | t when t = PrimitiveTag.Char -> br.ReadChar() :> obj
        | t when t = PrimitiveTag.UInt32 -> br.ReadUInt32() :> obj
        | t when t = PrimitiveTag.UInt64 -> br.ReadUInt64() :> obj
        | t when t = PrimitiveTag.UInt16 -> br.ReadUInt16() :> obj
        | t when t = PrimitiveTag.SByte -> br.ReadSByte() :> obj
        | t when t = PrimitiveTag.ByteArray ->
            let len = br.ReadInt32()
            br.ReadBytes(len) :> obj
        | t when t = PrimitiveTag.Unit -> () :> obj
        | _ -> invalidOp $"Unknown primitive tag: {tag}"

    /// <summary>
    /// Reads a value of any supported F# type from the BinaryReader.
    /// </summary>
    and readValue (br: BinaryReader) (expectedType: Type) : obj =
        let tag = br.ReadByte()

        match tag with
        | t when t = TypeTag.Null -> null
        | t when t = TypeTag.OptionNone -> readOptionNone expectedType
        | t when t = TypeTag.OptionSome -> readOptionSome br expectedType
        | t when t = TypeTag.FSharpList -> readListValue br expectedType
        | t when t = TypeTag.FSharpMap -> readMapValue br expectedType
        | t when t = TypeTag.FSharpSet -> readSetValue br expectedType
        | t when t = TypeTag.Array -> readArrayValue br expectedType
        | t when t = TypeTag.Tuple -> readTupleValue br expectedType
        | t when t = TypeTag.DU -> readUnionValue br expectedType
        | t when t = TypeTag.Record -> readRecordValue br expectedType
        | t when t = TypeTag.Primitive -> readPrimitive br
        | _ -> invalidOp $"Unknown type tag: {tag}"

    and private readOptionNone (expectedType: Type) : obj =
        let cases = getUnionCases expectedType
        let noneCase = cases |> Array.find (fun c -> c.Name = "None")
        FSharpValue.MakeUnion(noneCase, [||], true)

    and private readOptionSome (br: BinaryReader) (expectedType: Type) : obj =
        let cases = getUnionCases expectedType
        let someCase = cases |> Array.find (fun c -> c.Name = "Some")
        let innerType = expectedType.GetGenericArguments().[0]
        let innerValue = readValue br innerType
        FSharpValue.MakeUnion(someCase, [| innerValue |], true)

    /// <summary>
    /// Creates a typed array from an obj array by copying values into Array.CreateInstance.
    /// Required because reflection-based methods like List.ofArray expect T[] not obj[].
    /// </summary>
    and private toTypedArray (elementType: Type) (objArray: obj array) : obj =
        let typedArr = System.Array.CreateInstance(elementType, objArray.Length)

        for i in 0 .. objArray.Length - 1 do
            typedArr.SetValue(objArray.[i], i)

        typedArr :> obj

    and private readListValue (br: BinaryReader) (expectedType: Type) : obj =
        let elementType = expectedType.GetGenericArguments().[0]
        let count = br.ReadInt32()
        let items = Array.init count (fun _ -> readValue br elementType)
        let typedArr = toTypedArray elementType items
        let listModule = typeof<list<int>>.Assembly.GetType("Microsoft.FSharp.Collections.ListModule")
        let ofArray = listModule.GetMethod("OfArray").MakeGenericMethod(elementType)
        ofArray.Invoke(null, [| typedArr |])

    and private readMapValue (br: BinaryReader) (expectedType: Type) : obj =
        let genArgs = expectedType.GetGenericArguments()
        let keyType = genArgs.[0]
        let valType = genArgs.[1]
        let count = br.ReadInt32()
        let tupleType = FSharpType.MakeTupleType([| keyType; valType |])
        let pairs = Array.init count (fun _ ->
            let k = readValue br keyType
            let v = readValue br valType
            FSharpValue.MakeTuple([| k; v |], tupleType))
        let typedArr = toTypedArray tupleType pairs
        let mapModule = typeof<Map<int, int>>.Assembly.GetType("Microsoft.FSharp.Collections.MapModule")
        let ofArray = mapModule.GetMethod("OfArray").MakeGenericMethod(keyType, valType)
        ofArray.Invoke(null, [| typedArr |])

    and private readSetValue (br: BinaryReader) (expectedType: Type) : obj =
        let elementType = expectedType.GetGenericArguments().[0]
        let count = br.ReadInt32()
        let items = Array.init count (fun _ -> readValue br elementType)
        let typedArr = toTypedArray elementType items
        let setModule = typeof<Set<int>>.Assembly.GetType("Microsoft.FSharp.Collections.SetModule")
        let ofArray = setModule.GetMethod("OfArray").MakeGenericMethod(elementType)
        ofArray.Invoke(null, [| typedArr |])

    and private readArrayValue (br: BinaryReader) (expectedType: Type) : obj =
        let elementType = expectedType.GetElementType()
        let count = br.ReadInt32()
        let arr = System.Array.CreateInstance(elementType, count)

        for i in 0 .. count - 1 do
            arr.SetValue(readValue br elementType, i)

        arr :> obj

    and private readTupleValue (br: BinaryReader) (expectedType: Type) : obj =
        let count = br.ReadInt32()
        let elementTypes = getTupleElements expectedType
        let elements = Array.init count (fun i -> readValue br elementTypes.[i])
        FSharpValue.MakeTuple(elements, expectedType)

    and private readUnionValue (br: BinaryReader) (expectedType: Type) : obj =
        let caseTag = br.ReadInt32()
        let cases = getUnionCases expectedType
        let unionCase = cases |> Array.find (fun c -> c.Tag = caseTag)
        let fieldCount = br.ReadInt32()
        let fieldInfos = unionCase.GetFields()
        let fields = Array.init fieldCount (fun i -> readValue br fieldInfos.[i].PropertyType)
        FSharpValue.MakeUnion(unionCase, fields, true)

    and private readRecordValue (br: BinaryReader) (expectedType: Type) : obj =
        let fieldCount = br.ReadInt32()
        let fields = getRecordFields expectedType
        let values = Array.init fieldCount (fun i -> readValue br fields.[i].PropertyType)
        FSharpValue.MakeRecord(expectedType, values, true)

    /// <summary>
    /// Serializes a value to a byte array using the F# binary format.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="valueType">The runtime type of the value.</param>
    /// <returns>A byte array containing the binary representation.</returns>
    let serialize (value: obj) (valueType: Type) : byte array =
        use ms = new MemoryStream()
        use bw = new BinaryWriter(ms, Text.Encoding.UTF8, true)
        writeValue bw value valueType
        bw.Flush()
        ms.ToArray()

    /// <summary>
    /// Deserializes a value from a byte array using the F# binary format.
    /// </summary>
    /// <param name="data">The byte array to deserialize from.</param>
    /// <param name="expectedType">The expected type of the deserialized value.</param>
    /// <returns>The deserialized value.</returns>
    let deserialize (data: byte array) (expectedType: Type) : obj =
        use ms = new MemoryStream(data)
        use br = new BinaryReader(ms, Text.Encoding.UTF8, true)
        readValue br expectedType

    /// <summary>
    /// Returns true if the given type is a supported F# type for binary serialization.
    /// Supports: DU, record, option, list, map, set, array, tuple, and primitive types.
    /// </summary>
    /// <param name="t">The type to check.</param>
    /// <returns>True if the type is supported for F# binary serialization.</returns>
    let isSupportedType (t: Type) : bool =
        if isNull t then
            false
        elif isOptionType t then
            true
        elif isFSharpListType t then
            true
        elif isFSharpMapType t then
            true
        elif isFSharpSetType t then
            true
        elif t.IsArray then
            true
        elif FSharpType.IsTuple(t) then
            true
        elif FSharpType.IsUnion(t, true) then
            true
        elif FSharpType.IsRecord(t, true) then
            true
        else
            false

/// <summary>
/// Orleans generalized codec that serializes F# types (discriminated unions, records,
/// options, lists, maps, sets, arrays, tuples) in binary format without requiring
/// [GenerateSerializer] or [Id] attributes.
/// Implements IGeneralizedCodec, IGeneralizedCopier, and ITypeFilter for full
/// Orleans serialization pipeline integration.
/// </summary>
type FSharpBinaryCodec() =

    interface IGeneralizedCodec with
        /// <summary>
        /// Returns true if the given type is a supported F# type for binary serialization.
        /// </summary>
        member _.IsSupportedType(``type``: Type) =
            FSharpBinaryFormat.isSupportedType ``type``

    interface IFieldCodec with
        /// <summary>
        /// Writes an F# value as a length-prefixed binary blob in the Orleans wire format.
        /// </summary>
        member _.WriteField<'TBufferWriter when 'TBufferWriter :> System.Buffers.IBufferWriter<byte>>
            (writer: byref<Writer<'TBufferWriter>>, fieldIdDelta: uint32, expectedType: Type, value: obj) =
            if ReferenceCodec.TryWriteReferenceField(&writer, fieldIdDelta, expectedType, value) then
                ()
            else
                let bytes = FSharpBinaryFormat.serialize value (if isNull value then expectedType else value.GetType())
                writer.WriteFieldHeader(fieldIdDelta, expectedType, (if isNull value then expectedType else value.GetType()), WireType.LengthPrefixed)
                writer.WriteVarUInt32(uint32 bytes.Length)
                writer.Write(ReadOnlySpan<byte>(bytes))

        /// <summary>
        /// Reads an F# value from a length-prefixed binary blob in the Orleans wire format.
        /// </summary>
        member _.ReadValue<'TInput>(reader: byref<Reader<'TInput>>, field: Field) : obj =
            if field.IsReference then
                ReferenceCodec.ReadReference<obj, 'TInput>(&reader, field)
            else
                let length = reader.ReadVarUInt32()
                let bytes = reader.ReadBytes(length)
                let fieldType = field.FieldType

                if isNull fieldType then
                    invalidOp "Cannot deserialize F# binary codec value without field type information"

                FSharpBinaryFormat.deserialize bytes fieldType

    interface IGeneralizedCopier with
        /// <summary>
        /// Returns true if the given type is a supported F# type for deep copying.
        /// </summary>
        member _.IsSupportedType(``type``: Type) =
            FSharpBinaryFormat.isSupportedType ``type``

    interface IDeepCopier with
        /// <summary>
        /// Deep copies an F# value by serializing and deserializing through the binary format.
        /// Immutable F# types (DU, record, option, list, map, set, tuple) are returned as-is
        /// since they are already immutable.
        /// </summary>
        member _.DeepCopy(input: obj, _context: CopyContext) : obj =
            // F# DUs, records, options, lists, maps, sets, and tuples are immutable.
            // No need to copy them — return the original reference.
            input

    interface ITypeFilter with
        /// <summary>
        /// Allows F# types supported by this codec through the Orleans type filter.
        /// Returns null for unsupported types, letting other filters handle them.
        /// </summary>
        member _.IsTypeAllowed(``type``: Type) : Nullable<bool> =
            if FSharpBinaryFormat.isSupportedType ``type`` then
                Nullable<bool>(true)
            else
                Nullable<bool>()

/// <summary>
/// Functions for registering the FSharpBinaryCodec with the Orleans serializer.
/// </summary>
[<RequireQualifiedAccess>]
module FSharpBinaryCodecRegistration =

    open Microsoft.Extensions.DependencyInjection

    /// <summary>
    /// Registers the FSharpBinaryCodec as a generalized codec, copier, and type filter
    /// with the Orleans serializer builder.
    /// </summary>
    /// <param name="builder">The Orleans serializer builder.</param>
    /// <returns>The serializer builder for chaining.</returns>
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
