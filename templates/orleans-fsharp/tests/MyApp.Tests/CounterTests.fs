module MyApp.Tests.CounterTests

open FsCheck
open MyApp.Grains
open Swensen.Unquote
open global.Xunit

[<global.FsCheck.Xunit.Property>]
let ``increment increases a non-negative counter by one`` (state: NonNegativeInt) =
    Counter.increment state.Get = state.Get + 1

[<global.FsCheck.Xunit.Property>]
let ``decrement never produces a negative counter`` (state: NonNegativeInt) =
    Counter.decrement state.Get >= 0

[<global.FsCheck.Xunit.Property>]
let ``increment then decrement restores a non-negative counter`` (state: NonNegativeInt) =
    state.Get |> Counter.increment |> Counter.decrement = state.Get

[<Fact>]
let ``zero remains zero when decremented`` () =
    test <@ Counter.decrement 0 = 0 @>

[<Fact>]
let ``positive counter decreases by one`` () =
    test <@ Counter.decrement 3 = 2 @>
