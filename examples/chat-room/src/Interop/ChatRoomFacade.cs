using System;
using System.Threading.Tasks;
using ChatRoom.Grains;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Orleans.FSharp;

namespace ChatRoom.Interop;

/// <summary>
/// The C# view of <c>RoomApi</c> (see <c>src/Grains/ChatGrainFunctional.fs</c>). Nothing generates
/// this interface: a consumer writes the members it wants, and
/// <see cref="Orleans.FSharp.FunctionalGrainInterop.For{TFacade}"/> checks every one of them
/// against the contract when the facade is created.
/// </summary>
/// <remarks>
/// <para>
/// Four rules are visible here. A member name matches its operation ID case-insensitively, so
/// <c>MemberCount</c> reaches the record field <c>memberCount</c>. An operation taking a tuple --
/// <c>say: string * string -&gt; Task&lt;Result&lt;int, ChatError&gt;&gt;</c> -- is written as an
/// ordinary two-parameter member, and the facade packs the tuple. A <c>Task&lt;unit&gt;</c> reply is
/// written as the plain <c>Task</c> a C# author expects. And the room's <c>subscribe</c> /
/// <c>unsubscribe</c> operations are simply absent: a facade may cover part of a contract.
/// </para>
/// </remarks>
public interface IChatRoom
{
    /// <summary>Adds a member to the room. Idempotent.</summary>
    Task Join(string user);

    /// <summary>Removes a member from the room. Idempotent.</summary>
    Task Leave(string user);

    /// <summary>
    /// Posts a message. The reply is the F# <c>Result</c> the grain returns, verbatim: an
    /// <c>FSharpResult</c> carrying the new message count, or a <see cref="ChatError"/>.
    /// </summary>
    Task<FSharpResult<int, ChatError>> Say(string sender, string message);

    /// <summary>The most recent entries, newest first, as the F# list the grain returns.</summary>
    Task<FSharpList<Tuple<string, string, DateTimeOffset>>> History(int take);

    /// <summary>The current member count. A unit-argument operation is a parameterless member.</summary>
    Task<int> MemberCount();

    /// <summary>
    /// The fire-and-forget typing indicator. The member is named <c>Typing</c> in C# and the
    /// operation is <c>typing</c>, which the case-insensitive match already covers; the attribute
    /// is here to show the explicit form, which is what a renamed member needs.
    /// </summary>
    [FunctionalOperation("typing")]
    Task NotifyTyping(string user, bool isTyping);
}
