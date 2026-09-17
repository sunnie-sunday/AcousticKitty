// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;

namespace AcousticKitty.Common;

public sealed record DatabaseEntryViewModel(
	string CacheKey,
	string Name,
	string World,
	Lazy<LodestoneProfile>? Profile,
	DateTime? ProfileFetchedAtUtc,
	PlayerLocalData? CharacterData,
	DateTime? CharacterDataAsOfUtc,
	string? AvatarUrlHash = null,
	string? DataCenter = null,
	string? JobAbbreviation = null,
	string? LevelText = null,
	Lazy<IReadOnlyList<NameHistoryEntry>>? NameHistory = null);
