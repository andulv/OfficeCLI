// Copyright 2026 OfficeCLI (https://OfficeCLI.AI)
// SPDX-License-Identifier: Apache-2.0

namespace OfficeCli.Core;

/// <summary>
/// Sink for watch Server-Sent Events. The watch engine builds each event and
/// hands it to a broadcaster, which delivers it to the connected browsers over
/// whatever transport the host uses (the default is a loopback HTTP/SSE server;
/// an embedder may implement this to serve the preview from its own web stack).
/// </summary>
public interface IWatchBroadcaster
{
    /// <summary>Deliver a single SSE event to all connected watch clients.</summary>
    void Broadcast(WatchSseEvent watchEvent);
}

/// <summary>
/// A single Server-Sent Event destined for the watch browser(s). <see cref="Data"/>
/// is the pre-serialized event payload (the SSE <c>data:</c> field); <see cref="EventName"/>
/// is the SSE <c>event:</c> field.
/// </summary>
public sealed record WatchSseEvent(string Data, string EventName = "update");
