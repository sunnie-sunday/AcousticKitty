// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;
using AcousticKitty.Echo;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace AcousticKitty.Character;

public sealed partial class NearbyCharactersService : IDisposable
{
	private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);

	private static readonly TimeSpan QueueRebuildInterval = TimeSpan.FromSeconds(1);

	private static readonly TimeSpan CapEnforcementInterval = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan ReuseHoldDuration = TimeSpan.FromHours(72);

	private static readonly TimeSpan NoWorkPollInterval = TimeSpan.FromSeconds(1);

	private const int MaxQueueSnapshotSize = 5_000;

	private readonly IPlayerState playerState;
	private readonly IObjectTable objectTable;
	private readonly IClientState clientState;
	private readonly IFramework framework;
	private readonly IDataManager dataManager;
	private readonly ILodestoneClient client;
	private readonly LodestoneCache lodestoneCache;
	private readonly EchoStore echoStore;
	private readonly CharacterDirectory characterDirectory;
	private readonly Configuration configuration;
	private readonly EchoService echo;
	private readonly GroupSearchService groupSearch;
	private readonly FreeCompanyIdIndex freeCompanyIdIndex;
	private readonly IPluginLog log;
	private readonly CancellationTokenSource disposalCts = new();

	private DateTime lastScanUtc = DateTime.MinValue;
	private DateTime lastQueueRebuildUtc = DateTime.MinValue;
	private DateTime lastCapEnforcementUtc = DateTime.MinValue;
	private int drainingFlag;
	private volatile bool isProcessingScan;
	private CancellationTokenSource? drainStopCts;

	private ulong activeContentIdOrZero;

	private volatile IReadOnlyList<NearbyMemberViewModel> currentlyNearby =
		Array.Empty<NearbyMemberViewModel>();
	private volatile IReadOnlyList<NearbyMemberViewModel> queue =
		Array.Empty<NearbyMemberViewModel>();

	#region Public API

	public NearbyCharactersService(
		IPlayerState playerState,
		IObjectTable objectTable,
		IClientState clientState,
		IFramework framework,
		IDataManager dataManager,
		ILodestoneClient client,
		LodestoneCache lodestoneCache,
		EchoStore echoStore,
		CharacterDirectory characterDirectory,
		Configuration configuration,
		EchoService echo,
		GroupSearchService groupSearch,
		FreeCompanyIdIndex freeCompanyIdIndex,
		IPluginLog log)
	{
		this.playerState = playerState;
		this.objectTable = objectTable;
		this.clientState = clientState;
		this.framework = framework;
		this.dataManager = dataManager;
		this.client = client;
		this.lodestoneCache = lodestoneCache;
		this.echoStore = echoStore;
		this.characterDirectory = characterDirectory;
		this.configuration = configuration;
		this.echo = echo;
		this.groupSearch = groupSearch;
		this.freeCompanyIdIndex = freeCompanyIdIndex;
		this.log = log;

		this.clientState.Logout += this.OnLogout;
		this.framework.Update += this.OnFrameworkUpdate;

		this.RebuildQueueSnapshot();
	}

	public IReadOnlyList<NearbyMemberViewModel> CurrentlyNearby => this.currentlyNearby;

	public IReadOnlyList<NearbyMemberViewModel> Queue => this.queue;

	public bool QueueMightHaveMore => this.queue.Count >= MaxQueueSnapshotSize;

	public IReadOnlyList<NameHistoryEntry> GetNameHistory(ulong contentId) =>
		this.characterDirectory.GetNameHistory(contentId);

	public bool IsDraining => this.drainingFlag != 0;

	public void RunSearchOnce()
	{
		if (Interlocked.CompareExchange(ref this.drainingFlag, 1, 0) != 0)
		{
			return;
		}

		_ = this.DrainQueueAsync();
	}

	public void StopSearch() => this.drainStopCts?.Cancel();

	public void QueueOverride(ulong contentId, ulong lodestoneId)
	{
		this.characterDirectory.QueueOverride(contentId, lodestoneId, DateTime.UtcNow);

		this.lastQueueRebuildUtc = DateTime.MinValue;
		this.RebuildQueueSnapshot();

		if (this.configuration.NearbySearchMode == NearbySearchMode.Auto)
		{
			this.RunSearchOnce();
		}
	}

	public void Dispose()
	{
		this.framework.Update -= this.OnFrameworkUpdate;
		this.clientState.Logout -= this.OnLogout;
		this.disposalCts.Cancel();
		this.disposalCts.Dispose();
	}

	#endregion

	#region Private Implementation

	private void OnLogout(int type, int code) =>
		this.currentlyNearby = Array.Empty<NearbyMemberViewModel>();

	private void OnFrameworkUpdate(IFramework unused)
	{
		if (!this.playerState.IsLoaded || this.objectTable.LocalPlayer is not { } localPlayer)
		{
			return;
		}

		var now = DateTime.UtcNow;
		if (now - this.lastScanUtc < ScanInterval || this.isProcessingScan)
		{
			return;
		}

		this.lastScanUtc = now;

		var territoryId = this.clientState.TerritoryType;
		var snapshots = new List<PlayerLocalData>();
		foreach (var gameObject in this.objectTable)
		{
			if (gameObject.ObjectKind != ObjectKind.Pc || gameObject.EntityId == localPlayer.EntityId)
			{
				continue;
			}

			if (gameObject is not IPlayerCharacter character ||
				NearbyCharactersService.SnapshotCharacter(character, territoryId) is not { } data ||
				data.ContentId == 0)
			{
				continue;
			}

			snapshots.Add(data);
		}

		this.isProcessingScan = true;
		_ = this.ProcessScanAsync(snapshots, now);
	}

	private async Task ProcessScanAsync(List<PlayerLocalData> snapshots, DateTime scanUtc)
	{
		try
		{
			await Task.Run(() => this.ProcessScan(snapshots, scanUtc), this.disposalCts.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			this.isProcessingScan = false;
		}
	}

	private void ProcessScan(List<PlayerLocalData> snapshots, DateTime scanUtc)
	{
		var rows = snapshots.Select(data =>
		{
			this.RecordRenameIfChanged(data);
			var nameWorldKey = CharacterDirectory.BuildNameWorldKey(data.Name, data.HomeWorldId);
			return (data.ContentId, nameWorldKey, data, scanUtc);
		}).ToArray();
		this.characterDirectory.UpsertSnapshots(rows);

		foreach (var data in snapshots)
		{
			this.HandleNameReuseIfDetected(data, scanUtc);
		}

		this.characterDirectory.PromoteMatureConflictHolds(scanUtc);

		var liveList = new List<NearbyMemberViewModel>(snapshots.Count);
		foreach (var data in snapshots)
		{
			if (this.characterDirectory.TryGetByContentId(data.ContentId) is { } known)
			{
				liveList.Add(this.BuildViewModel(known));
			}
		}

		this.currentlyNearby = liveList;
		this.RebuildQueueSnapshot();
		this.EnforceCapsIfDue();

		if (this.configuration.NearbySearchMode == NearbySearchMode.Auto)
		{
			this.RunSearchOnce();
		}
	}

	private void EnforceCapsIfDue()
	{
		var now = DateTime.UtcNow;
		if (now - this.lastCapEnforcementUtc < CapEnforcementInterval)
		{
			return;
		}

		this.lastCapEnforcementUtc = now;
		var cap = DatabaseCapEnforcer.ResolveCap(this.configuration.DatabaseCacheTier);
		DatabaseCapEnforcer.EnforceHidden(this.characterDirectory, cap);
		DatabaseCapEnforcer.EnforceUnseen(this.characterDirectory, this.lodestoneCache, cap);
		DatabaseCapEnforcer.EnforceVerified(
			this.characterDirectory, this.lodestoneCache, this.echoStore, cap);
	}

	private static unsafe PlayerLocalData? SnapshotCharacter(
		IPlayerCharacter character, uint territoryId)
	{
		if (!character.IsValid())
		{
			return null;
		}

		var name = character.Name.TextValue;
		if (string.IsNullOrEmpty(name))
		{
			return null;
		}

		var tag = character.CompanyTag.TextValue.Trim('«', '»', ' ');
		var freeCompanyTag = string.IsNullOrEmpty(tag) ? null : tag;

		var native = (NativeCharacter*)character.Address;

		return new PlayerLocalData(
			name,
			character.HomeWorld.RowId,
			native->ContentId,
			character.ClassJob.RowId,
			character.Level,
			freeCompanyTag,
			native->CharacterData.TitleId,
			territoryId,
			character.CurrentWorld.RowId,
			character.CustomizeData.Sex == 0 ? "M" : "F");
	}

	private void RecordRenameIfChanged(PlayerLocalData data)
	{
		var known = this.characterDirectory.TryGetByContentId(data.ContentId);
		if (known == null)
		{
			return;
		}

		if (known.Data.Name == data.Name && known.Data.HomeWorldId == data.HomeWorldId)
		{
			return;
		}

		CharacterTransferRecorder.Record(
			this.characterDirectory, this.lodestoneCache, this.dataManager,
			data.ContentId, known.Data.Name, known.Data.HomeWorldId, known.LastSeenUtc,
			data.Name, data.HomeWorldId, known.LodestoneId);
	}

	private void HandleNameReuseIfDetected(PlayerLocalData data, DateTime nowUtc)
	{
		var nameWorldKey = CharacterDirectory.BuildNameWorldKey(data.Name, data.HomeWorldId);
		var priorHolder = this.characterDirectory.TryGetActiveHolder(nameWorldKey, data.ContentId);
		if (priorHolder == null)
		{
			return;
		}

		var priorIsPinned =
			priorHolder.LodestoneId is { } priorId && this.echoStore.IsPinned(priorId);
		if (priorIsPinned)
		{
			var (profile, fetchedAtUtc) = this.lodestoneCache.ResolveProfileOrPartial(nameWorldKey);
			if (profile != null)
			{
				this.echoStore.SavePinnedProfileSnapshot(
					priorHolder.LodestoneId!.Value, profile, fetchedAtUtc);
			}

			this.characterDirectory.SetLookupResult(
				priorHolder.Data.ContentId, priorHolder.LodestoneId, NearbyLookupState.Transferred, null);
		}
		else
		{
			this.characterDirectory.DeleteMany(new[] { priorHolder.Data.ContentId });
			if (priorHolder.LodestoneId is { } verifiedId)
			{
				this.echoStore.DeleteVerified(new[] { verifiedId });
			}
		}

		if (this.characterDirectory.TryGetByContentId(data.ContentId)?.LodestoneId != null)
		{
			return;
		}

		this.lodestoneCache.DeleteCharacters(new[] { nameWorldKey });
		this.characterDirectory.SetLookupResult(
			data.ContentId, null, NearbyLookupState.ConflictHold, null,
			priorityAtUtc: nowUtc + ReuseHoldDuration);
	}

	private void RebuildQueueSnapshot()
	{
		var now = DateTime.UtcNow;
		if (now - this.lastQueueRebuildUtc < QueueRebuildInterval)
		{
			return;
		}

		this.lastQueueRebuildUtc = now;
		var stopwatch = Stopwatch.StartNew();
		var pending = this.characterDirectory.GetPending(MaxQueueSnapshotSize);
		this.queue = pending.Select(this.BuildQueueRowViewModel).ToArray();
		this.log.Verbose(
			$"Rebuilt Lodestone queue snapshot: {this.queue.Count} row(s) in " +
			$"{stopwatch.ElapsedMilliseconds} ms.");
	}

	private NearbyMemberViewModel BuildQueueRowViewModel(KnownCharacter known)
	{
		var worldName = GameDataResolver.ResolveWorldName(this.dataManager, known.Data.HomeWorldId);
		var isSearching = known.Data.ContentId == this.activeContentIdOrZero;
		return new NearbyMemberViewModel(
			known, false, null, null, isSearching, worldName, string.Empty, string.Empty);
	}

	private NearbyMemberViewModel BuildViewModel(KnownCharacter known)
	{
		var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
		var isPinned = known.LodestoneId is { } lodestoneId && this.echoStore.IsPinned(lodestoneId);
		var isVerified =
			known.LodestoneId is { } verifiedId && this.echoStore.IsVerified(verifiedId);

		LodestoneProfile? profile = null;
		DateTime? profileAsOfUtc = null;
		string? avatarUrlHash = null;

		if (known.LodestoneId != null)
		{
			(profile, profileAsOfUtc) = this.lodestoneCache.ResolveProfileOrPartial(cacheKey);
			avatarUrlHash = profile?.AvatarUrlHash ?? this.lodestoneCache.TryGetResolvedAvatarUrlHash(cacheKey);
		}

		var worldName = GameDataResolver.ResolveWorldName(this.dataManager, known.Data.HomeWorldId);
		var dataCenterName =
			GameDataResolver.ResolveDataCenterName(this.dataManager, known.Data.HomeWorldId);
		var jobAbbreviation = GameDataResolver.ResolveJobAbbreviation(this.dataManager, known.Data.JobId);

		var isSearching = known.Data.ContentId == this.activeContentIdOrZero;
		return new NearbyMemberViewModel(
			known, isPinned, profile, avatarUrlHash, isSearching, worldName, dataCenterName,
			jobAbbreviation, isVerified, profileAsOfUtc,
			known.LookupState == NearbyLookupState.ConflictHold);
	}

	private async Task DrainQueueAsync()
	{
		this.drainStopCts = CancellationTokenSource.CreateLinkedTokenSource(this.disposalCts.Token);
		try
		{
			while (true)
			{
				IReadOnlyList<KnownCharacter> pending;
				try
				{
					pending = this.characterDirectory.GetPending();
				}
				catch (Exception ex)
				{
					this.log.Error(ex, "Failed to read the Lodestone queue; retrying shortly.");
					await Task.Delay(NoWorkPollInterval, this.drainStopCts.Token).ConfigureAwait(false);
					continue;
				}

				if (pending.Count == 0)
				{
					await Task.Delay(NoWorkPollInterval, this.drainStopCts.Token).ConfigureAwait(false);
					continue;
				}

				foreach (var known in pending)
				{
					await this.ResolveOneAsync(known, this.drainStopCts.Token).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			this.drainStopCts?.Dispose();
			this.drainStopCts = null;
			this.drainingFlag = 0;
		}
	}

	private async Task ResolveOneAsync(KnownCharacter known, CancellationToken cancellationToken)
	{
		var contentId = known.Data.ContentId;
		this.activeContentIdOrZero = contentId;
		var worldName = GameDataResolver.ResolveWorldName(this.dataManager, known.Data.HomeWorldId);
		var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
		var overrideLodestoneId = known.OverrideLodestoneId;
		ulong? lodestoneId = null;
		try
		{
			MemberListEntry? searchEntry = null;
			LodestoneProfile? profile = null;
			if (overrideLodestoneId is { } overrideId)
			{
				profile = await this.client
					.GetProfileAsync(overrideId, this.dataManager, cancellationToken)
					.ConfigureAwait(false);
				if (profile.Name != known.Data.Name || profile.HomeWorldId != known.Data.HomeWorldId)
				{
					this.MarkHidden(known);
					return;
				}

				lodestoneId = overrideId;
			}
			else if (known.LodestoneId is { } existingId)
			{
				lodestoneId = existingId;
			}
			else if (this.lodestoneCache.TryGetResolvedId(cacheKey) is { } cachedId)
			{
				lodestoneId = cachedId;
			}
			else
			{
				searchEntry = await this.client
					.SearchCharacterAsync(known.Data.Name, worldName, this.dataManager, cancellationToken)
					.ConfigureAwait(false);
				if (searchEntry == null)
				{
					this.MarkHidden(known);
					return;
				}

				lodestoneId = searchEntry.CharacterId;
			}

			if (this.characterDirectory.TryGetByLodestoneId(lodestoneId.Value) is { } owner &&
				owner.Data.ContentId != contentId)
			{
				this.characterDirectory.SetLookupResult(
					contentId, null, NearbyLookupState.ConflictHold, null,
					priorityAtUtc: DateTime.UtcNow + ReuseHoldDuration);
				return;
			}

			this.lodestoneCache.SaveResolvedId(
				cacheKey, lodestoneId.Value, known.Data.Name, known.Data.HomeWorldId, DateTime.UtcNow,
				searchEntry?.AvatarUrlHash, searchEntry?.Level, searchEntry?.JobId,
				searchEntry?.FreeCompanyId, searchEntry?.FreeCompanyName);

			this.characterDirectory.SetLookupResult(contentId, lodestoneId, NearbyLookupState.Found, null);
			this.echo.NotifyMatchConfirmed();

			if (searchEntry != null)
			{
				this.freeCompanyIdIndex.Record(
					known.Data.FreeCompanyTag, known.Data.HomeWorldId, searchEntry.FreeCompanyId,
					searchEntry.FreeCompanyName);
			}

			try
			{
				profile ??= await this.client
					.GetProfileAsync(lodestoneId.Value, this.dataManager, cancellationToken)
					.ConfigureAwait(false);

				if (searchEntry != null)
				{
					profile = profile with
					{
						JobId = searchEntry.JobId,
						Level = searchEntry.Level,
						FreeCompanyId = searchEntry.FreeCompanyId ?? profile.FreeCompanyId,
						FreeCompanyName = searchEntry.FreeCompanyName ?? profile.FreeCompanyName,
					};
				}

				var saved = this.lodestoneCache.SaveCachedProfile(cacheKey, profile, DateTime.UtcNow);
				if (saved)
				{
					this.freeCompanyIdIndex.Record(
						known.Data.FreeCompanyTag, known.Data.HomeWorldId, profile.FreeCompanyId,
						profile.FreeCompanyName);
				}
				else
				{
					this.log.Warning(ProfileRegressionGuard.LogMessage(known.Data.Name));
				}
			}
			catch (LodestoneAccessRestrictedException)
			{
				this.characterDirectory.SetLookupResult(
					contentId, lodestoneId, NearbyLookupState.AccessRestricted, null);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				this.log.Warning(
					$"Failed to update {known.Data.Name}'s cached Lodestone profile: " +
					$"{ex.GetType()}: {ex.Message}");
			}
		}
		catch (LodestoneNotFoundException)
		{
			this.MarkHidden(known);
		}
		catch (LodestoneAccessRestrictedException)
		{
			this.MarkHidden(known);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			this.log.Error(ex, "Failed to resolve a nearby character's Lodestone profile.");
			this.characterDirectory.SetLookupResult(
				contentId, known.LodestoneId, NearbyLookupState.Pending, ex.Message,
				clearOverride: false);
		}
		finally
		{
			this.activeContentIdOrZero = 0;
			this.RebuildQueueSnapshot();
		}
	}

	private void MarkHidden(KnownCharacter known)
	{
		this.characterDirectory.SetLookupResult(
			known.Data.ContentId, null, NearbyLookupState.NotFound, null);
		this.TryForceResolveViaKnownFreeCompany(known);
	}

	private void TryForceResolveViaKnownFreeCompany(KnownCharacter known)
	{
		foreach (var (freeCompanyId, freeCompanyName) in this.freeCompanyIdIndex.GetFreeCompanies(
			known.Data.FreeCompanyTag, known.Data.HomeWorldId))
		{
			_ = this.groupSearch.ForceResolveFreeCompanyRosterAsync(freeCompanyId, freeCompanyName);
		}
	}

	#endregion
}
