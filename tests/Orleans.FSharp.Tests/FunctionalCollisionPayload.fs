/// <summary>
/// A payload type declared at NAMESPACE level, so its <c>FullName</c> carries no <c>+</c> and a
/// dynamic assembly can emit a distinct type with exactly the same name. That is what makes a
/// genuine top-level-payload-name collision reproducible in a test.
/// </summary>
/// <remarks>
/// Nothing else may use this type: declaring a colliding name poisons that entry of the
/// process-wide declaration table for the rest of the test run.
/// </remarks>
namespace Orleans.FSharp.Tests.Collision

/// <summary>An ordinary F# record the F# binary codec serializes.</summary>
type ContestedPayload = { note: string }
