namespace Orleans.FSharp.Testing

open System
open System.Collections.Concurrent
open System.Reflection
open Microsoft.FSharp.Reflection
open FsCheck
open FsCheck.FSharp

/// <summary>
/// Internal helper type providing static generic methods that can be invoked
/// via reflection to produce typed FsCheck generators.
/// </summary>
[<AbstractClass; Sealed>]
type internal GenHelpers private () =

    /// <summary>
    /// Produces a Gen for the given type using FsCheck default arbitraries.
    /// Returns the Gen boxed as obj. Called via reflection with MakeGenericMethod.
    /// </summary>
    static member DefaultGen<'T>() : obj =
        box (ArbMap.defaults |> ArbMap.generate<'T>)

    /// <summary>Map a typed Gen to Gen of obj for sequencing field generators.</summary>
    static member MapToObj<'T>(gen: obj) : obj =
        let typedGen = gen :?> Gen<'T>
        box (typedGen |> Gen.map box)

    /// <summary>
    /// Sequence an array of Gen of obj, construct the union case from the values,
    /// and return a typed Gen for the result.
    /// </summary>
    static member SequenceAndConstruct<'Result>(objGens: obj array, construct: Func<obj array, obj>) : obj =
        let typedGens = objGens |> Array.map (fun g -> g :?> Gen<obj>) |> Array.toList

        let rec sequenceGens (acc: obj list) (remaining: Gen<obj> list) : Gen<obj list> =
            match remaining with
            | [] -> Gen.constant (List.rev acc)
            | g :: rest ->
                gen {
                    let! v = g
                    return! sequenceGens (v :: acc) rest
                }

        let resultGen : Gen<'Result> =
            gen {
                let! values = sequenceGens [] typedGens
                let arr = values |> List.toArray
                return construct.Invoke(arr) :?> 'Result
            }

        box resultGen

    /// <summary>Choose uniformly from an array of generators.</summary>
    static member OneOf<'T>(gens: obj array) : obj =
        let typedGens = gens |> Array.map (fun g -> g :?> Gen<'T>) |> Array.toList
        box (Gen.oneof typedGens)

    /// <summary>
    /// Map a single-field Gen to a result Gen via a constructor function.
    /// </summary>
    static member MapField<'Field, 'Result>(fieldGen: obj, construct: Func<obj, obj>) : obj =
        let typedFieldGen = fieldGen :?> Gen<'Field>

        let resultGen : Gen<'Result> =
            typedFieldGen |> Gen.map (fun v -> construct.Invoke(v) :?> 'Result)

        box resultGen

    /// <summary>
    /// Wrap a constant value in a Gen.
    /// </summary>
    static member Constant<'T>(value: obj) : obj =
        box (Gen.constant (value :?> 'T))

/// <summary>
/// Auto-generates FsCheck Arbitrary instances for F# discriminated unions and other types
/// using FSharp.Reflection for runtime type inspection. Designed for property-based testing
/// of grain state types and command types without manually writing Arbitrary instances.
/// </summary>
[<RequireQualifiedAccess>]
module GrainArbitrary =

    /// <summary>Cache to avoid regenerating Gen instances for the same type.</summary>
    let private genCache = ConcurrentDictionary<Type, obj>()

    // Pre-fetch MethodInfo for GenHelpers static methods using robust lookup
    let private helpersType = typeof<GenHelpers>
    let private allMethods =
        helpersType.GetMethods(BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

    let private findMethod name =
        allMethods
        |> Array.find (fun m -> m.Name = name)

    let private defaultGenMethod = findMethod "DefaultGen"
    let private mapToObjMethod = findMethod "MapToObj"
    let private sequenceMethod = findMethod "SequenceAndConstruct"
    let private oneOfMethod = findMethod "OneOf"
    let private mapFieldMethod = findMethod "MapField"
    let private constantMethod = findMethod "Constant"

    /// <summary>
    /// Gets or creates an FsCheck Gen (boxed) for the given System.Type at runtime.
    /// Uses FSharp.Reflection to detect DU structure and construct instances.
    /// Falls back to FsCheck default generators for primitives and non-DU types.
    /// </summary>
    let rec private mkGenUntyped (ty: Type) : obj =
        match genCache.TryGetValue(ty) with
        | true, cached -> cached
        | _ ->
            // Pre-insert default to break infinite recursion on recursive types
            let fallback = defaultGenMethod.MakeGenericMethod(ty).Invoke(null, [||])
            genCache.[ty] <- fallback

            let result =
                if FSharpType.IsUnion(ty, true) then
                    mkUnionGenUntyped ty
                else
                    fallback

            genCache.[ty] <- result
            result

    and private mkUnionGenUntyped (ty: Type) : obj =
        let cases = FSharpType.GetUnionCases(ty, true)

        if cases.Length = 0 then
            invalidOp $"Cannot generate values for empty union type '{ty.Name}'"

        let caseGens =
            cases
            |> Array.map (fun case ->
                let fields = case.GetFields()

                if fields.Length = 0 then
                    // Fieldless case: constant generator
                    let value = FSharpValue.MakeUnion(case, [||], true)
                    constantMethod.MakeGenericMethod(ty).Invoke(null, [| value |])

                elif fields.Length = 1 then
                    // Single field: map the field gen to the union case
                    let fieldType = fields.[0].PropertyType
                    let fieldGen = mkGenUntyped fieldType

                    mapFieldMethod
                        .MakeGenericMethod(fieldType, ty)
                        .Invoke(
                            null,
                            [| fieldGen
                               Func<obj, obj>(fun v -> FSharpValue.MakeUnion(case, [| v |], true)) |]
                        )

                else
                    // Multiple fields: sequence all field gens, then construct
                    let fieldTypes = fields |> Array.map (fun f -> f.PropertyType)

                    let objGens =
                        fieldTypes
                        |> Array.map (fun ft ->
                            let fieldGen = mkGenUntyped ft
                            mapToObjMethod.MakeGenericMethod(ft).Invoke(null, [| fieldGen |]))

                    sequenceMethod
                        .MakeGenericMethod(ty)
                        .Invoke(
                            null,
                            [| objGens
                               Func<obj array, obj>(fun args -> FSharpValue.MakeUnion(case, args, true)) |]
                        ))

        oneOfMethod.MakeGenericMethod(ty).Invoke(null, [| caseGens |])

    /// <summary>
    /// Auto-generate an FsCheck Arbitrary for any grain state type.
    /// Uses FSharp.Reflection to inspect the type structure and generate all DU cases
    /// with valid nested data (records, options, lists, other DUs).
    /// For non-DU types, falls back to the default FsCheck generator.
    /// </summary>
    /// <typeparam name="'T">The state type to generate an Arbitrary for.</typeparam>
    /// <returns>An Arbitrary that generates valid instances of the state type.</returns>
    let forState<'T> () : Arbitrary<'T> =
        let gen = mkGenUntyped typeof<'T> :?> Gen<'T>
        Arb.fromGen gen

    /// <summary>
    /// Auto-generate an FsCheck Arbitrary for command sequences (non-empty lists).
    /// Uses FSharp.Reflection to inspect the command DU structure and generate all cases
    /// with valid nested data. Produces non-empty lists of commands suitable for
    /// state machine property testing.
    /// </summary>
    /// <typeparam name="'Command">The command type to generate sequences for.</typeparam>
    /// <returns>An Arbitrary that generates non-empty lists of commands.</returns>
    let forCommands<'Command> () : Arbitrary<'Command list> =
        let cmdGen = mkGenUntyped typeof<'Command> :?> Gen<'Command>
        let listGen = Gen.nonEmptyListOf cmdGen
        Arb.fromGen listGen
