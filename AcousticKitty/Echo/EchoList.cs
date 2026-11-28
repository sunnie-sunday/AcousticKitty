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
using AcousticKitty.Character;
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
	private readonly CharacterDirectory characterDirectory;
	private readonly Configuration configuration;
	private readonly string pluginVersion;
	private readonly IDataManager dataManager;
	private readonly IPluginLog log;
	private readonly ConcurrentQueue<(PinnedCharacter Pin, string WorldName)> pending;

	public ProfileRefresh(
		ILodestoneClient client,
		LodestoneCache lodestoneCache,
		EchoList echoList,
		CharacterDirectory characterDirectory,
		Configuration configuration,
		string pluginVersion,
		IDataManager dataManager,
		IPluginLog log)
	{
		this.client = client;
		this.lodestoneCache = lodestoneCache;
		this.characterDirectory = characterDirectory;
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
			this.RecordRenameIfChanged(pin, profile);
			this.Save(pin, profile);
			this.UpdateRowState(pin, NearbyLookupState.Found);
		}
		catch (LodestoneAccessRestrictedException)
		{
			this.UpdateRowState(pin, NearbyLookupState.AccessRestricted);
		}
		catch (LodestoneNotFoundException) when (this.TryGetPinRow(pin) is { } known)
		{
			this.lodestoneCache.SavePinnedProfileSnapshot(
				pin.LodestoneId, CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId),
				known.LookupState == NearbyLookupState.AccessRestricted);
			this.characterDirectory.SetLookupResult(
				known.Data.ContentId, pin.LodestoneId, NearbyLookupState.Deleted, null);
			this.log.Information($"{pin.Name} was deleted from The Lodestone ({pin.LodestoneId}).");
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

	private void UpdateRowState(PinnedCharacter pin, NearbyLookupState state)
	{
		if (this.TryGetPinRow(pin) is { } known &&
			known.LookupState != state &&
			(known.LodestoneId == null ||
				known.LookupState is NearbyLookupState.Found or NearbyLookupState.AccessRestricted
					or NearbyLookupState.Deleted))
		{
			this.characterDirectory.SetLookupResult(
				known.Data.ContentId, pin.LodestoneId, state, null);
		}
	}

	private void RecordRenameIfChanged(PinnedCharacter pin, LodestoneProfile profile)
	{
		if (this.TryGetPinRow(pin) is not { } known || known.Data.ContentId != pin.ContentId ||
			(known.Data.Name == profile.Name && known.Data.HomeWorldId == profile.HomeWorldId))
		{
			return;
		}

		CharacterTransferRecorder.Record(
			this.characterDirectory, this.lodestoneCache, this.dataManager,
			known.Data.ContentId, known.Data.Name, known.Data.HomeWorldId, known.LastSeenUtc,
			profile.Name, profile.HomeWorldId, pin.LodestoneId);
		this.characterDirectory.UpdateNameWorld(
			known.Data.ContentId, profile.Name, profile.HomeWorldId);
		this.characterDirectory.SetLookupResult(
			known.Data.ContentId, pin.LodestoneId, NearbyLookupState.Found, null);
	}

	private KnownCharacter? TryGetPinRow(PinnedCharacter pin) =>
		this.characterDirectory.TryGetByLodestoneId(pin.LodestoneId) ??
		(pin.ContentId is { } contentId &&
			this.characterDirectory.TryGetByContentId(contentId) is {
				LodestoneId: null,
				LookupState: NearbyLookupState.Pending or NearbyLookupState.NotFound
					or NearbyLookupState.Error,
			} unresolved
				? unresolved
				: null);

	private void Save(PinnedCharacter pin, LodestoneProfile profile)
	{
		var key = CharacterKey.Build(profile.Name, profile.HomeWorldId);
		var fetchedAtUtc = DateTime.UtcNow;
		this.lodestoneCache.SaveResolvedId(
			key, pin.LodestoneId, profile.Name, profile.HomeWorldId, fetchedAtUtc,
			profile.AvatarUrlHash, profile.FreeCompanyId, profile.FreeCompanyName,
			freeCompanyKnown: !profile.IsPartiallyPrivate);
		if (!this.lodestoneCache.SaveCachedProfile(key, profile, fetchedAtUtc))
		{
			this.log.Warning(ProfileRegressionGuard.LogMessage(profile.Name));
		}
	}
}
