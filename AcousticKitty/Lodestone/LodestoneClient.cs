// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

	private const string HeavyTrafficText = "The server is experiencing unusually heavy traffic";

	private const string UserAgent =
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
		"Chrome/156.0.0.0 Safari/537.36 Edg/156.0.0.0";

	private static readonly TimeSpan MaintenanceBackoff = TimeSpan.FromMinutes(5);

	private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);

	private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

	private readonly RateLimiter rateLimiter = new(TimeSpan.FromSeconds(1));

	private readonly RateLimiter avatarRateLimiter = new(TimeSpan.FromMilliseconds(20));

	private readonly object clientGate = new();

	private readonly object backoffGate = new();

	private HttpClient? cachedClient;

	private DateTime pausedUntilUtc = DateTime.MinValue;

	private TimeSpan nextBackoff = InitialBackoff;

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
		await this.WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var body = await this.SendAsync(url, cancellationToken).ConfigureAwait(false);
			this.ResetBackoff();
			return body;
		}
		catch (LodestoneUnavailableException ex)
		{
			this.PauseRequests(ex);
			throw;
		}
	}

	private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			var remaining = this.GetPauseRemaining();
			if (remaining > TimeSpan.Zero)
			{
				await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
				continue;
			}

			await this.rateLimiter.WaitAsync(highPriority: true, cancellationToken)
				.ConfigureAwait(false);
			if (this.GetPauseRemaining() <= TimeSpan.Zero)
			{
				return;
			}
		}
	}

	private TimeSpan GetPauseRemaining()
	{
		lock (this.backoffGate)
		{
			return this.pausedUntilUtc - DateTime.UtcNow;
		}
	}

	private void PauseRequests(LodestoneUnavailableException ex)
	{
		TimeSpan delay;
		lock (this.backoffGate)
		{
			var now = DateTime.UtcNow;
			if (now < this.pausedUntilUtc)
			{
				return;
			}

			delay = ex.RetryAfter ?? (ex is LodestoneMaintenanceException
				? MaintenanceBackoff
				: this.nextBackoff);
			if (ex.RetryAfter == null && ex is not LodestoneMaintenanceException)
			{
				var doubled = this.nextBackoff * 2;
				this.nextBackoff = doubled < MaxBackoff ? doubled : MaxBackoff;
			}

			this.pausedUntilUtc = now + delay;
		}

		log.Warning(
			$"Pausing Lodestone requests for {delay.TotalSeconds:0} s: {ex.Message}");
	}

	private void ResetBackoff()
	{
		lock (this.backoffGate)
		{
			this.nextBackoff = InitialBackoff;
		}
	}

	private async Task<string> SendAsync(string url, CancellationToken cancellationToken)
	{
		var client = this.GetOrCreateHttpClient();
		var stopwatch = Stopwatch.StartNew();
		using var response = await LodestoneClient
			.WithNetworkErrorsAsUnavailableAsync(
				() => client.GetAsync(
					url, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);

		try
		{
			var body = await LodestoneClient
				.WithNetworkErrorsAsUnavailableAsync(
					() => response.Content.ReadAsStringAsync(cancellationToken), cancellationToken)
				.ConfigureAwait(false);
			LodestoneClient.ThrowIfFailed(response, body);
			return body;
		}
		finally
		{
			log.Debug(
				$"Lodestone GET {url} -> {(int)response.StatusCode} {response.ReasonPhrase} " +
				$"({stopwatch.ElapsedMilliseconds} ms)");
		}
	}

	private static async Task<T> WithNetworkErrorsAsUnavailableAsync<T>(
		Func<Task<T>> request, CancellationToken cancellationToken)
	{
		try
		{
			return await request().ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			throw new LodestoneUnavailableException(
				$"The Lodestone could not be reached: {ex.Message}", null, ex);
		}
		catch (IOException ex)
		{
			throw new LodestoneUnavailableException(
				$"The connection to the Lodestone was interrupted: {ex.Message}", null, ex);
		}
		catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
		{
			throw new LodestoneUnavailableException(
				"The Lodestone did not respond in time.", null, ex);
		}
	}

	// Error handling based on Simbiat/lodestone-parser: https://github.com/Simbiat/lodestone-parser
	private static void ThrowIfFailed(HttpResponseMessage response, string body)
	{
		if (response.IsSuccessStatusCode)
		{
			return;
		}

		var statusCode = (int)response.StatusCode;
		var retryAfter = LodestoneClient.GetRetryAfter(response);
		if (response.StatusCode == HttpStatusCode.TooManyRequests ||
			body.Contains(HeavyTrafficText, StringComparison.OrdinalIgnoreCase))
		{
			throw new LodestoneThrottledException(retryAfter);
		}

		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			throw new LodestoneNotFoundException();
		}

		if (response.StatusCode == HttpStatusCode.Forbidden)
		{
			throw LodestoneClient.IsLodestonePage(body)
				? new LodestoneAccessRestrictedException()
				: new LodestoneThrottledException(retryAfter);
		}

		if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
		{
			throw new LodestoneMaintenanceException(retryAfter);
		}

		if (statusCode >= 500)
		{
			throw new LodestoneUnavailableException(
				$"The Lodestone returned {statusCode} {response.ReasonPhrase}.", retryAfter);
		}

		response.EnsureSuccessStatusCode();
	}

	private static bool IsLodestonePage(string body) =>
		body.Contains("error__heading", StringComparison.Ordinal) ||
		body.Contains("parts__zero", StringComparison.Ordinal);

	private static TimeSpan? GetRetryAfter(HttpResponseMessage response) =>
		response.Headers.RetryAfter switch
		{
			{ Delta: { } delta } => delta,
			{ Date: { } date } when date > DateTimeOffset.UtcNow => date - DateTimeOffset.UtcNow,
			{ Date: not null } => TimeSpan.Zero,
			_ => null,
		};

	private HttpClient GetOrCreateHttpClient()
	{
		if (this.cachedClient != null)
		{
			return this.cachedClient;
		}

		lock (this.clientGate)
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
	}

	public void Dispose()
	{
		this.cachedClient?.Dispose();
	}

	#endregion
}
