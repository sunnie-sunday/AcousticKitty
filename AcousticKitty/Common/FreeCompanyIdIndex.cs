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

public sealed class FreeCompanyIdIndex
{
	private readonly object gate = new();
	private readonly Dictionary<(string Tag, uint HomeWorldId), Dictionary<ulong, string>> index = new();

	public FreeCompanyIdIndex(CharacterDirectory characterDirectory, LodestoneCache lodestoneCache)
	{
		var tagged = characterDirectory.GetAllWithFreeCompanyTag();
		var keys = tagged
			.Select(known => CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId))
			.ToArray();
		var profilesByKey = lodestoneCache.GetCachedProfilesForKeys(keys)
			.ToDictionary(entry => entry.Key);

		foreach (var known in tagged)
		{
			var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
			if (profilesByKey.TryGetValue(cacheKey, out var cached))
			{
				this.Record(
					known.Data.FreeCompanyTag, known.Data.HomeWorldId,
					cached.Profile.Value.FreeCompanyId, cached.Profile.Value.FreeCompanyName);
			}
		}
	}

	public void Record(
		string? freeCompanyTag, uint homeWorldId, ulong? freeCompanyId, string? freeCompanyName)
	{
		if (string.IsNullOrEmpty(freeCompanyTag) || freeCompanyId is not { } id || freeCompanyName == null)
		{
			return;
		}

		lock (this.gate)
		{
			if (!this.index.TryGetValue((freeCompanyTag, homeWorldId), out var byId))
			{
				byId = new Dictionary<ulong, string>();
				this.index[(freeCompanyTag, homeWorldId)] = byId;
			}

			byId[id] = freeCompanyName;
		}
	}

	public IReadOnlyList<(ulong FreeCompanyId, string FreeCompanyName)> GetFreeCompanies(
		string? freeCompanyTag, uint homeWorldId)
	{
		if (string.IsNullOrEmpty(freeCompanyTag))
		{
			return Array.Empty<(ulong, string)>();
		}

		lock (this.gate)
		{
			return this.index.TryGetValue((freeCompanyTag, homeWorldId), out var byId)
				? byId.Select(pair => (pair.Key, pair.Value)).ToArray()
				: Array.Empty<(ulong, string)>();
		}
	}
}
