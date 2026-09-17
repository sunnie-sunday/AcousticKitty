// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AcousticKitty.Character;
using AcousticKitty.Common;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Lodestone;

internal static class AvatarUrlBuilder
{
	private const string DefaultAvatarHash = "c21f969b5f03d33d43e04f8f136e7682";

	public static string? ExtractHash(string? scrapedAvatarUrl)
	{
		if (string.IsNullOrEmpty(scrapedAvatarUrl))
		{
			return null;
		}

		var fileName = scrapedAvatarUrl[(scrapedAvatarUrl.LastIndexOf('/') + 1)..];
		var underscoreIndex = fileName.IndexOf('_');
		return underscoreIndex > 0 ? fileName[..underscoreIndex] : null;
	}

	public static string? Build(string? avatarUrlHash, string? worldName)
	{
		if (string.IsNullOrEmpty(worldName))
		{
			return null;
		}

		var hash = string.IsNullOrEmpty(avatarUrlHash) ? DefaultAvatarHash : avatarUrlHash;
		var worldHash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(worldName)));
		return $"https://img2.finalfantasyxiv.com/f/{hash}_{worldHash}fc0.jpg";
	}
}

public sealed class AvatarTextureCache(ILodestoneClient client, ITextureProvider textureProvider)
	: IDisposable
{
	private const int MaxCachedTextures = 5_000;

	private readonly Dictionary<string, (Task<IDalamudTextureWrap?> Task, LinkedListNode<string> Node)>
		cache = new();

	private readonly LinkedList<string> usageOrder = new();
	private readonly object gate = new();

	public Task<IDalamudTextureWrap?> GetOrFetchAsync(string url, CancellationToken cancellationToken)
	{
		lock (this.gate)
		{
			if (this.cache.TryGetValue(url, out var existing))
			{
				this.usageOrder.Remove(existing.Node);
				this.usageOrder.AddFirst(existing.Node);
				return existing.Task;
			}

			var node = this.usageOrder.AddFirst(url);
			var task = this.FetchAsync(url, cancellationToken);
			this.cache[url] = (task, node);
			this.ForgetIfFailed(url, task);
			this.EvictOldestIfOverCapacity();
			return task;
		}
	}

	private async Task<IDalamudTextureWrap?> FetchAsync(
		string url,
		CancellationToken cancellationToken)
	{
		try
		{
			var bytes = await client
				.GetAvatarBytesAsync(url, highPriority: true, cancellationToken)
				.ConfigureAwait(false);
			return await textureProvider
				.CreateFromImageAsync(bytes, url, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	private void ForgetIfFailed(string url, Task<IDalamudTextureWrap?> task)
	{
		_ = task.ContinueWith(
			completed =>
			{
				if (completed.Status == TaskStatus.RanToCompletion && completed.Result != null)
				{
					return;
				}

				lock (this.gate)
				{
					if (this.cache.TryGetValue(url, out var current) && current.Task == task)
					{
						this.cache.Remove(url);
						this.usageOrder.Remove(current.Node);
					}
				}
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private void EvictOldestIfOverCapacity()
	{
		while (this.cache.Count > MaxCachedTextures && this.usageOrder.Last is { } oldest)
		{
			this.usageOrder.RemoveLast();
			if (this.cache.Remove(oldest.Value, out var evicted) && evicted.Task.IsCompletedSuccessfully)
			{
				evicted.Task.Result?.Dispose();
			}
		}
	}

	public void Dispose()
	{
		lock (this.gate)
		{
			foreach (var entry in this.cache.Values)
			{
				if (entry.Task.IsCompletedSuccessfully)
				{
					entry.Task.Result?.Dispose();
				}
			}

			this.cache.Clear();
			this.usageOrder.Clear();
		}
	}
}

public sealed class AvatarWorldHistoryService(
	ILodestoneClient client,
	CharacterDirectory characterDirectory,
	IDataManager dataManager,
	IPluginLog log)
{
	private byte[]? defaultAvatarBytes;
	private readonly SemaphoreSlim defaultAvatarGate = new(1, 1);

	public async Task DiscoverWorldHistoryAsync(
		ulong contentId, string? avatarUrlHash, string currentWorldName, CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(avatarUrlHash))
		{
			return;
		}

		byte[] defaultBytes;
		try
		{
			defaultBytes = await this.GetDefaultAvatarBytesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			log.Verbose($"AvatarWorldHistoryService: couldn't fetch the default-avatar reference: {ex.Message}");
			return;
		}

		var alreadyOnFile = characterDirectory.GetHistoryWorldNames(contentId).ToHashSet();
		var candidateWorlds = GameDataResolver.GetAllPublicWorldNames(dataManager)
			.Where(world => world != currentWorldName && !alreadyOnFile.Contains(world));

		foreach (var worldName in candidateWorlds)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (AvatarUrlBuilder.Build(avatarUrlHash, worldName) is not { } url)
			{
				continue;
			}

			byte[] probeBytes;
			try
			{
				probeBytes = await client
					.GetAvatarBytesAsync(url, highPriority: false, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				log.Verbose($"AvatarWorldHistoryService: probe of \"{worldName}\" failed: {ex.Message}");
				continue;
			}

			if (!probeBytes.AsSpan().SequenceEqual(defaultBytes))
			{
				characterDirectory.RecordNameHistory(contentId, string.Empty, worldName, DateTime.UtcNow);
			}
		}
	}

	private async Task<byte[]> GetDefaultAvatarBytesAsync(CancellationToken cancellationToken)
	{
		if (this.defaultAvatarBytes is { } cached)
		{
			return cached;
		}

		await this.defaultAvatarGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (this.defaultAvatarBytes is { } cachedAfterWait)
			{
				return cachedAfterWait;
			}

			var referenceUrl = AvatarUrlBuilder.Build(null, "Excalibur")!;
			var bytes = await client
				.GetAvatarBytesAsync(referenceUrl, highPriority: false, cancellationToken)
				.ConfigureAwait(false);
			this.defaultAvatarBytes = bytes;
			return bytes;
		}
		finally
		{
			this.defaultAvatarGate.Release();
		}
	}
}
