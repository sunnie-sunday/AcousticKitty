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

	Task<byte[]> GetAvatarBytesAsync(string avatarUrl, bool highPriority, CancellationToken cancellationToken);
}

#endregion

public sealed class LodestoneClient(Configuration configuration, IPluginLog log)
	: ILodestoneClient, IDisposable
{
	private const string Host = "na.finalfantasyxiv.com";

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
			log.Verbose(
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

		var userAgent = string.IsNullOrWhiteSpace(configuration.LodestoneUserAgent)
			? Configuration.DefaultLodestoneUserAgent
			: configuration.LodestoneUserAgent;
		var handler = new SocketsHttpHandler
		{
			AutomaticDecompression = DecompressionMethods.All,
			ConnectTimeout = TimeSpan.FromSeconds(15),
		};
		var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
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
