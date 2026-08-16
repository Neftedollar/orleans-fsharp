namespace Orleans.FSharp

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
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
    type internal TypeCodec = {
        Write: BinaryWriter -> obj -> unit
        Read:  BinaryReader -> obj
    }

    /// <summary>What a <see cref="CodecCell"/> hands back to a caller asking for its codec.</summary>
    type private CodecClaim =
        /// Use this codec: either the finished one, or a forwarder onto an in-flight build.
        | UseCodec of TypeCodec
        /// The caller now owns the build for this type and must run it.
        | BuildIt

    /// <summary>
    /// The build state of one type's codec.
    /// </summary>
    /// <remarks>
    /// A forwarding codec is published before the real codec exists so a self-referential type
    /// (e.g. <c>Tree = Leaf | Branch of Tree * Tree</c>) and any thread racing the same first use
    /// have something to hold. Invoking a forwarder whose build has not finished yet WAITS for the
    /// builder; the earlier design dereferenced the still-forwarding cell and self-recursed until
    /// the stack overflowed, which is unrecoverable. Only one thread ever builds a given type;
    /// waiting happens at invocation time rather than at build time, so two threads building
    /// mutually recursive types cannot deadlock on each other.
    /// </remarks>
    type private CodecCell() =
        /// The finished codec. Read off the gate on the hot path, hence volatile.
        [<VolatileField>]
        let mutable real: TypeCodec option = None
        /// The forwarder handed to re-entrant/racing callers; Some exactly while a build is in flight.
        let mutable forwarder: TypeCodec option = None
        /// Managed thread id of the thread building this type, 0 when no build is in flight.
        let mutable builder = 0
        let gate = obj ()

        /// Upper bound on waiting for another thread's build, so a lost build can never hang a process.
        static let buildTimeout = TimeSpan.FromSeconds 30.0

        /// <summary>Claims the build for this thread, or returns the codec to use.</summary>
        member this.Claim(t: Type) : CodecClaim =
            lock gate (fun () ->
                match real with
                | Some c -> UseCodec c
                | None ->
                    match forwarder with
                    | Some fwd when builder <> 0 -> UseCodec fwd
                    | _ ->
                        builder <- Environment.CurrentManagedThreadId

                        forwarder <-
                            Some
                                { Write = fun bw v -> (this.Resolve t).Write bw v
                                  Read = fun br -> (this.Resolve t).Read br }

                        BuildIt)

        /// <summary>Publishes a finished build and wakes every waiter.</summary>
        member _.Publish(codec: TypeCodec) =
            lock gate (fun () ->
                real <- Some codec
                builder <- 0
                forwarder <- None
                Monitor.PulseAll gate)

        /// <summary>
        /// Releases a failed build so a later caller retries it, instead of inheriting a
        /// forwarder that can never resolve.
        /// </summary>
        member _.Abandon() =
            lock gate (fun () ->
                builder <- 0
                forwarder <- None
                Monitor.PulseAll gate)

        /// <summary>Resolves a forwarder at invocation time, waiting out an in-flight build.</summary>
        member private _.Resolve(t: Type) : TypeCodec =
            match real with
            | Some c -> c
            | None ->
                lock gate (fun () ->
                    if builder = Environment.CurrentManagedThreadId then
                        // A codec invoked during its own build would wait on itself forever.
                        invalidOp
                            $"FSharpBinaryCodec: the codec for '{t.FullName}' was invoked while it was still being built on this thread."

                    let deadline = DateTime.UtcNow + buildTimeout

                    while real.IsNone && builder <> 0 do
                        let remaining = deadline - DateTime.UtcNow

                        if remaining <= TimeSpan.Zero || not (Monitor.Wait(gate, remaining)) then
                            invalidOp
                                $"FSharpBinaryCodec: timed out waiting for the codec for '{t.FullName}' to be built on another thread."

                    match real with
                    | Some c -> c
                    | None ->
                        invalidOp
                            $"FSharpBinaryCodec: the codec for '{t.FullName}' failed to build; the value cannot be serialized.")

    // ── Cache ───────────────────────────────────────────────────────────────
    // Two-level cache for thread-safety and recursive-type support:
    //   codecCells  – one build cell per type, claimed by a single builder thread; every other
    //                 caller (re-entrant or concurrent) gets a forwarder that resolves once the
    //                 build finishes.
    //   builtCodecs – lock-free map of fully-built codecs for fast lookup.

    let private codecCells = ConcurrentDictionary<Type, CodecCell>()
    let private builtCodecs = ConcurrentDictionary<Type, TypeCodec>()

    /// <summary>
    /// Returns true if the given type is an F# DU case type (a nested class whose
    /// declaring type is an F# union). This handles cases like BankAccountCommand+Deposit
    /// where Orleans sees the concrete runtime type rather than the parent union.
    /// </summary>
    let isUnionCaseType (t: Type) : bool =
        t.IsNested && FSharpType.IsUnion(t.DeclaringType, true)

    /// <summary>
    /// Returns the codec for <paramref name="t"/>, building it on first access.
    /// Self-referential types are handled via a forwarding ref that is populated
    /// after the real codec is built.
    /// For F# DU case types (e.g. BankAccountCommand+Deposit), delegates to the
    /// parent union's codec since the case type is a subtype of the union.
    /// </summary>
    let rec getCodec (t: Type) : TypeCodec =
        // Fast path: fully built
        match builtCodecs.TryGetValue(t) with
        | true, c -> c
        | _ ->
            // DU case types: delegate to the parent union's codec
            if isUnionCaseType t then
                let parentCodec = getCodec t.DeclaringType
                // Wrap so the Write accepts the case type (it's already a union value)
                // and Read returns the parent union type (compatible with the case type)
                { Write = parentCodec.Write
                  Read  = parentCodec.Read }
            else
                let cell = codecCells.GetOrAdd(t, fun _ -> CodecCell())
                match cell.Claim t with
                | UseCodec c -> c // real, or a forwarder onto the build in flight
                | BuildIt ->
                    let realCodec =
                        try
                            let shape = TypeShape.Create(t)

                            shape.Accept
                                { new ITypeVisitor<TypeCodec> with
                                    member _.Visit<'T>() = buildCodecFor<'T>() }
                        with _ ->
                            // Release the claim so an unsupported type throws the same
                            // diagnostic on every call instead of poisoning the cell.
                            cell.Abandon()
                            reraise ()

                    cell.Publish realCodec
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
            // Check for DU case types first (nested class of an F# union)
            if isUnionCaseType t then true
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
    /// Types an upper layer has declared as a top-level payload type, keyed by
    /// <c>Type.FullName</c>.
    /// </summary>
    /// <remarks>
    /// Orleans elides the field-type header when the actual type equals the expected type,
    /// which is exactly what exact-type payload serialization produces. In that case
    /// <c>ReadValue</c> receives no field type and has to resolve the embedded FullName
    /// itself, and <c>Type.GetType</c> can only see this assembly and the framework — never an
    /// application assembly. Registering the declared types keeps that resolution working
    /// without widening the assembly allow-list to arbitrary loaded types.
    /// </remarks>
    let private declaredTypes = ConcurrentDictionary<string, Type>(StringComparer.Ordinal)

    /// <summary>
    /// Declare one closed type as a top-level payload type so an elided field type can be
    /// resolved by name. Idempotent.
    /// </summary>
    /// <remarks>
    /// A second declaration of the same <c>FullName</c> with a DIFFERENT CLR type is rejected
    /// rather than silently overwritten: the table is the authority for resolving an elided
    /// top-level payload type, so last-writer-wins would make deserialization pick a type by
    /// registration order and hand the wrong object to a handler. Declaration happens during
    /// binding preflight and silo startup, never on the hot path, so failing here converts a
    /// silent cross-assembly type confusion into a configuration diagnostic.
    /// </remarks>
    let internal declareType (declared: Type) =
        if
            not (isNull declared)
            && not declared.ContainsGenericParameters
            && not (isNull declared.FullName)
        then
            let existing =
                declaredTypes.GetOrAdd(declared.FullName, declared)

            if not (obj.ReferenceEquals(existing, declared)) then
                invalidOp
                    $"FSharpBinaryCodec: the type name '{declared.FullName}' is already declared as a top-level payload type by '{existing.AssemblyQualifiedName}' and cannot be redeclared by '{declared.AssemblyQualifiedName}'. Two distinct types sharing one FullName cannot both be resolved when Orleans elides the field type."

    /// <summary>
    /// Serializes a value to a codec-level byte array that embeds the type's FullName.
    /// Used by WriteField/ReadValue so deserialization can recover the type even when
    /// Orleans omits the field-type header (SchemaType.Expected optimization).
    /// Format: [length-prefixed UTF8 FullName][raw value bytes from serialize].
    /// </summary>
    let serializeWithType (value: obj) (valueType: Type) : byte array =
        use ms = new MemoryStream()
        use bw = new BinaryWriter(ms, Text.Encoding.UTF8, true)
        bw.Write(valueType.FullName)
        let valueBytes = serialize value valueType
        bw.Write(int32 valueBytes.Length)
        bw.Write(valueBytes)
        bw.Flush()
        ms.ToArray()

    /// <summary>
    /// Deserializes a value from a codec-level byte array produced by <see cref="serializeWithType"/>.
    /// If <paramref name="hintType"/> is non-null it is used directly; otherwise the type name
    /// embedded in the bytes is resolved via <see cref="Type.GetType"/> with an assembly
    /// allow-list for defense-in-depth.
    /// </summary>
    let deserializeWithType (data: byte array) (hintType: Type) : obj =
        use ms = new MemoryStream(data)
        use br = new BinaryReader(ms, Text.Encoding.UTF8, true)
        let typeName = br.ReadString()
        let valueLen  = br.ReadInt32()
        let valueBytes = br.ReadBytes(valueLen)

        // Known-safe assembly name prefixes for type resolution.
        // Only types from these assemblies are resolved when hintType is null.
        let allowedAssemblyPrefixes =
            [| "Orleans.FSharp"
               "System"
               "Microsoft.FSharp"
               "FSharp.Core"
               "mscorlib"
               "netstandard"
               "TypeShape" |]

        let isAssemblyAllowed (asmName: string) : bool =
            asmName <> null &&
            allowedAssemblyPrefixes |> Array.exists (fun prefix -> asmName.StartsWith prefix)

        let actualType =
            if isNull hintType then
                match declaredTypes.TryGetValue typeName with
                | true, declared -> declared
                | _ ->

                match Type.GetType(typeName, throwOnError = false) with
                | null ->
                    invalidOp $"FSharpBinaryCodec: type '{typeName}' not found. Ensure the type is in a loaded assembly."
                | t ->
                    let asmName = t.Assembly.GetName().Name
                    if isAssemblyAllowed asmName then t
                    else
                        invalidOp $"FSharpBinaryCodec: type '{typeName}' is from assembly '{asmName}' which is not in the trusted allow-list. Provide an explicit hintType to deserialize safely."
            else
                hintType
        deserialize valueBytes actualType

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
                // serializeWithType embeds the FullName so ReadValue can recover the
                // type when Orleans elides the field-type header (SchemaType.Expected).
                let bytes = FSharpBinaryFormat.serializeWithType value actualType
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.LengthPrefixed)
                writer.WriteVarUInt32(uint32 bytes.Length)
                writer.Write(ReadOnlySpan<byte>(bytes))

        member _.ReadValue<'TInput>(reader: byref<Reader<'TInput>>, field: Field) : obj =
            if field.IsReference then
                ReferenceCodec.ReadReference<obj, 'TInput>(&reader, field)
            else
                let length = reader.ReadVarUInt32()
                let bytes  = reader.ReadBytes(length)
                // field.FieldType is null when Orleans uses SchemaType.Expected (no type
                // bytes in the header); deserializeWithType reads the type from our prefix.
                FSharpBinaryFormat.deserializeWithType bytes field.FieldType

    interface IGeneralizedCopier with
        member _.IsSupportedType(``type``: Type) =
            FSharpBinaryFormat.isSupportedType ``type``

    interface IDeepCopier with
        member _.DeepCopy(input: obj, _context: CopyContext) : obj =
            if isNull input then null
            else
                let t = input.GetType()
                // F# unions, records, options, lists, maps, and DU case types are all
                // structurally immutable — return as-is without cloning.
                if t.IsClass
                   && not (FSharpType.IsUnion(t, true))
                   && not (FSharpType.IsRecord(t, true))
                   && not (FSharpBinaryFormat.isUnionCaseType t)
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

/// <summary>Presence marker: the F# generalized codec and its type filter are registered.</summary>
[<Sealed>]
type internal FSharpBinaryCodecMarker() =
    class
    end

/// <summary>Presence marker: the F# generalized copier is registered.</summary>
[<Sealed>]
type internal FSharpBinaryCopierMarker() =
    class
    end

/// <summary>
/// Registration helpers for FSharpBinaryCodec.
/// </summary>
[<RequireQualifiedAccess>]
module FSharpBinaryCodecRegistration =

    open Microsoft.Extensions.DependencyInjection

    /// <summary>
    /// The codec, its singleton, and the type filter — registered at most once per service
    /// collection, whichever entry point asks for them first.
    /// </summary>
    let private ensureCodec (services: IServiceCollection) =
        let registered =
            services
            |> Seq.exists (fun descriptor -> descriptor.ServiceType = typeof<FSharpBinaryCodecMarker>)

        if not registered then
            services.AddSingleton<FSharpBinaryCodecMarker>() |> ignore
            services.AddSingleton<FSharpBinaryCodec>() |> ignore

            services.AddSingleton<IGeneralizedCodec>(
                Func<IServiceProvider, IGeneralizedCodec>(fun sp -> sp.GetRequiredService<FSharpBinaryCodec>()))
            |> ignore

            services.AddSingleton<ITypeFilter>(
                Func<IServiceProvider, ITypeFilter>(fun sp -> sp.GetRequiredService<FSharpBinaryCodec>()))
            |> ignore

    /// <summary>The generalized copier — registered at most once per service collection.</summary>
    let private ensureCopier (services: IServiceCollection) =
        let registered =
            services
            |> Seq.exists (fun descriptor -> descriptor.ServiceType = typeof<FSharpBinaryCopierMarker>)

        if not registered then
            services.AddSingleton<FSharpBinaryCopierMarker>() |> ignore

            services.AddSingleton<IGeneralizedCopier>(
                Func<IServiceProvider, IGeneralizedCopier>(fun sp -> sp.GetRequiredService<FSharpBinaryCodec>()))
            |> ignore

    /// <summary>
    /// Registers the FSharpBinaryCodec as a generalized codec, copier, and type filter
    /// with the Orleans serializer builder.
    /// </summary>
    let addToSerializerBuilder (builder: ISerializerBuilder) : ISerializerBuilder =
        ensureCodec builder.Services
        ensureCopier builder.Services
        builder

    /// <summary>
    /// Registers the FSharpBinaryCodec as a generalized codec together with its type filter,
    /// and deliberately no generalized copier: the functional grain runtime carries every
    /// argument and reply across an explicit byte boundary, which already gives a local call
    /// the same object-graph isolation as a remote one.
    /// </summary>
    /// <remarks>
    /// Codec registration is shared with <see cref="addToSerializerBuilder"/>: using both entry
    /// points on one builder keeps a single codec registration and still adds the compatibility
    /// entry point's generalized copier.
    /// </remarks>
    let addCodecToSerializerBuilder (builder: ISerializerBuilder) : ISerializerBuilder =
        ensureCodec builder.Services
        builder
