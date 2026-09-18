// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AcousticKitty.Common;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Lodestone;

#region Interface

public interface ILodestoneClient
{
	Task<MemberListEntry?> SearchCharacterAsync(
		string characterName,
		string worldName,
		IDataManager dataManager,
		CancellationToken cancellationToken);

	Task<ulong?> SearchCharacterIdAsync(
		string characterName,
		string worldName,
		IDataManager dataManager,
		CancellationToken cancellationToken);

	Task<LodestoneProfile> GetProfileAsync(
		ulong lodestoneId,
		IDataManager dataManager,
		CancellationToken cancellationToken);

	Task<IReadOnlyList<GroupSearchResult>> SearchGroupAsync(
		SocialGroupKind kind,
		string name,
		string? worldOrDataCenter,
		IDataManager dataManager,
		CancellationToken cancellationToken);

	Task<GroupRoster> GetGroupRosterAsync(
		SocialGroupKind kind,
		string groupId,
		int maxPages,
		IDataManager dataManager,
		Action<IReadOnlyList<MemberListEntry>, string?> onPageFetched,
		CancellationToken cancellationToken);

	Task<byte[]> GetAvatarBytesAsync(string avatarUrl, bool highPriority, CancellationToken cancellationToken);
}

#endregion

public sealed class LodestoneClient(IPluginLog log)
	: ILodestoneClient, IDisposable
{
	private const string Host = "na.finalfantasyxiv.com";

	private const string UserAgent =
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
		"Chrome/152.0.0.0 Safari/537.36 Edg/152.0.4191.66";

	private readonly RateLimiter rateLimiter = new(TimeSpan.FromSeconds(1));

	private readonly RateLimiter avatarRateLimiter = new(TimeSpan.FromMilliseconds(20));

	private HttpClient? cachedClient;

	#region Endpoints

	public async Task<MemberListEntry?> SearchCharacterAsync(
		string characterName,
		string worldName,
		IDataManager dataManager,
		CancellationToken cancellationToken)
	{
		var url = $"https://{Host}/lodestone/character/" +
			$"?q={Uri.EscapeDataString(characterName)}&worldname={Uri.EscapeDataString(worldName)}";
		var html = await this.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
		return LodestoneParser.FindCharacterEntry(dataManager, html, characterName, worldName);
	}

	public async Task<ulong?> SearchCharacterIdAsync(
		string characterName,
		string worldName,
		IDataManager dataManager,
		CancellationToken cancellationToken) =>
		(await this.SearchCharacterAsync(characterName, worldName, dataManager, cancellationToken)
			.ConfigureAwait(false))
		?.CharacterId;

	public async Task<LodestoneProfile> GetProfileAsync(
		ulong lodestoneId,
		IDataManager dataManager,
		CancellationToken cancellationToken)
	{
		var url = $"https://{Host}/lodestone/character/{lodestoneId}/";
		var html = await this.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
		return LodestoneParser.ParseProfile(dataManager, html, lodestoneId);
	}

	public async Task<IReadOnlyList<GroupSearchResult>> SearchGroupAsync(
		SocialGroupKind kind,
		string name,
		string? worldOrDataCenter,
		IDataManager dataManager,
		CancellationToken cancellationToken)
	{
		var url = $"https://{Host}/lodestone/{GetGroupUrlSegment(kind)}/" +
			$"?q={Uri.EscapeDataString(name)}";
		if (!string.IsNullOrWhiteSpace(worldOrDataCenter))
		{
			var paramName = kind is SocialGroupKind.CrossWorldLinkshell or SocialGroupKind.PvpTeam
				? "dcname"
				: "worldname";
			url += $"&{paramName}={Uri.EscapeDataString($"_dc_{worldOrDataCenter}")}";
		}

		var html = await this.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
		return LodestoneParser.ParseGroupSearchResults(dataManager, html, kind);
	}

	public async Task<GroupRoster> GetGroupRosterAsync(
		SocialGroupKind kind,
		string groupId,
		int maxPages,
		IDataManager dataManager,
		Action<IReadOnlyList<MemberListEntry>, string?> onPageFetched,
		CancellationToken cancellationToken)
	{
		var firstPageHtml = await this
			.GetGroupMemberPageHtmlAsync(kind, groupId, 1, cancellationToken)
			.ConfigureAwait(false);

		var freeCompanyName = kind == SocialGroupKind.FreeCompany
			? LodestoneParser.ParseFreeCompanyName(firstPageHtml)
			: null;

		var firstPageMembers = LodestoneParser.ParseMemberListEntries(dataManager, firstPageHtml);
		var members = new List<MemberListEntry>(firstPageMembers);
		onPageFetched(firstPageMembers, freeCompanyName);

		var totalPages = Math.Min(LodestoneParser.ParsePageCount(firstPageHtml), maxPages);
		for (var page = 2; page <= totalPages; page++)
		{
			var html = await this
				.GetGroupMemberPageHtmlAsync(kind, groupId, page, cancellationToken)
				.ConfigureAwait(false);
			var pageMembers = LodestoneParser.ParseMemberListEntries(dataManager, html);
			members.AddRange(pageMembers);
			onPageFetched(pageMembers, freeCompanyName);
		}

		return new GroupRoster(freeCompanyName, members);
	}

	private Task<string> GetGroupMemberPageHtmlAsync(
		SocialGroupKind kind,
		string groupId,
		int page,
		CancellationToken cancellationToken)
	{
		var memberSegment = kind == SocialGroupKind.FreeCompany ? "member/" : string.Empty;
		var url = $"https://{Host}/lodestone/{GetGroupUrlSegment(kind)}/{groupId}/" +
			$"{memberSegment}?page={page}";
		return this.GetStringAsync(url, cancellationToken);
	}

	public async Task<byte[]> GetAvatarBytesAsync(
		string avatarUrl,
		bool highPriority,
		CancellationToken cancellationToken)
	{
		await this.avatarRateLimiter.WaitAsync(highPriority, cancellationToken).ConfigureAwait(false);

		var client = this.GetOrCreateHttpClient();
		using var response = await client.GetAsync(avatarUrl, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
	}

	#endregion

	#region Plumbing

	private static string GetGroupUrlSegment(SocialGroupKind kind) => kind switch
	{
		SocialGroupKind.FreeCompany => "freecompany",
		SocialGroupKind.Linkshell => "linkshell",
		SocialGroupKind.CrossWorldLinkshell => "crossworld_linkshell",
		SocialGroupKind.PvpTeam => "pvpteam",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
	};

	private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
	{
		await this.rateLimiter.WaitAsync(highPriority: true, cancellationToken).ConfigureAwait(false);

		var client = this.GetOrCreateHttpClient();
		var stopwatch = Stopwatch.StartNew();
		using var response = await client
			.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
			.ConfigureAwait(false);

		try
		{
			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				throw new LodestoneNotFoundException();
			}

			if (response.StatusCode == HttpStatusCode.Forbidden)
			{
				throw new LodestoneAccessRestrictedException();
			}

			if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
			{
				throw new LodestoneMaintenanceException(response.Headers.RetryAfter?.Delta);
			}

			response.EnsureSuccessStatusCode();
			return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			log.Debug(
				$"Lodestone GET {url} -> {(int)response.StatusCode} {response.ReasonPhrase} " +
				$"({stopwatch.ElapsedMilliseconds} ms)");
		}
	}

	private HttpClient GetOrCreateHttpClient()
	{
		if (this.cachedClient != null)
		{
			return this.cachedClient;
		}

		var handler = new SocketsHttpHandler
		{
			AutomaticDecompression = DecompressionMethods.All,
			ConnectTimeout = TimeSpan.FromSeconds(15),
		};
		var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd(LodestoneClient.UserAgent);
		client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

		this.cachedClient = client;
		return client;
	}

	public void Dispose()
	{
		this.cachedClient?.Dispose();
	}

	#endregion
}
