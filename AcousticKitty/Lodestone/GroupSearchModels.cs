// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Character;

namespace AcousticKitty.Lodestone;

public enum GroupSearchState
{
	Idle,
	SearchingGroups,
	AwaitingSelection,
	FetchingRoster,
	Ready,
	NotFound,
	Error,
}

public enum MemberProfileState
{
	NotPinned,
	Fetching,
	Loaded,
	NotFound,
	AccessRestricted,
	Error,
}

public sealed class GroupMemberViewModel(MemberListEntry entry)
{
	public MemberListEntry Entry { get; } = entry;

	public bool IsPinned { get; internal set; }

	public bool IsVerified { get; internal set; }

	public bool IsConflicted { get; internal set; }

	public MemberProfileState ProfileState { get; internal set; } = MemberProfileState.NotPinned;

	public LodestoneProfile? Profile { get; internal set; }

	public DateTime? ProfileFetchedAtUtc { get; internal set; }

	public string? ErrorMessage { get; internal set; }

	public KnownCharacter? KnownCharacter { get; internal set; }
}
