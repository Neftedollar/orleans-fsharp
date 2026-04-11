module Orleans.FSharp.Generator.Discovery

open System
open System.Reflection
open TypeShape.Core

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/// Info extracted from a [<FSharpEventSourcedGrain>] definition.
type EventSourcedStubInfo =
    { /// The grain interface the generated stub implements (e.g. IBankAccountGrain)
      InterfaceType: Type
      /// Grain state type (TState)
      StateType: Type
      /// Domain event type (TEvent)
      EventType: Type
      /// Command type (TCommand)
      CommandType: Type
      /// The F# let-binding name (e.g. "bankAccount")
      DefinitionName: string
      /// The compiled module class name (e.g. "BankAccountGrainDef")
      SourceModule: string
      /// Fully-qualified C# name of the source module (e.g. "Orleans.FSharp.Sample.BankAccountGrainDef")
      SourceModuleFqn: string
      /// Short name of the source assembly
      AssemblyName: string
      /// DU case names of the command type (for documentation)
      CommandCases: string list
      /// True when the grain interface inherits IFSharpEventSourcedGrain —
      /// use the thin FSharpEventSourcedGrainImpl-based stub template.
      UseThinStub: bool
      /// The log consistency provider name (e.g. "LogStorage", "CustomStorage"), or None for silo default.
      ConsistencyProvider: string option
      /// True when the definition declares customStorage callbacks — the thin stub
      /// will override ReadStateFromStorageCore / ApplyUpdatesToStorageCore.
      HasCustomStorage: bool }

// ---------------------------------------------------------------------------
// TypeShape helpers
// ---------------------------------------------------------------------------

/// Extract DU case names from an F# discriminated union type.
/// Returns [] when the type is not a DU.
let private getUnionCaseNames (t: Type) : string list =
    let shape = TypeShape.Create t

    match shape with
    | Shape.FSharpUnion union ->
        union.UnionCases |> Array.map (fun c -> c.CaseInfo.Name) |> Array.toList
    | _ -> []

/// Validate that a state type satisfies JournaledGrain<TState,TEvent> constraints:
///   TState : not struct  AND  TState : (new : unit -> TState)
let private validateStateType (t: Type) =
    if t.IsValueType then
        failwithf
            "State type '%s' must be a reference type (not a struct). \
             JournaledGrain<TState,TEvent> requires 'not struct'."
            t.FullName

    if (t.GetConstructor [||] |> isNull) then
        failwithf
            "State type '%s' must have a public parameterless constructor. \
             JournaledGrain<TState,TEvent> requires 'new : unit -> TState'."
            t.FullName

// ---------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------

/// Full name of the marker attribute — name-based matching is safe across load contexts.
[<Literal>]
let private AttrFullName =
    "Orleans.FSharp.EventSourcing.FSharpEventSourcedGrainAttribute"

/// Full name of the definition generic type.
[<Literal>]
let private DefTypeBaseName =
    "Orleans.FSharp.EventSourcing.EventSourcedGrainDefinition`3"

/// Full name of the universal event-sourced grain interface.
[<Literal>]
let private UniversalInterfaceFullName =
    "Orleans.FSharp.IFSharpEventSourcedGrain"

/// True when the interface inherits IFSharpEventSourcedGrain (by name, cross-load-context safe).
let private inheritsUniversalInterface (t: Type) : bool =
    t.GetInterfaces()
    |> Array.exists (fun i -> i.FullName = UniversalInterfaceFullName)

/// Scan all public static properties in the assembly for
/// [<FSharpEventSourcedGrain(typeof<IGrainInterface>)>] bindings.
let discoverEventSourcedGrains (assembly: Assembly) : EventSourcedStubInfo list =
    [ for t in assembly.GetTypes() do
          for prop in t.GetProperties(BindingFlags.Public ||| BindingFlags.Static) do
              let attr =
                  prop.GetCustomAttributes()
                  |> Seq.tryFind (fun a -> a.GetType().FullName = AttrFullName)

              match attr with
              | None -> ()
              | Some attr ->
                  let propType = prop.PropertyType

                  let defTypeName =
                      if propType.IsGenericType then
                          propType.GetGenericTypeDefinition().FullName |> Option.ofObj
                      else
                          None

                  if defTypeName <> Some DefTypeBaseName then
                      eprintfn
                          "WARNING: [FSharpEventSourcedGrain] on '%s.%s' — \
                           property type is not EventSourcedGrainDefinition<S,E,C>. Skipping."
                          t.Name
                          prop.Name
                  else

                  let args = propType.GetGenericArguments()
                  let stateType = args.[0]
                  let eventType = args.[1]
                  let commandType = args.[2]

                  // Read GrainInterface via reflection (cross-load-context safe)
                  let grainInterface =
                      match attr.GetType().GetProperty("GrainInterface") with
                      | null -> failwithf "Attribute on '%s.%s' has no GrainInterface property." t.Name prop.Name
                      | pi ->
                          match pi.GetValue(attr) with
                          | null -> failwithf "GrainInterface is null on '%s.%s'." t.Name prop.Name
                          | v -> v :?> Type

                  validateStateType stateType

                  // Read the actual definition value to inspect runtime fields.
                  let defValue = prop.GetValue(null)

                  // Extract ConsistencyProvider: string option
                  // FSharpOption<string>: None = null object, Some x = non-null FSharpOption<string>
                  let consistencyProvider =
                      if isNull defValue then None
                      else
                          match propType.GetProperty("ConsistencyProvider") with
                          | null -> None
                          | csProp ->
                              let opt = csProp.GetValue(defValue)
                              if isNull opt then None
                              else
                                  // FSharpOption<string> is non-null for Some — read .Value
                                  match opt.GetType().GetProperty("Value") with
                                  | null -> None
                                  | vProp -> vProp.GetValue(opt) |> Option.ofObj |> Option.map string

                  // Detect custom storage: CustomStorage field is None (null) or Some (non-null)
                  let hasCustomStorage =
                      if isNull defValue then false
                      else
                          match propType.GetProperty("CustomStorage") with
                          | null -> false
                          | csProp -> not (isNull (csProp.GetValue(defValue)))

                  yield
                      { InterfaceType = grainInterface
                        StateType = stateType
                        EventType = eventType
                        CommandType = commandType
                        DefinitionName = prop.Name
                        SourceModule = t.Name
                        SourceModuleFqn = t.FullName.Replace('+', '.')
                        AssemblyName = assembly.GetName().Name
                        CommandCases = getUnionCaseNames commandType
                        UseThinStub = inheritsUniversalInterface grainInterface
                        ConsistencyProvider = consistencyProvider
                        HasCustomStorage = hasCustomStorage } ]
