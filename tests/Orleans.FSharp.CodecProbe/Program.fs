namespace Orleans.FSharp.CodecProbe

open System
open System.IO
open System.Reflection
open System.Text
open System.Threading
open Microsoft.Extensions.DependencyInjection
open Orleans.Serialization
open Orleans.FSharp

/// A marker used by the parent test process to resolve this probe assembly.
type Marker = class end

[<AllowNullLiteral>]
type private CyclicNode(value: int) =
    member val Value = value with get, set
    member val Next: CyclicNode = null with get, set

module Program =

    let private cycleProbe () =
        let services = ServiceCollection()

        ServiceCollectionExtensions.AddSerializer(
            services,
            Action<ISerializerBuilder>(fun builder ->
                FSharpBinaryCodecRegistration.addToSerializerBuilder builder |> ignore)
        )
        |> ignore

        use provider = services.BuildServiceProvider()
        let serializer = provider.GetRequiredService<Serializer>()
        let value = CyclicNode(42)
        value.Next <- value

        try
            serializer.SerializeToArray value |> ignore
            eprintfn "CYCLE_WAS_NOT_REJECTED"
            2
        with
        | :? InvalidOperationException as error ->
            let expectedTypeName = typeof<CyclicNode>.FullName

            if not (error.Message.Contains "reference cycle was detected") then
                eprintfn "CYCLE_WRONG_DIAGNOSTIC: %s" error.Message
                3
            elif not (error.Message.Contains expectedTypeName) then
                eprintfn "CYCLE_MISSING_TYPE_NAME: %s" error.Message
                4
            else
                let healthy = CyclicNode(7)
                let bytes = serializer.SerializeToArray healthy
                let restored = serializer.Deserialize<CyclicNode> bytes

                if restored.Value <> healthy.Value || not (isNull restored.Next) then
                    eprintfn "POST_FAILURE_ROUNDTRIP_FAILED"
                    5
                else
                    printfn "CYCLE_REJECTED: %s" error.Message
                    printfn "CYCLE_TYPE_CONFIRMED: %s" expectedTypeName
                    printfn "POST_FAILURE_ROUNDTRIP_OK"
                    0
        | error ->
            eprintfn "CYCLE_WRONG_EXCEPTION: %s" (error.ToString())
            6

    let private methodWithSignature
        (moduleType: Type)
        (name: string)
        (parameterTypes: Type[])
        (returnType: Type)
        =
        let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static

        moduleType.GetMethods flags
        |> Array.filter (fun methodInfo ->
            methodInfo.Name = name
            && methodInfo.ReturnType = returnType
            && (methodInfo.GetParameters() |> Array.map _.ParameterType) = parameterTypes)
        |> function
            | [| methodInfo |] -> methodInfo
            | matches ->
                failwithf
                    "Expected one %s method with the requested signature on %s, found %d."
                    name
                    moduleType.FullName
                    matches.Length

    let private payloadWithTypeName (typeName: string) (body: byte[]) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        writer.Write typeName
        writer.Write body.Length
        writer.Write body
        writer.Flush()
        stream.ToArray()

    let private invoke (methodInfo: MethodInfo) arguments =
        try
            methodInfo.Invoke(null, arguments)
        with :? TargetInvocationException as error when not (isNull error.InnerException) ->
            raise error.InnerException

    let private cacheCapProbe () =
        let maxResolvedTypes = 512
        let workerCount = 64
        let prefillCount = maxResolvedTypes - workerCount / 2
        let codecAssembly = typeof<FSharpBinaryCodec>.Assembly

        let moduleType =
            codecAssembly.GetType("Orleans.FSharp.FSharpBinaryFormat", throwOnError = true)

        let countResolved =
            methodWithSignature moduleType "wireResolvedTypeCount" [||] typeof<int>

        let serialize =
            methodWithSignature moduleType "serialize" [| typeof<obj>; typeof<Type> |] typeof<byte[]>

        let deserializeWithType =
            methodWithSignature moduleType "deserializeWithType" [| typeof<byte[]>; typeof<Type> |] typeof<obj>

        let readCount () = countResolved.Invoke(null, [||]) :?> int
        let value = 0x12345678
        let body = invoke serialize [| box value; typeof<int> |] :?> byte[]
        let assemblyName = typeof<int>.Assembly.GetName().Name

        // Whitespace after the assembly separator is insignificant to Type.GetType but remains
        // part of the exact wire-name cache key. This supplies hundreds of distinct, legitimate
        // names without loading dynamic assemblies or relying on names that only get rejected.
        let payloads =
            Array.init (prefillCount + workerCount) (fun index ->
                let typeName = typeof<int>.FullName + "," + String(' ', index + 1) + assemblyName
                typeName, payloadWithTypeName typeName body)

        if (payloads |> Array.distinctBy fst).Length <> payloads.Length then
            failwith "The cache-cap fixture did not produce distinct wire type names."

        if readCount () <> 0 then
            failwithf "The cache-cap probe did not start in a fresh process: count=%d." (readCount ())

        for index in 0 .. prefillCount - 1 do
            let name, payload = payloads.[index]
            let restored = invoke deserializeWithType [| payload; null |] |> unbox<int>

            if restored <> value then
                failwithf "Nominal type %s decoded %d instead of %d." name restored value

            let expectedCount = index + 1

            if readCount () <> expectedCount then
                failwithf "Cache count after prefill %d was %d." expectedCount (readCount ())

        use barrier = new Barrier(workerCount + 1)
        let outcomes: Result<int, exn> option[] = Array.zeroCreate workerCount

        let threads =
            Array.init workerCount (fun workerIndex ->
                let _, payload = payloads.[prefillCount + workerIndex]

                let thread =
                    Thread(
                        ThreadStart(fun () ->
                            barrier.SignalAndWait()

                            outcomes.[workerIndex] <-
                                try
                                    invoke deserializeWithType [| payload; null |]
                                    |> unbox<int>
                                    |> Ok
                                    |> Some
                                with error ->
                                    Error error |> Some),
                        IsBackground = true
                    )

                thread.Start()
                thread)

        barrier.SignalAndWait()

        for thread in threads do
            if not (thread.Join(TimeSpan.FromSeconds 15.0)) then
                failwith "A cache-cap resolver thread exceeded its 15 second timeout."

        let mutable resolvedCount = 0
        let mutable capRejectionCount = 0

        for workerIndex in 0 .. workerCount - 1 do
            let name, _ = payloads.[prefillCount + workerIndex]

            match Type.GetType(name, throwOnError = false), outcomes.[workerIndex] with
            | actualType, _ when actualType <> typeof<int> ->
                failwithf "The nominal contender name did not resolve to System.Int32: %s" name
            | _, Some(Ok restored) when restored = value ->
                resolvedCount <- resolvedCount + 1
            | _, Some(Error(:? InvalidOperationException as error))
                when error.Message.Contains "already resolved 512 distinct type names" ->
                capRejectionCount <- capRejectionCount + 1
            | _, Some(Ok restored) ->
                failwithf "Nominal contender %s decoded %d instead of %d." name restored value
            | _, Some(Error error) ->
                failwithf "Unexpected resolver failure for %s: %O" name error
            | _, None ->
                failwithf "Resolver thread produced no outcome for %s." name

        let expectedConcurrentSuccesses = maxResolvedTypes - prefillCount
        let expectedCapRejections = workerCount - expectedConcurrentSuccesses
        let finalCount = readCount ()

        if finalCount <> maxResolvedTypes then
            failwithf "Cache cap was exceeded or under-filled: count=%d." finalCount

        if resolvedCount <> expectedConcurrentSuccesses then
            failwithf
                "Expected %d concurrent resolutions before the cap, got %d."
                expectedConcurrentSuccesses
                resolvedCount

        if capRejectionCount <> expectedCapRejections then
            failwithf "Expected %d cap rejections, got %d." expectedCapRejections capRejectionCount

        // Reaching the cap stops new admissions, not legitimate reads of existing entries.
        let _, cachedPayload = payloads.[0]
        let cachedValue = invoke deserializeWithType [| cachedPayload; null |] |> unbox<int>

        if cachedValue <> value || readCount () <> maxResolvedTypes then
            failwith "A cached hit failed or changed the cache size after capacity was reached."

        printfn
            "CACHE_CAP_OK count=%d resolved=%d rejected=%d verified=%d"
            finalCount
            resolvedCount
            capRejectionCount
            workerCount

        0

    let private cacheReentryProbe () =
        let moduleType = typeof<FSharpBinaryCodec>.Assembly.GetType("Orleans.FSharp.FSharpBinaryFormat", true)
        let count = methodWithSignature moduleType "wireResolvedTypeCount" [||] typeof<int>
        let serialize = methodWithSignature moduleType "serialize" [| typeof<obj>; typeof<Type> |] typeof<byte[]>
        let deserialize = methodWithSignature moduleType "deserializeWithType" [| typeof<byte[]>; typeof<Type> |] typeof<obj>
        let readCount () = invoke count [||] |> unbox<int>
        let value = 42
        let body = invoke serialize [| box value; typeof<int> |] :?> byte[]
        let decode name = invoke deserialize [| payloadWithTypeName name body; null |] |> unbox<int>
        let coreAssembly = typeof<int>.Assembly
        let alias index = typeof<int>.FullName + "," + String(' ', index) + coreAssembly.GetName().Name

        if readCount () <> 0 then
            failwith "The re-entry probe did not start with an empty cache."

        for index in 1 .. 511 do
            if decode (alias index) <> value then
                failwith "A re-entry prefill value did not round-trip."

        let requestedAssembly = "Orleans.FSharp.CodecProbe.ReentrantAlias"
        let mutable callbackEntered = false

        let handler =
            ResolveEventHandler(fun _ arguments ->
                if arguments.Name.Split(',').[0] = requestedAssembly then
                    callbackEntered <- true

                    // Fill the final slot while the outer resolver owns its reentrant lock.
                    if decode (alias 512) <> value then
                        failwith "The nested resolution did not round-trip."

                    coreAssembly
                else
                    null)

        AppDomain.CurrentDomain.add_AssemblyResolve handler

        try
            let mutable rejectedAtCap = false

            try
                decode (typeof<int>.FullName + ", " + requestedAssembly) |> ignore
            with :? InvalidOperationException as error when error.Message.Contains "already resolved 512 distinct type names" ->
                rejectedAtCap <- true

            if not callbackEntered || not rejectedAtCap || readCount () <> 512 then
                failwithf "Re-entry cap failed: callback=%b rejected=%b count=%d." callbackEntered rejectedAtCap (readCount ())

            if decode (alias 1) <> value then
                failwith "Cached reads stopped working after re-entry rejection."

            printfn "CACHE_REENTRY_OK count=512"
            0
        finally
            AppDomain.CurrentDomain.remove_AssemblyResolve handler

    [<EntryPoint>]
    let main arguments =
        try
            match arguments with
            | [| "cycle" |] -> cycleProbe ()
            | [| "cache-cap" |] -> cacheCapProbe ()
            | [| "cache-reentry" |] -> cacheReentryProbe ()
            | _ ->
                eprintfn "usage: Orleans.FSharp.CodecProbe cycle|cache-cap|cache-reentry"
                64
        with error ->
            eprintfn "PROBE_FAILED: %O" error
            70
