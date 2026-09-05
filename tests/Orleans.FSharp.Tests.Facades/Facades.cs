// C#-declared facade interfaces for the FunctionalGrainInterop tests.
//
// They live in a C# project on purpose. Two of the rejection rules -- default interface methods
// and events -- cannot be written in F# at all, so an F#-only fixture set would leave them
// untested; and every accepted interface here is literally the C# a consumer writes, which is the
// thing under test.
//
// The contract these bind to is FacadeApi in tests/Orleans.FSharp.Tests/FunctionalInteropTests.fs:
//
//     join:        string        -> Task<unit>
//     leave:       string        -> Task<unit>
//     say:         string*string -> Task<Result<int64, string>>
//     history:     int           -> Task<string list>
//     memberCount: unit          -> Task<int>
//     typing:      string*bool   -> Task<unit>
//
// Only FSharp.Core types appear in these signatures. That is deliberate: an argument or reply
// type declared in the F# test assembly would need a project reference back to it, and the
// example (examples/chat-room/src/Interop) is where a C# consumer calling a grain whose reply
// carries an F# discriminated union is proven end to end.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Orleans;
using Orleans.FSharp;

namespace Orleans.FSharp.Tests.Facades;

// ── Accepted shapes ──────────────────────────────────────────────────────────

/// Every argument and reply shape at once: unit argument, single argument, tuple argument,
/// unit reply as `Task` and as `Task<Unit>`, a Result reply, and a list reply.
public interface IRoomFacade
{
    Task Join(string user);
    Task<Unit> Leave(string user);
    Task<FSharpResult<long, string>> Say(string author, string text);
    Task<FSharpList<string>> History(int take);
    Task<int> MemberCount();
    Task Typing(string user, bool isTyping);
}

/// A partial facade: three of the contract's six operations, the rest left alone.
public interface IPartialFacade
{
    Task Join(string user);
    Task<int> MemberCount();
}

/// The explicit override, and an alias: two members bound to the same operation.
public interface IAliasFacade
{
    Task Join(string user);

    [FunctionalOperation("join")]
    Task Enter(string user);
}

/// A tuple argument taken as one parameter of the tuple type rather than as two parameters.
public interface ITupleAsSingleFacade
{
    Task<FSharpResult<long, string>> Say(Tuple<string, string> post);
}

/// Members inherited from an extended interface are bound too.
public interface IExtendedBase
{
    Task Join(string user);
}

public interface IExtendedFacade : IExtendedBase
{
    Task<int> MemberCount();
}

/// The attribute names an operation the member's own name does not match.
public interface IRenamedFacade
{
    [FunctionalOperation("say")]
    Task<FSharpResult<long, string>> Post(string author, string text);
}

// ── The ambiguity fixtures (bound to AmbiguousApi: `say` and `Say`) ───────────

/// Matches both `say` and `Say` case-insensitively.
public interface IAmbiguousFacade
{
    Task<int> SAY(string text);
}

/// The same member, disambiguated by the exactly-matched override.
public interface IDisambiguatedFacade
{
    [FunctionalOperation("Say")]
    Task<int> SAY(string text);
}

// ── Rejected member shapes (rule 5) ──────────────────────────────────────────

public interface IGenericMemberFacade
{
    Task Join<T>(T user);
}

public interface IRefParameterFacade
{
    Task Join(ref string user);
}

public interface IOutParameterFacade
{
    Task Join(out string user);
}

public interface IInParameterFacade
{
    Task Join(in string user);
}

public interface IPropertyFacade
{
    string Join { get; }
}

public interface IEventFacade
{
    Task Join(string user);
    event EventHandler Typing;
}

public interface IDefaultImplementationFacade
{
    Task Join(string user) => Task.CompletedTask;
}

public interface IStaticMemberFacade
{
    Task Join(string user);
    static Task Helper() => Task.CompletedTask;
}

// ── Rejected reply shapes (rule 4) ───────────────────────────────────────────

public interface IVoidReplyFacade
{
    void Join(string user);
}

public interface IValueTaskReplyFacade
{
    ValueTask Join(string user);
}

public interface IWrongReplyFacade
{
    Task<int> Say(string author, string text);
}

public interface IBareTaskForNonUnitReplyFacade
{
    Task MemberCount();
}

// ── Rejected argument shapes (rule 3) ────────────────────────────────────────

public interface IMissingArgumentFacade
{
    Task Join();
}

public interface IWrongArgumentTypeFacade
{
    Task Join(int user);
}

public interface ITooManyArgumentsFacade
{
    Task Join(string user, string other);
}

public interface ITupleArityFacade
{
    Task<FSharpResult<long, string>> Say(string author, string text, string extra);
}

public interface ITupleElementFacade
{
    Task<FSharpResult<long, string>> Say(string author, int text);
}

public interface IUnitArgumentWithParameterFacade
{
    Task<int> MemberCount(int take);
}

// ── Rejected name mappings (rules 1 and 2) ───────────────────────────────────

public interface IUnmappedFacade
{
    Task Shout(string text);
}

public interface IUnknownOverrideFacade
{
    [FunctionalOperation("shout")]
    Task Join(string user);
}

/// The override is matched exactly, so a case-folded operation ID does not resolve.
public interface ICaseFoldedOverrideFacade
{
    [FunctionalOperation("JOIN")]
    Task Join(string user);
}

public interface IBlankOverrideFacade
{
    [FunctionalOperation("")]
    Task Join(string user);
}

/// An extended interface whose own member is fine but whose base member is not.
public interface IUnmappedBase
{
    Task Shout(string text);
}

public interface IExtendedUnmappedFacade : IUnmappedBase
{
    Task Join(string user);
}

/// A well-known BCL interface, extended by accident: Dispose returns void.
public interface IDisposableFacade : IDisposable
{
    Task Join(string user);
}

// ── Cancellation is not part of the facade surface ───────────────────────────

public interface ICancellableFacade
{
    Task Join(string user, CancellationToken cancellationToken);
}

// ── Implicit-subscription reference classes (spec 004 item 1) ────────────────
//
// Attribute-decorated classes whose published manifest bindings are the reference the functional
// runtime's own binding publication is compared against, byte for byte. They live in C# for the
// same reason the facades above do: this is literally what a consumer writes, and running
// Orleans' own AttributeGrainBindingsProvider over a decorated class is the only way to get the
// real property keys and values rather than a transcription of them.
//
// They deliberately implement NO grain-key interface (IGrainWithStringKey and friends), because
// the functional marker class FunctionalGrainMarker<TActor> implements none either -- and
// LegacyGrainId.IsLegacyGrainType, which the attributes consult, keys off exactly those
// interfaces. A reference class that implemented one would publish an extra
// "legacy-grain-key-type" binding key the functional grain type does not have, and the exactness
// test would (correctly) fail.

/// One implicit stream subscription, the shape `onStream provider "ns"` publishes.
[ImplicitStreamSubscription(ImplicitSubscriptionNamespaces.Stream)]
public sealed class ImplicitStreamReference
{
}

/// One implicit broadcast-channel subscription, the shape `onBroadcast provider "ns"` publishes.
[ImplicitChannelSubscription(ImplicitSubscriptionNamespaces.Channel)]
public sealed class ImplicitChannelReference
{
}

/// The namespaces the reference classes above are decorated with, so the F# test and the C#
/// attribute cannot drift apart.
public static class ImplicitSubscriptionNamespaces
{
    public const string Stream = "orleans.fsharp.tests.implicit.stream";
    public const string Channel = "orleans.fsharp.tests.implicit.channel";
}

// ── Binary-codec POCO fixtures (issue #33) ──────────────────────────────────
//
// These are deliberately ordinary CLR classes. An F# [<CLIMutable>] record is still an
// F# record to TypeShape and therefore cannot prove that the Shape.Poco codec is correct.

public sealed class FieldsOnlyPoco
{
    public int Count;
    public string? Name;
}

public sealed class PropertyPoco
{
    public int Count { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// Orleans owns this outer object through a generated codec while the unannotated POCO fields
/// are delegated to the generalized F# codec. Repeated fields therefore share one Orleans session.
[GenerateSerializer]
public sealed class NativeMixedEnvelope
{
    [Id(0)]
    public int Native { get; set; }

    [Id(1)]
    public PropertyPoco First { get; set; } = new();

    [Id(2)]
    public PropertyPoco Second { get; set; } = new();

    [Id(3)]
    public PropertyPoco? Missing { get; set; }
}

/// The private fields intentionally have the opposite order from the public properties.
/// A positional property-to-field mapping silently swaps First and Second.
public sealed class ReorderedBackingFieldsPoco
{
    private int _second;
    private int _first;

    public int First
    {
        get => _first;
        set => _first = value;
    }

    public int Second
    {
        get => _second;
        set => _second = value;
    }
}

public class InheritedPocoBase
{
    public int BaseValue { get; set; }
}

public sealed class InheritedPoco : InheritedPocoBase
{
    public int DerivedValue { get; set; }
}

public class ReadOnlyPocoBase
{
    public int Count { get; } = 3;
}

public sealed class InheritedReadOnlyPoco : ReadOnlyPocoBase
{
    public string Name { get; } = "readonly";
}

/// A base property's compiler backing field cannot reconstruct this unrelated hidden getter.
public sealed class HiddenComputedPoco : ReadOnlyPocoBase
{
    public new int Count => base.Count * 2;
}

/// This shape has state which is not represented by one reconstructable property model.
/// The binary codec must reject it explicitly instead of silently dropping Value.
public sealed class ComputedPropertyPoco
{
    public int Value;
    public int Double => Value * 2;
}

public enum SignedByteEnum : sbyte
{
    Minimum = sbyte.MinValue,
    Zero = 0,
    Maximum = sbyte.MaxValue,
}

public enum UnsignedByteEnum : byte
{
    Minimum = byte.MinValue,
    Maximum = byte.MaxValue,
}

public enum SignedShortEnum : short
{
    Minimum = short.MinValue,
    Maximum = short.MaxValue,
}

public enum UnsignedShortEnum : ushort
{
    Minimum = ushort.MinValue,
    Maximum = ushort.MaxValue,
}

public enum SignedIntEnum : int
{
    Minimum = int.MinValue,
    Maximum = int.MaxValue,
}

public enum UnsignedIntEnum : uint
{
    Minimum = uint.MinValue,
    Maximum = uint.MaxValue,
}

public enum SignedLongEnum : long
{
    Minimum = long.MinValue,
    Maximum = long.MaxValue,
}

public enum UnsignedLongEnum : ulong
{
    Minimum = ulong.MinValue,
    Zero = 0,
    Maximum = ulong.MaxValue,
}
