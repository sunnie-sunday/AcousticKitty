// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;
using Dalamud.Plugin.Services;
using SQLite;

namespace AcousticKitty.Echo;

public sealed class EchoList
{
	public const string FileName = "stalkers.db3";

	private readonly IReadOnlyDictionary<ulong, PinnedCharacter> byContentId;
	private readonly IReadOnlyDictionary<ulong, PinnedCharacter> byLodestoneId;
	private readonly IReadOnlySet<string> nameWorldKeys;

	public EchoList(string assemblyDirectory, IDataManager dataManager, IPluginLog log)
	{
		var pins = EchoList.Load(Path.Combine(assemblyDirectory, FileName), dataManager, log);
		this.All = pins;
		this.byContentId = pins
			.Where(pin => pin.ContentId is not null)
			.GroupBy(pin => pin.ContentId!.Value)
			.ToDictionary(group => group.Key, group => group.First());
		this.byLodestoneId = pins
			.GroupBy(pin => pin.LodestoneId)
			.ToDictionary(group => group.Key, group => group.First());
		this.nameWorldKeys = pins.Select(pin => pin.NameWorldKey).ToHashSet();
		log.Information($"Loaded {pins.Count} pinned character(s) from {FileName}.");
	}

	public IReadOnlyList<PinnedCharacter> All { get; }

	public bool IsPinned(ulong contentId, ulong? lodestoneId) =>
		this.byContentId.ContainsKey(contentId) ||
		(lodestoneId is { } id && this.byLodestoneId.ContainsKey(id));

	public bool IsPinnedLodestoneId(ulong lodestoneId) =>
		this.byLodestoneId.ContainsKey(lodestoneId);

	public bool IsPinnedNameWorldKey(string nameWorldKey) =>
		this.nameWorldKeys.Contains(nameWorldKey);

	public PinnedCharacter? TryGetByContentId(ulong contentId) =>
		this.byContentId.GetValueOrDefault(contentId);

	private static IReadOnlyList<PinnedCharacter> Load(
		string databasePath, IDataManager dataManager, IPluginLog log)
	{
		if (!File.Exists(databasePath))
		{
			log.Warning($"{FileName} is missing next to the plugin; no character is pinned.");
			return Array.Empty<PinnedCharacter>();
		}

		List<PinRow> rows;
		try
		{
			var connectionString =
				new SQLiteConnectionString(databasePath, SQLiteOpenFlags.ReadOnly, true);
			using var connection = new SQLiteConnection(connectionString);
			rows = connection.Table<PinRow>().ToList();
		}
		catch (Exception ex)
		{
			log.Error(ex, $"Failed to read {FileName}; no character is pinned.");
			return Array.Empty<PinnedCharacter>();
		}

		var pins = new List<PinnedCharacter>(rows.Count);
		foreach (var row in rows)
		{
			if (GameDataResolver.TryResolveWorldId(dataManager, row.HomeWorld) is not { } worldId)
			{
				log.Warning($"{FileName}: skipping {row.Name}, unknown world '{row.HomeWorld}'.");
				continue;
			}

			pins.Add(new PinnedCharacter(
				row.ContentId is { } contentId ? (ulong)contentId : null,
				(ulong)row.LodestoneId,
				CharacterKey.Build(row.Name, worldId),
				row.Name,
				worldId,
				row.AvatarUrlHash,
				GameDataResolver.TryResolveJobId(dataManager, row.Job),
				row.Level,
				row.FreeCompanyId is { } freeCompanyId ? (ulong)freeCompanyId : null,
				row.FreeCompanyName));
		}

		return pins;
	}
}

public sealed class ProfileRefresh
{
	private readonly ILodestoneClient client;
	private readonly LodestoneCache lodestoneCache;
	private readonly Configuration configuration;
	private readonly string pluginVersion;
	private readonly IDataManager dataManager;
	private readonly IPluginLog log;
	private readonly ConcurrentQueue<(PinnedCharacter Pin, string WorldName)> pending;

	public ProfileRefresh(
		ILodestoneClient client,
		LodestoneCache lodestoneCache,
		EchoList echoList,
		Configuration configuration,
		string pluginVersion,
		IDataManager dataManager,
		IPluginLog log)
	{
		this.client = client;
		this.lodestoneCache = lodestoneCache;
		this.configuration = configuration;
		this.pluginVersion = pluginVersion;
		this.dataManager = dataManager;
		this.log = log;
		this.pending = new ConcurrentQueue<(PinnedCharacter, string)>(
			configuration.PinnedProfilesRefreshedFor == pluginVersion
				? []
				: echoList.All.Select(pin =>
					(pin, GameDataResolver.ResolveWorldName(dataManager, pin.HomeWorldId))));
	}

	public int PendingCount => this.pending.Count;

	public IReadOnlyList<(PinnedCharacter Pin, string WorldName)> Pending => this.pending.ToArray();

	public async Task<bool> TryRefreshNextAsync(CancellationToken cancellationToken)
	{
		if (!this.pending.TryPeek(out var next))
		{
			return false;
		}

		var pin = next.Pin;

		try
		{
			var profile = await this.client
				.GetProfileAsync(pin.LodestoneId, this.dataManager, cancellationToken)
				.ConfigureAwait(false);
			this.Save(pin, profile);
		}
		catch (LodestoneUnavailableException)
		{
			return true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			this.log.Warning(
				$"Failed to refresh pinned character {pin.Name}'s Lodestone profile: " +
				$"{ex.GetType()}: {ex.Message}");
		}

		this.pending.TryDequeue(out _);
		if (this.pending.IsEmpty)
		{
			this.configuration.PinnedProfilesRefreshedFor = this.pluginVersion;
			this.configuration.Save();
			this.log.Information("Refreshed the pinned characters' Lodestone profiles.");
		}

		return true;
	}

	private void Save(PinnedCharacter pin, LodestoneProfile profile)
	{
		var key = CharacterKey.Build(profile.Name, profile.HomeWorldId);
		var fetchedAtUtc = DateTime.UtcNow;
		this.lodestoneCache.SaveResolvedId(
			key, pin.LodestoneId, profile.Name, profile.HomeWorldId, fetchedAtUtc,
			profile.AvatarUrlHash, profile.Level, profile.JobId, profile.FreeCompanyId,
			profile.FreeCompanyName);
		if (!this.lodestoneCache.SaveCachedProfile(key, profile, fetchedAtUtc))
		{
			this.log.Warning(ProfileRegressionGuard.LogMessage(profile.Name));
		}
	}
}
