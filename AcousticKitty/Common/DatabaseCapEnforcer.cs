// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System.Collections.Generic;
using System.Linq;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;

namespace AcousticKitty.Common;

internal static class DatabaseCapEnforcer
{
	public const int UnverifiedCap = 25_000;
	public const int HiddenCap = 25_000;

	public static IReadOnlyList<KnownCharacter> GetUnverifiedCandidates(
		CharacterDirectory characterDirectory) =>
		characterDirectory.GetFoundOrAccessRestricted()
			.Where(known => known.LodestoneId != null)
			.ToArray();

	public static void EnforceUnverified(CharacterDirectory characterDirectory, LodestoneCache lodestoneCache)
	{
		var candidates = DatabaseCapEnforcer.GetUnverifiedCandidates(characterDirectory)
			.Select(known => (
				known.Data.ContentId,
				CacheKey: CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId),
				known.LastSeenUtc))
			.ToList();

		OldestFirstEviction.Trim(
			candidates, UnverifiedCap, _ => true, item => item.LastSeenUtc,
			items =>
			{
				characterDirectory.DeleteMany(items.Select(item => item.ContentId));
				lodestoneCache.DeleteCharacters(items.Select(item => item.CacheKey));
			});
	}

	public static void EnforceHidden(CharacterDirectory characterDirectory)
	{
		var candidates = characterDirectory.GetHidden().ToList();
		OldestFirstEviction.Trim(
			candidates, HiddenCap, _ => true, known => known.LastSeenUtc,
			items => characterDirectory.DeleteMany(items.Select(known => known.Data.ContentId)));
	}
}
