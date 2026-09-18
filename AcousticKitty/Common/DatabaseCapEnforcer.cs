// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;

namespace AcousticKitty.Common;

internal static class DatabaseCapEnforcer
{
	public const int UnverifiedCap = 25_000;
	public const int HiddenCap = 25_000;
	public const int UnseenCap = 25_000;

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

	public static void EnforceUnseen(CharacterDirectory characterDirectory, LodestoneCache lodestoneCache)
	{
		var seenNameWorldKeys = new HashSet<string>(characterDirectory.GetAllNameWorldKeys());
		var resolvedByKey = lodestoneCache.GetAllResolved().ToDictionary(resolved => resolved.Key);

		var candidates = new List<(string Key, DateTime Timestamp)>();
		var candidateKeys = new HashSet<string>();

		foreach (var cached in lodestoneCache.GetAllCachedProfiles())
		{
			var hasResolved = resolvedByKey.TryGetValue(cached.Key, out var resolvedForKey);
			var name = hasResolved ? resolvedForKey!.Name : cached.Profile.Value.Name;
			var homeWorldId = hasResolved ? resolvedForKey!.HomeWorldId : cached.Profile.Value.HomeWorldId;
			var nameWorldKey = CharacterDirectory.BuildNameWorldKey(name, homeWorldId);
			if (seenNameWorldKeys.Contains(nameWorldKey))
			{
				continue;
			}

			candidates.Add((cached.Key, cached.FetchedAtUtc));
			candidateKeys.Add(cached.Key);
		}

		foreach (var resolved in resolvedByKey.Values)
		{
			if (candidateKeys.Contains(resolved.Key))
			{
				continue;
			}

			var nameWorldKey = CharacterDirectory.BuildNameWorldKey(resolved.Name, resolved.HomeWorldId);
			if (seenNameWorldKeys.Contains(nameWorldKey))
			{
				continue;
			}

			candidates.Add((resolved.Key, resolved.ResolvedAtUtc));
			candidateKeys.Add(resolved.Key);
		}

		OldestFirstEviction.Trim(
			candidates, UnseenCap, _ => true, item => item.Timestamp,
			items => lodestoneCache.DeleteCharacters(items.Select(item => item.Key)));
	}
}
