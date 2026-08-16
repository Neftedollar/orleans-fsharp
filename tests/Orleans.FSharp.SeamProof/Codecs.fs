/// Phase 0 seam proof — explicit hand-written Orleans serialization for the
/// fixed transport types.
///
/// F# assemblies get no Orleans source-generated codecs, so the fixed
/// request/reply/envelope types must be covered by explicit codecs, copiers and
/// activators. This spike registers one generalized codec/copier/type-filter
/// triple (the same seam `FSharpBinaryCodec` already uses in production) that
/// understands exactly the three fixed transport types.
namespace Orleans.FSharp.SeamProof

open System
open System.IO
open System.Threading
open Microsoft.Extensions.DependencyInjection
open Orleans.Serialization
open Orleans.Serialization.Buffers
open Orleans.Serialization.Cloning
open Orleans.Serialization.Codecs
open Orleans.Serialization.Serializers
open Orleans.Serialization.WireProtocol

[<RequireQualifiedAccess>]
module internal TransportWire =

    let private writeBytes (bw: BinaryWriter) (bytes: byte[]) =
        if isNull bytes then
            bw.Write(-1)
        else
            bw.Write(bytes.Length)
            bw.Write(bytes)

    let private readBytes (br: BinaryReader) =
        let len = br.ReadInt32()
        if len < 0 then null else br.ReadBytes len

    let private writeEnvelope (bw: BinaryWriter) (e: FunctionalRequestEnvelope) =
        bw.Write(e.GrainType)
        bw.Write(e.ContractVersion)
        bw.Write(e.OperationId)
        writeBytes bw e.ProtocolToken
        bw.Write(e.AdmissionFlags)
        writeBytes bw e.Payload

    let private readEnvelope (br: BinaryReader) =
        let grainType = br.ReadString()
        let version = br.ReadInt32()
        let operationId = br.ReadString()
        let token = readBytes br
        let flags = br.ReadByte()
        let payload = readBytes br
        FunctionalRequestEnvelope(grainType, version, operationId, token, flags, payload)

    let isSupportedType (t: Type) =
        t = typeof<FunctionalRequestEnvelope>
        || t = typeof<FunctionalReply>
        || t = typeof<FunctionalRequest>

    let serialize (value: obj) : byte[] =
        use ms = new MemoryStream()
        use bw = new BinaryWriter(ms, Text.Encoding.UTF8, true)

        match value with
        | :? FunctionalRequestEnvelope as e ->
            bw.Write(0uy)
            writeEnvelope bw e
        | :? FunctionalReply as r ->
            bw.Write(1uy)
            writeBytes bw r.ProtocolToken
            writeBytes bw r.Payload
        | :? FunctionalRequest as req ->
            bw.Write(2uy)
            writeEnvelope bw req.Envelope
        | null -> bw.Write(255uy)
        | other -> invalidOp $"SeamTransportCodec cannot serialize {other.GetType().FullName}."

        bw.Flush()
        ms.ToArray()

    let deserialize (data: byte[]) : obj =
        use ms = new MemoryStream(data)
        use br = new BinaryReader(ms, Text.Encoding.UTF8, true)

        match br.ReadByte() with
        | 0uy -> box (readEnvelope br)
        | 1uy ->
            let token = readBytes br
            let payload = readBytes br
            box (FunctionalReply(token, payload))
        | 2uy ->
            let envelope = readEnvelope br
            // Target and target-local cancellation state are never wire data.
            box (new FunctionalRequest(envelope, CancellationToken.None))
        | 255uy -> null
        | tag -> invalidOp $"SeamTransportCodec: unknown transport tag {tag}."

/// Instrumentation so a test can prove the fixed transport really crosses the
/// Orleans serialization boundary rather than being handed over as an object.
[<RequireQualifiedAccess>]
module SeamCodecCounters =
    let mutable private written = 0
    let mutable private read = 0

    let internal countWrite () = System.Threading.Interlocked.Increment &written |> ignore
    let internal countRead () = System.Threading.Interlocked.Increment &read |> ignore
    let writes () = System.Threading.Volatile.Read &written
    let reads () = System.Threading.Volatile.Read &read

/// Generalized codec/copier/type-filter for the three fixed transport types.
[<Sealed>]
type SeamTransportCodec() =

    interface IGeneralizedCodec with
        member _.IsSupportedType(t: Type) = TransportWire.isSupportedType t

    interface IFieldCodec with
        member _.WriteField<'TBufferWriter when 'TBufferWriter :> System.Buffers.IBufferWriter<byte>>
            (writer: byref<Writer<'TBufferWriter>>, fieldIdDelta: uint32, expectedType: Type, value: obj)
            =
            if ReferenceCodec.TryWriteReferenceField(&writer, fieldIdDelta, expectedType, value) then
                ()
            else
                let actualType = if isNull value then expectedType else value.GetType()
                let bytes = TransportWire.serialize value
                SeamCodecCounters.countWrite ()
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.LengthPrefixed)
                writer.WriteVarUInt32(uint32 bytes.Length)
                writer.Write(ReadOnlySpan<byte>(bytes))

        member _.ReadValue<'TInput>(reader: byref<Reader<'TInput>>, field: Field) : obj =
            if field.IsReference then
                ReferenceCodec.ReadReference<obj, 'TInput>(&reader, field)
            else
                let length = reader.ReadVarUInt32()
                let bytes = reader.ReadBytes(length)
                SeamCodecCounters.countRead ()
                TransportWire.deserialize bytes

    interface IGeneralizedCopier with
        member _.IsSupportedType(t: Type) = TransportWire.isSupportedType t

    interface IDeepCopier with
        member _.DeepCopy(input: obj, _context: CopyContext) : obj =
            match input with
            | null -> null
            // Envelope and reply are immutable after construction: payload arrays are
            // never handed out for mutation in this spike.
            | :? FunctionalRequestEnvelope -> input
            | :? FunctionalReply -> input
            | :? FunctionalRequest as request ->
                // A local copy preserves the envelope but resets target and
                // target-local cancellation resources.
                let copy = new FunctionalRequest(request.Envelope, CancellationToken.None)
                copy.SetCallerMetadata(typedefof<IFunctionalGrainTarget<_>>)
                copy.AddInvokeMethodOptions(request.Options)
                box copy
            | other -> other

    interface ITypeFilter with
        member _.IsTypeAllowed(t: Type) : Nullable<bool> =
            if TransportWire.isSupportedType t then
                Nullable<bool>(true)
            else
                Nullable<bool>()

[<RequireQualifiedAccess>]
module SeamTransportCodecRegistration =

    let addToSerializerBuilder (builder: ISerializerBuilder) : ISerializerBuilder =
        builder.Services.AddSingleton<SeamTransportCodec>() |> ignore

        builder.Services.AddSingleton<IGeneralizedCodec>(
            Func<IServiceProvider, IGeneralizedCodec>(fun sp -> sp.GetRequiredService<SeamTransportCodec>())
        )
        |> ignore

        builder.Services.AddSingleton<IGeneralizedCopier>(
            Func<IServiceProvider, IGeneralizedCopier>(fun sp -> sp.GetRequiredService<SeamTransportCodec>())
        )
        |> ignore

        builder.Services.AddSingleton<ITypeFilter>(
            Func<IServiceProvider, ITypeFilter>(fun sp -> sp.GetRequiredService<SeamTransportCodec>())
        )
        |> ignore

        builder
