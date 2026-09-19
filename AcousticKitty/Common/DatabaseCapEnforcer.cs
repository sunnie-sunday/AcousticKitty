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
	public const int UnverifiedCap = 5_000;

	public static int ResolveCap(DatabaseCacheTier tier) => tier switch
	{
		DatabaseCacheTier.Small => 12_500,
		DatabaseCacheTier.Medium => 25_000,
		DatabaseCacheTier.Large => 50_000,
		_ => 25_000,
	};

	public static IReadOnlyList<KnownCharacter> GetUnverifiedCandidates(
		CharacterDirectory characterDirectory, EchoStore echoStore)
	{
		var verifiedKeys = echoStore.GetAllVerifiedKeys();
		return characterDirectory.GetFoundOrAccessRestricted()
			.Where(known => known.LodestoneId != null &&
				!verifiedKeys.Contains(CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId)))
			.ToArray();
	}

	public static void EnforceUnverified(
		CharacterDirectory characterDirectory, LodestoneCache lodestoneCache, EchoStore echoStore,
		ulong? currentlyVerifyingContentId)
	{
		var pinnedKeys = new HashSet<string>(echoStore.GetAllPinned().Select(pin => pin.Key));
		var candidates = DatabaseCapEnforcer.GetUnverifiedCandidates(characterDirectory, echoStore)
			.Select(known => (
				known.Data.ContentId,
				CacheKey: CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId),
				known.LastSeenUtc))
			.ToList();

		OldestFirstEviction.Trim(
			candidates, UnverifiedCap,
			item => !pinnedKeys.Contains(item.CacheKey) && item.ContentId != currentlyVerifyingContentId,
			item => item.LastSeenUtc,
			items =>
			{
				characterDirectory.DeleteMany(items.Select(item => item.ContentId));
				lodestoneCache.DeleteCharacters(items.Select(item => item.CacheKey));
			});
	}

	public static void EnforceHidden(CharacterDirectory characterDirectory, int cap)
	{
		var candidates = characterDirectory.GetHidden().ToList();
		OldestFirstEviction.Trim(
			candidates, cap, _ => true, known => known.LastSeenUtc,
			items => characterDirectory.DeleteMany(items.Select(known => known.Data.ContentId)));
	}

	public static void EnforceUnseen(
		CharacterDirectory characterDirectory, LodestoneCache lodestoneCache, int cap)
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
			candidates, cap, _ => true, item => item.Timestamp,
			items => lodestoneCache.DeleteCharacters(items.Select(item => item.Key)));
	}

	public static void EnforceVerified(
		CharacterDirectory characterDirectory, LodestoneCache lodestoneCache, EchoStore echoStore,
		int cap)
	{
		var verifiedKeys = echoStore.GetAllVerifiedKeys();
		var pinnedKeys = new HashSet<string>(
			echoStore.GetAllPinned().Select(pin => pin.Key));

		var candidates = characterDirectory.GetFoundOrAccessRestricted()
			.Select(known => (
				known.Data.ContentId,
				CacheKey: CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId),
				known.LastSeenUtc))
			.Where(item => verifiedKeys.Contains(item.CacheKey))
			.ToList();

		OldestFirstEviction.Trim(
			candidates, cap, item => !pinnedKeys.Contains(item.CacheKey), item => item.LastSeenUtc,
			items =>
			{
				characterDirectory.DeleteMany(items.Select(item => item.ContentId));
				lodestoneCache.DeleteCharacters(items.Select(item => item.CacheKey));
				echoStore.DeleteVerified(items.Select(item => item.CacheKey));
			});
	}
}
