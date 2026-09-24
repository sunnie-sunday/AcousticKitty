// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using AcousticKitty.Lodestone;

namespace AcousticKitty.Common;

internal readonly record struct UnseenRow(
	CachedProfileEntry? Cached,
	ResolvedCharacterEntry? Resolved,
	string Key,
	string Name,
	uint HomeWorldId,
	DateTime Timestamp);

internal static class UnseenCandidates
{
	public static IEnumerable<UnseenRow> Enumerate(
		IEnumerable<CachedProfileEntry> cachedProfiles,
		IReadOnlyDictionary<string, ResolvedCharacterEntry> resolvedByKey,
		ISet<string> seenNameWorldKeys,
		string? ownNameWorldKey)
	{
		var emitted = new HashSet<string>();

		foreach (var cached in cachedProfiles)
		{
			var hasResolved = resolvedByKey.TryGetValue(cached.Key, out var resolved);
			var name = hasResolved ? resolved!.Name : cached.Profile.Value.Name;
			var homeWorldId = hasResolved ? resolved!.HomeWorldId : cached.Profile.Value.HomeWorldId;
			var nameWorldKey = CharacterKey.Build(name, homeWorldId);
			if (seenNameWorldKeys.Contains(nameWorldKey) || nameWorldKey == ownNameWorldKey)
			{
				continue;
			}

			emitted.Add(cached.Key);
			yield return new UnseenRow(
				cached, hasResolved ? resolved : null, cached.Key, name, homeWorldId, cached.FetchedAtUtc);
		}

		foreach (var resolved in resolvedByKey.Values)
		{
			if (emitted.Contains(resolved.Key))
			{
				continue;
			}

			var nameWorldKey = CharacterKey.Build(resolved.Name, resolved.HomeWorldId);
			if (seenNameWorldKeys.Contains(nameWorldKey) || nameWorldKey == ownNameWorldKey)
			{
				continue;
			}

			yield return new UnseenRow(
				null, resolved, resolved.Key, resolved.Name, resolved.HomeWorldId, resolved.ResolvedAtUtc);
		}
	}
}
