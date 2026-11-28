// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using AcousticKitty.Character;

namespace AcousticKitty.Lodestone;

public sealed record RosterFetch(SocialGroupKind Kind, string GroupId, string Name);

public enum GroupSearchState
{
	Idle,
	SearchingGroups,
	AwaitingSelection,
	FetchingRoster,
	Ready,
	NoMembers,
	NotFound,
	Error,
}

public sealed class GroupMemberViewModel(MemberListEntry entry)
{
	public MemberListEntry Entry { get; } = entry;

	public bool IsPinned { get; internal set; }
	public bool IsConflicted { get; internal set; }

	public LodestoneProfile? Profile { get; internal set; }
	public DateTime? ProfileFetchedAtUtc { get; internal set; }
	public KnownCharacter? KnownCharacter { get; internal set; }
	public IReadOnlyList<NameHistoryEntry>? NameHistory { get; internal set; }
}
