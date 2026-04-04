module Orleans.FSharp.Tests.BehaviorPatternTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// ── Test domain: a chat room with phases ──

type ChatPhase =
    | WaitingForConfig
    | Running of maxHistory: int
    | Suspended of reason: string

type ChatState =
    { Phase: ChatPhase
      Messages: string list }

type ChatCommand =
    | Configure of maxHistory: int
    | Send of text: string
    | GetHistory
    | Suspend of reason: string
    | Resume

/// <summary>
/// Example behavior handler that dispatches based on (phase, command) tuples.
/// Demonstrates the behavior pattern without a dedicated CE.
/// </summary>
let chatHandler (state: ChatState) (cmd: ChatCommand) : Task<BehaviorResult<ChatState>> =
    task {
        match state.Phase, cmd with
        // WaitingForConfig behavior: only Configure is accepted
        | WaitingForConfig, Configure maxHistory ->
            return Become { state with Phase = Running maxHistory }
        | WaitingForConfig, _ ->
            return Stay state

        // Running behavior: process messages, allow suspension
        | Running maxHistory, Send msg ->
            let newMessages =
                (msg :: state.Messages) |> List.truncate maxHistory

            return Stay { state with Messages = newMessages }
        | Running _, GetHistory ->
            return Stay state
        | Running _, Suspend reason ->
            return Become { state with Phase = Suspended reason }
        | Running _, _ ->
            return Stay state

        // Suspended behavior: only Resume is accepted
        | Suspended _, Resume ->
            return Become { state with Phase = Running 50 }
        | Suspended _, _ ->
            return Stay state
    }

// ── BehaviorResult DU tests ──

[<Fact>]
let ``Stay wraps state`` () =
    let state = { Phase = WaitingForConfig; Messages = [] }
    let result = Stay state
    test <@ result = Stay state @>

[<Fact>]
let ``Become wraps state`` () =
    let state = { Phase = Running 10; Messages = [] }
    let result = Become state
    test <@ result = Become state @>

[<Fact>]
let ``Stop is a value case`` () =
    let result: BehaviorResult<ChatState> = Stop
    test <@ result = Stop @>

// ── Behavior.unwrap tests ──

[<Fact>]
let ``Behavior.unwrap extracts state from Stay`` () =
    let state = { Phase = Running 10; Messages = [ "hi" ] }
    let result = Behavior.unwrap { Phase = WaitingForConfig; Messages = [] } (Stay state)
    test <@ result = state @>

[<Fact>]
let ``Behavior.unwrap extracts state from Become`` () =
    let state = { Phase = Suspended "maintenance"; Messages = [] }
    let result = Behavior.unwrap { Phase = WaitingForConfig; Messages = [] } (Become state)
    test <@ result = state @>

[<Fact>]
let ``Behavior.unwrap returns original state for Stop`` () =
    let original = { Phase = Running 10; Messages = [ "hi" ] }
    let result = Behavior.unwrap original Stop
    test <@ result = original @>

// ── Behavior.map tests ──

[<Fact>]
let ``Behavior.map transforms Stay state`` () =
    let state = { Phase = Running 10; Messages = [ "a" ] }

    let result =
        Stay state
        |> Behavior.map (fun s -> { s with Messages = "b" :: s.Messages })

    match result with
    | Stay s -> test <@ s.Messages = [ "b"; "a" ] @>
    | _ -> failwith "Expected Stay"

[<Fact>]
let ``Behavior.map transforms Become state`` () =
    let state = { Phase = Running 10; Messages = [] }

    let result =
        Become state
        |> Behavior.map (fun s -> { s with Phase = Suspended "mapped" })

    match result with
    | Become s -> test <@ s.Phase = Suspended "mapped" @>
    | _ -> failwith "Expected Become"

[<Fact>]
let ``Behavior.map preserves Stop`` () =
    let result: BehaviorResult<ChatState> =
        Stop
        |> Behavior.map (fun s -> { s with Messages = [ "should not appear" ] })

    test <@ result = Stop @>

// ── Behavior.isTransition tests ──

[<Fact>]
let ``Behavior.isTransition returns false for Stay`` () =
    let result = Stay { Phase = Running 10; Messages = [] }
    test <@ Behavior.isTransition result = false @>

[<Fact>]
let ``Behavior.isTransition returns true for Become`` () =
    let result = Become { Phase = Suspended "test"; Messages = [] }
    test <@ Behavior.isTransition result = true @>

[<Fact>]
let ``Behavior.isTransition returns false for Stop`` () =
    let result: BehaviorResult<ChatState> = Stop
    test <@ Behavior.isTransition result = false @>

// ── Behavior.isStopped tests ──

[<Fact>]
let ``Behavior.isStopped returns false for Stay`` () =
    let result = Stay { Phase = Running 10; Messages = [] }
    test <@ Behavior.isStopped result = false @>

[<Fact>]
let ``Behavior.isStopped returns false for Become`` () =
    let result = Become { Phase = Running 10; Messages = [] }
    test <@ Behavior.isStopped result = false @>

[<Fact>]
let ``Behavior.isStopped returns true for Stop`` () =
    let result: BehaviorResult<ChatState> = Stop
    test <@ Behavior.isStopped result = true @>

// ── Behavior.toHandlerResult tests ──

[<Fact>]
let ``Behavior.toHandlerResult returns state and boxed state for Stay`` () =
    let state = { Phase = Running 10; Messages = [ "hi" ] }
    let (newState, boxed) = Behavior.toHandlerResult { Phase = WaitingForConfig; Messages = [] } (Stay state)
    test <@ newState = state @>
    test <@ unbox<ChatState> boxed = state @>

[<Fact>]
let ``Behavior.toHandlerResult returns state and boxed state for Become`` () =
    let state = { Phase = Suspended "reason"; Messages = [] }
    let (newState, boxed) = Behavior.toHandlerResult { Phase = WaitingForConfig; Messages = [] } (Become state)
    test <@ newState = state @>
    test <@ unbox<ChatState> boxed = state @>

[<Fact>]
let ``Behavior.toHandlerResult returns original for Stop`` () =
    let original = { Phase = Running 10; Messages = [ "hi" ] }
    let (newState, boxed) = Behavior.toHandlerResult original Stop
    test <@ newState = original @>
    test <@ unbox<ChatState> boxed = original @>

// ── Behavior pattern with chatHandler tests ──

[<Fact>]
let ``WaitingForConfig ignores Send`` () =
    task {
        let state = { Phase = WaitingForConfig; Messages = [] }
        let! result = chatHandler state (Send "hello")

        match result with
        | Stay s -> test <@ s.Phase = WaitingForConfig @>
        | _ -> failwith "Expected Stay"
    }

[<Fact>]
let ``WaitingForConfig ignores GetHistory`` () =
    task {
        let state = { Phase = WaitingForConfig; Messages = [] }
        let! result = chatHandler state GetHistory

        match result with
        | Stay s -> test <@ s.Phase = WaitingForConfig @>
        | _ -> failwith "Expected Stay"
    }

[<Fact>]
let ``WaitingForConfig ignores Suspend`` () =
    task {
        let state = { Phase = WaitingForConfig; Messages = [] }
        let! result = chatHandler state (Suspend "test")

        match result with
        | Stay s -> test <@ s.Phase = WaitingForConfig @>
        | _ -> failwith "Expected Stay"
    }

[<Fact>]
let ``WaitingForConfig ignores Resume`` () =
    task {
        let state = { Phase = WaitingForConfig; Messages = [] }
        let! result = chatHandler state Resume

        match result with
        | Stay s -> test <@ s.Phase = WaitingForConfig @>
        | _ -> failwith "Expected Stay"
    }

[<Fact>]
let ``Configure transitions from WaitingForConfig to Running`` () =
    task {
        let state = { Phase = WaitingForConfig; Messages = [] }
        let! result = chatHandler state (Configure 100)

        match result with
        | Become s -> test <@ s.Phase = Running 100 @>
        | _ -> failwith "Expected Become"
    }

[<Fact>]
let ``Running processes Send and truncates to maxHistory`` () =
    task {
        let state = { Phase = Running 3; Messages = [ "a"; "b"; "c" ] }
        let! result = chatHandler state (Send "d")

        match result with
        | Stay s ->
            test <@ s.Messages.Length = 3 @>
            test <@ s.Messages = [ "d"; "a"; "b" ] @>
        | _ -> failwith "Expected Stay"
    }

[<Fact>]
let ``Running processes GetHistory as Stay`` () =
    task {
        let state = { Phase = Running 10; Messages = [ "a"; "b" ] }
        let! result = chatHandler state GetHistory

        match result with
        | Stay s -> test <@ s.Messages = [ "a"; "b" ] @>
        | _ -> failwith "Expected Stay"
    }

[<Fact>]
let ``Running transitions to Suspended on Suspend`` () =
    task {
        let state = { Phase = Running 10; Messages = [] }
        let! result = chatHandler state (Suspend "maintenance")

        match result with
        | Become s -> test <@ s.Phase = Suspended "maintenance" @>
        | _ -> failwith "Expected Become"
    }

[<Fact>]
let ``Suspended ignores Send`` () =
    task {
        let state = { Phase = Suspended "off"; Messages = [] }
        let! result = chatHandler state (Send "hello")

        match result with
        | Stay s -> test <@ s.Phase = Suspended "off" @>
        | _ -> failwith "Expected Stay"
    }

[<Fact>]
let ``Suspended ignores GetHistory`` () =
    task {
        let state = { Phase = Suspended "off"; Messages = [] }
        let! result = chatHandler state GetHistory

        match result with
        | Stay s -> test <@ s.Phase = Suspended "off" @>
        | _ -> failwith "Expected Stay"
    }

[<Fact>]
let ``Suspended transitions to Running on Resume`` () =
    task {
        let state = { Phase = Suspended "done"; Messages = [ "a" ] }
        let! result = chatHandler state Resume

        match result with
        | Become s ->
            test <@ s.Phase = Running 50 @>
            test <@ s.Messages = [ "a" ] @>
        | _ -> failwith "Expected Become"
    }

// ── Integration with grain CE ──

[<Fact>]
let ``Behavior pattern works with handleState in grain CE`` () =
    task {
        let chatGrain =
            grain {
                defaultState { Phase = WaitingForConfig; Messages = [] }

                handleState (fun state cmd ->
                    task {
                        let! result = chatHandler state cmd
                        return Behavior.unwrap state result
                    })
            }

        let handler = chatGrain.Handler.Value
        let! (newState, _) = handler { Phase = WaitingForConfig; Messages = [] } (Configure 5)
        test <@ newState.Phase = Running 5 @>
    }

[<Fact>]
let ``Behavior toHandlerResult integrates with handle in grain CE`` () =
    task {
        let chatGrain =
            grain {
                defaultState { Phase = WaitingForConfig; Messages = [] }

                handle (fun state cmd ->
                    task {
                        let! result = chatHandler state cmd
                        return Behavior.toHandlerResult state result
                    })
            }

        let handler = chatGrain.Handler.Value
        let! (newState, boxed) = handler { Phase = WaitingForConfig; Messages = [] } (Configure 5)
        test <@ newState.Phase = Running 5 @>
        test <@ (unbox<ChatState> boxed).Phase = Running 5 @>
    }

// ── FsCheck property: arbitrary command sequences never crash ──

/// <summary>
/// Commands used in FsCheck property: a simple subset that avoids
/// needing custom Arbitrary instances for string/int DU payloads.
/// </summary>
type SimpleChatCommand =
    | SimpleConfigure
    | SimpleSend
    | SimpleGetHistory
    | SimpleSuspend
    | SimpleResume

let toRealCommand (cmd: SimpleChatCommand) : ChatCommand =
    match cmd with
    | SimpleConfigure -> Configure 10
    | SimpleSend -> Send "msg"
    | SimpleGetHistory -> GetHistory
    | SimpleSuspend -> Suspend "reason"
    | SimpleResume -> Resume

[<Property>]
let ``arbitrary command sequences never crash`` (commands: SimpleChatCommand list) =
    let mutable state = { Phase = WaitingForConfig; Messages = [] }

    for cmd in commands do
        let realCmd = toRealCommand cmd
        let result = (chatHandler state realCmd).GetAwaiter().GetResult()
        state <- Behavior.unwrap state result

    // Every resulting state should have a valid phase
    match state.Phase with
    | WaitingForConfig -> true
    | Running n -> n > 0
    | Suspended reason -> reason.Length > 0

[<Property>]
let ``behavior transitions are deterministic`` (commands: SimpleChatCommand list) =
    let run () =
        let mutable state = { Phase = WaitingForConfig; Messages = [] }

        for cmd in commands do
            let realCmd = toRealCommand cmd
            let result = (chatHandler state realCmd).GetAwaiter().GetResult()
            state <- Behavior.unwrap state result

        state

    let result1 = run ()
    let result2 = run ()
    result1 = result2

// ── Behavior.run tests ──────────────────────────────────────────────────────

[<Fact>]
let ``Behavior.run returns Stay state unchanged`` () =
    task {
        let h = fun state (_cmd: ChatCommand) -> task { return Stay state }
        let state = { Phase = Running 10; Messages = [ "hi" ] }
        let! result = Behavior.run h state GetHistory
        test <@ result = state @>
    }

[<Fact>]
let ``Behavior.run returns Become state`` () =
    task {
        let h = fun state (_cmd: ChatCommand) ->
            task { return Become { state with Phase = Suspended "test" } }
        let state = { Phase = Running 10; Messages = [] }
        let! result = Behavior.run h state GetHistory
        test <@ result.Phase = Suspended "test" @>
    }

[<Fact>]
let ``Behavior.run on Stop returns original state`` () =
    task {
        let h = fun _state (_cmd: ChatCommand) -> task { return Stop }
        let state = { Phase = Running 5; Messages = [ "a"; "b" ] }
        let! result = Behavior.run h state GetHistory
        test <@ result = state @>
    }

[<Fact>]
let ``Behavior.run can be plugged into handleState directly`` () =
    task {
        // Verify that Behavior.run produces the right type signature for handleState
        let graInDef =
            grain {
                defaultState { Phase = WaitingForConfig; Messages = [] }
                handleState (Behavior.run chatHandler)
            }

        let handler = GrainDefinition.getHandler graInDef
        let! (ns, _) = handler { Phase = WaitingForConfig; Messages = [] } (Configure 5)
        test <@ ns.Phase = Running 5 @>
    }

[<Fact>]
let ``Behavior.run: Configure then Send works end-to-end`` () =
    task {
        let initial = { Phase = WaitingForConfig; Messages = [] }
        let! s1 = Behavior.run chatHandler initial (Configure 3)
        test <@ s1.Phase = Running 3 @>

        let! s2 = Behavior.run chatHandler s1 (Send "hello")
        test <@ s2.Messages = [ "hello" ] @>

        let! s3 = Behavior.run chatHandler s2 (Suspend "maintenance")
        test <@ s3.Phase = Suspended "maintenance" @>

        let! s4 = Behavior.run chatHandler s3 Resume
        test <@ s4.Phase = Running 50 @>
    }

// ── Behavior.runWithContext tests ───────────────────────────────────────────

[<Fact>]
let ``Behavior.runWithContext Stay returns new state without deactivating`` () =
    task {
        let mutable deactivateCalled = false
        let ctx =
            { GrainContext.empty with
                DeactivateOnIdle = Some(fun () -> deactivateCalled <- true) }

        let h = fun _ctx state (_cmd: ChatCommand) -> task { return Stay state }
        let state = { Phase = Running 10; Messages = [] }
        let! result = Behavior.runWithContext h ctx state GetHistory
        test <@ result = state @>
        test <@ not deactivateCalled @>
    }

[<Fact>]
let ``Behavior.runWithContext Stop calls DeactivateOnIdle and returns original state`` () =
    task {
        let mutable deactivateCalled = false
        let ctx =
            { GrainContext.empty with
                DeactivateOnIdle = Some(fun () -> deactivateCalled <- true) }

        let h = fun _ctx _state (_cmd: ChatCommand) -> task { return Stop }
        let state = { Phase = Running 5; Messages = [ "x" ] }
        let! result = Behavior.runWithContext h ctx state GetHistory
        test <@ result = state @>       // original state returned
        test <@ deactivateCalled @>     // deactivation was triggered
    }

[<Fact>]
let ``Behavior.runWithContext Stop with no DeactivateOnIdle is safe`` () =
    task {
        // GrainContext.empty has DeactivateOnIdle = None
        let ctx = GrainContext.empty
        let h = fun _ctx _state (_cmd: ChatCommand) -> task { return Stop }
        let state = { Phase = WaitingForConfig; Messages = [] }
        let! result = Behavior.runWithContext h ctx state GetHistory
        test <@ result = state @>  // does not throw, returns original state
    }

[<Fact>]
let ``Behavior.runWithContext Become returns new state without deactivating`` () =
    task {
        let mutable deactivateCalled = false
        let ctx =
            { GrainContext.empty with
                DeactivateOnIdle = Some(fun () -> deactivateCalled <- true) }

        let newState = { Phase = Suspended "upgrade"; Messages = [] }
        let h = fun _ctx _state (_cmd: ChatCommand) -> task { return Become newState }
        let state = { Phase = Running 10; Messages = [] }
        let! result = Behavior.runWithContext h ctx state GetHistory
        test <@ result = newState @>
        test <@ not deactivateCalled @>
    }

[<Fact>]
let ``Behavior.runWithContext can be plugged into handleStateWithContext`` () =
    task {
        let grainDef =
            grain {
                defaultState { Phase = WaitingForConfig; Messages = [] }
                handleStateWithContext (Behavior.runWithContext (fun _ctx state cmd -> chatHandler state cmd))
            }

        let handler = GrainDefinition.getContextHandler grainDef
        let ctx = GrainContext.empty
        let! (ns, _) = handler ctx { Phase = WaitingForConfig; Messages = [] } (Configure 10)
        test <@ ns.Phase = Running 10 @>
    }

// ── Property tests for Behavior.run ────────────────────────────────────────

[<Property>]
let ``Behavior.run: arbitrary commands never crash`` (commands: SimpleChatCommand list) =
    let mutable state = { Phase = WaitingForConfig; Messages = [] }

    for cmd in commands do
        let realCmd = toRealCommand cmd
        state <- (Behavior.run chatHandler state realCmd).GetAwaiter().GetResult()

    match state.Phase with
    | WaitingForConfig -> true
    | Running n -> n > 0
    | Suspended reason -> reason.Length > 0

[<Property>]
let ``Behavior.run is equivalent to manual unwrap`` (commands: SimpleChatCommand list) =
    let runWithHelper =
        let mutable s = { Phase = WaitingForConfig; Messages = [] }
        for cmd in commands do
            s <- (Behavior.run chatHandler s (toRealCommand cmd)).GetAwaiter().GetResult()
        s

    let runManual =
        let mutable s = { Phase = WaitingForConfig; Messages = [] }
        for cmd in commands do
            let result = (chatHandler s (toRealCommand cmd)).GetAwaiter().GetResult()
            s <- Behavior.unwrap s result
        s

    runWithHelper = runManual
