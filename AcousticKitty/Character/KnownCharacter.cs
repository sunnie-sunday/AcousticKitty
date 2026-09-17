// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;

namespace AcousticKitty.Character;

public enum NearbySearchMode
{
	Auto,
	Manual,
}

public enum NearbyLookupState
{
	Pending,
	Found,
	NotFound,
	AccessRestricted,
	Error,
}

public sealed record KnownCharacter(
	PlayerLocalData Data,
	DateTime LastSeenUtc,
	ulong? LodestoneId,
	NearbyLookupState LookupState,
	string? LookupErrorMessage,
	DateTime? PriorityAtUtc,
	ulong? OverrideLodestoneId);

public sealed record NameHistoryEntry(string Name, string HomeWorldName, DateTime SeenUntilUtc);
