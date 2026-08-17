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
