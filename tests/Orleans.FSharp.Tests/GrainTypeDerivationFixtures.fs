/// <summary>
/// Actor-brand fixtures for the optional-'grainType' tests (spec 003 Task 12) in
/// <c>FunctionalContractTests.fs</c>, <c>FunctionalDefinitionTests.fs</c>,
/// <c>FunctionalBindingTests.fs</c>, and <c>FunctionalRuntimeTests.fs</c>.
///
/// These types are declared directly under a <c>namespace</c>, deliberately NOT inside an F#
/// <c>module</c>: every type an F# <c>module</c> declares compiles as a CLR-nested type
/// (<c>Type.IsNested = true</c>, a '+' in <c>FullName</c>) even with no explicit nested-module
/// block, exactly like the actor brand types every OTHER test file in this project declares
/// under its own top-level <c>module Orleans.FSharp.Tests.XyzTests</c> line. The derivation rule
/// rejects a nested brand, so a fixture meant to prove the DERIVED grain type actually works has
/// to live outside that pattern -- matching how the specification's own worked example declares
/// its actor brand under <c>namespace Chat.Contracts</c>, not a <c>module</c>.
/// </summary>
namespace Orleans.FSharp.Tests.GrainTypeDerivation

/// <summary>A simple, non-generic, non-nested actor brand: deriving its grain type succeeds and
/// equals the literal string "DerivableActor".</summary>
type DerivableActor = private DerivableActor of unit

/// <summary>
/// A generic actor brand, still namespace-scoped (so NOT nested) -- isolates the "generic brand"
/// rejection from the "nested brand" rejection, which is exercised separately with an ordinary
/// test-file-local actor type (any type declared inside another test file's top-level module is
/// already nested).
/// </summary>
type GenericActor<'T> = private GenericActor of 'T

namespace Orleans.FSharp.Tests.GrainTypeDerivation.CollisionOne

/// <summary>Same CLR simple name as <c>CollisionTwo.CounterActor</c> below, but a different
/// namespace: two contracts built over these two brands derive the identical grain type name
/// "CounterActor" and must fail registration under the existing grain-type-uniqueness rule.</summary>
type CounterActor = private CounterActor of unit

namespace Orleans.FSharp.Tests.GrainTypeDerivation.CollisionTwo

/// <summary>See <c>CollisionOne.CounterActor</c> above.</summary>
type CounterActor = private CounterActor of unit
