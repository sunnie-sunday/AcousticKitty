// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;
using AcousticKitty.Echo;

namespace AcousticKitty.Common;

public enum DatabaseCacheTier
{
	Small,
	Medium,
	Large,
}

internal static class DatabaseCapEnforcer
{
	public static int ResolveCap(DatabaseCacheTier tier) => tier switch
	{
		DatabaseCacheTier.Small => 12_500,
		DatabaseCacheTier.Medium => 25_000,
		DatabaseCacheTier.Large => 50_000,
		_ => 25_000,
	};

	private static int EnforcementCeiling(int cap) => cap + (cap / 10);

	public static void EnforceMatched(
		CharacterDirectory characterDirectory, LodestoneCache lodestoneCache, EchoList echoList,
		int cap)
	{
		if (characterDirectory.CountMatched() <= EnforcementCeiling(cap))
		{
			return;
		}

		var candidates = DatabaseCapEnforcer.GetMatchedCandidates(characterDirectory, echoList)
			.Select(known => (
				known.Data.ContentId,
				CacheKey: CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId),
				known.LastSeenUtc))
			.ToList();

		OldestFirstEviction.Trim(
			candidates, cap, _ => true, item => item.LastSeenUtc,
			items =>
			{
				characterDirectory.DeleteMany(items.Select(item => item.ContentId));
				lodestoneCache.DeleteCharacters(items.Select(item => item.CacheKey));
			});
	}

	public static IReadOnlyList<KnownCharacter> GetMatchedCandidates(
		CharacterDirectory characterDirectory, EchoList echoList) =>
		characterDirectory.GetMatched()
			.Where(known =>
				known.LodestoneId != null && !echoList.IsPinned(known.Data.ContentId, known.LodestoneId))
			.ToArray();

	public static void EnforceHidden(
		CharacterDirectory characterDirectory, EchoList echoList, int cap)
	{
		if (characterDirectory.CountHidden() <= EnforcementCeiling(cap))
		{
			return;
		}

		var candidates =
			DatabaseCapEnforcer.GetHiddenCandidates(characterDirectory, echoList).ToList();
		OldestFirstEviction.Trim(
			candidates, cap, _ => true, known => known.LastSeenUtc,
			items => characterDirectory.DeleteMany(items.Select(known => known.Data.ContentId)));
	}

	public static IReadOnlyList<KnownCharacter> GetHiddenCandidates(
		CharacterDirectory characterDirectory, EchoList echoList) =>
		characterDirectory.GetHidden()
			.Where(known => !echoList.IsPinned(known.Data.ContentId, known.LodestoneId))
			.ToArray();

	public static void EnforceUnseen(
		CharacterDirectory characterDirectory, LodestoneCache lodestoneCache, EchoList echoList,
		int cap)
	{
		if (lodestoneCache.CountResolved() - characterDirectory.CountMatched()
			<= EnforcementCeiling(cap))
		{
			return;
		}

		var seenNameWorldKeys = new HashSet<string>(characterDirectory.GetAllNameWorldKeys());
		var resolvedByKey = lodestoneCache.GetAllResolved().ToDictionary(resolved => resolved.Key);

		var candidates = UnseenCandidates
			.Enumerate(
				lodestoneCache.GetAllCachedProfiles(), resolvedByKey, seenNameWorldKeys,
				ownNameWorldKey: null)
			.Where(row => !DatabaseCapEnforcer.IsPinnedUnseen(row, echoList))
			.Select(row => (row.Key, row.Timestamp))
			.ToList();

		OldestFirstEviction.Trim(
			candidates, cap, _ => true, item => item.Timestamp,
			items => lodestoneCache.DeleteCharacters(items.Select(item => item.Key)));
	}

	public static bool IsPinnedUnseen(UnseenRow row, EchoList echoList) =>
		row.Resolved is { } resolved
			? echoList.IsPinnedLodestoneId(resolved.LodestoneId)
			: echoList.IsPinnedNameWorldKey(row.Key);
}
