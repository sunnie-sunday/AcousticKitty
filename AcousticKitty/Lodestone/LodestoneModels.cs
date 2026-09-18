// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;

namespace AcousticKitty.Lodestone;

public sealed record LodestoneProfile(
	ulong LodestoneId,
	string Name,
	uint? TitleId,
	uint HomeWorldId,
	string? AvatarUrlHash,
	bool IsPartiallyPrivate,
	string Gender,
	ulong? FreeCompanyId,
	string? FreeCompanyName,
	uint? JobId = null,
	int? Level = null)
{
	public static LodestoneProfile BuildPartial(
		ulong lodestoneId, string name, uint homeWorldId, string? avatarUrlHash,
		ulong? freeCompanyId, string? freeCompanyName, uint? jobId, int? level) =>
		new(lodestoneId, name, TitleId: null, homeWorldId, avatarUrlHash, IsPartiallyPrivate: true,
			Gender: string.Empty, freeCompanyId, freeCompanyName, jobId, level);

	public static (Lazy<LodestoneProfile>? Profile, DateTime? FetchedAtUtc) ResolveOrPartial(
		CachedProfileEntry? cached, ResolvedCharacterEntry? resolved)
	{
		if (cached != null)
		{
			return (cached.Profile, cached.FetchedAtUtc);
		}

		if (resolved == null)
		{
			return (null, null);
		}

		var partial = LodestoneProfile.BuildPartial(
			resolved.LodestoneId, resolved.Name, resolved.HomeWorldId, resolved.AvatarUrlHash,
			resolved.FreeCompanyId, resolved.FreeCompanyName, resolved.JobId, resolved.Level);
		return (new Lazy<LodestoneProfile>(() => partial), null);
	}
}

public enum SocialGroupKind
{
	FreeCompany,
	Linkshell,
	CrossWorldLinkshell,
	PvpTeam,
}

public enum GroupSearchMode
{
	Name,
	Id,
}

public sealed record GroupSearchResult(
	string Id, string Name, uint? WorldId, uint DataCenterId, IReadOnlyList<string> IconUrls);

public sealed record GroupRoster(string? FreeCompanyName, IReadOnlyList<MemberListEntry> Members);

public sealed record CachedProfileEntry(
	string Key, Lazy<LodestoneProfile> Profile, DateTime FetchedAtUtc);

public sealed record ResolvedCharacterEntry(
	string Key,
	string Name,
	uint HomeWorldId,
	ulong LodestoneId,
	DateTime ResolvedAtUtc,
	string? AvatarUrlHash,
	int? Level,
	uint? JobId,
	ulong? FreeCompanyId = null,
	string? FreeCompanyName = null);

public sealed record MemberListEntry(
	ulong CharacterId,
	string Name,
	uint HomeWorldId,
	string? AvatarUrlHash,
	int? Level,
	uint? JobId,
	ulong? FreeCompanyId,
	string? FreeCompanyName);

public sealed class LodestoneDataResolutionException(string scrapedText)
	: Exception($"Could not resolve \"{scrapedText}\" to any known game data.");

public sealed class LodestoneNotFoundException()
	: Exception("The Lodestone returned 404 for this character.");

public sealed class LodestoneAccessRestrictedException()
	: Exception("This character has made their Lodestone profile private.");

public sealed class LodestoneMaintenanceException(TimeSpan? retryAfter)
	: Exception("The Lodestone is currently undergoing maintenance.")
{
	public TimeSpan? RetryAfter { get; } = retryAfter;
}
