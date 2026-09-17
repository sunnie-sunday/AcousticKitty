// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Lodestone;

namespace AcousticKitty.Character;

public sealed class NearbyMemberViewModel(
	KnownCharacter knownCharacter,
	LodestoneProfile? profile,
	string? avatarUrlHash,
	bool isSearching,
	string worldName,
	string dataCenterName,
	string jobAbbreviation,
	DateTime? profileAsOfUtc = null)
{
	public KnownCharacter KnownCharacter { get; } = knownCharacter;

	public LodestoneProfile? Profile { get; internal set; } = profile;

	public DateTime? ProfileAsOfUtc { get; internal set; } = profileAsOfUtc;

	public string? AvatarUrlHash { get; internal set; } = avatarUrlHash;

	public bool IsSearching { get; internal set; } = isSearching;

	public string WorldName { get; } = worldName;

	public string DataCenterName { get; } = dataCenterName;

	public string JobAbbreviation { get; } = jobAbbreviation;
}
