// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;
using AcousticKitty.Echo;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Common;

public sealed class DatabaseOverviewService(
	LodestoneCache lodestoneCache,
	EchoStore echoStore,
	CharacterDirectory characterDirectory,
	IDataManager dataManager,
	IPlayerState playerState,
	GroupSearchService groupSearchService,
	FreeCompanyIdIndex freeCompanyIdIndex,
	Configuration configuration,
	EchoService echoService,
	IPluginLog log)
{
	private volatile IReadOnlyList<DatabaseEntryViewModel> pinned =
		Array.Empty<DatabaseEntryViewModel>();
	private volatile IReadOnlyList<DatabaseEntryViewModel> verified =
		Array.Empty<DatabaseEntryViewModel>();
	private volatile IReadOnlyList<DatabaseEntryViewModel> unverified =
		Array.Empty<DatabaseEntryViewModel>();
	private volatile IReadOnlyList<DatabaseEntryViewModel> hidden =
		Array.Empty<DatabaseEntryViewModel>();
	private volatile IReadOnlyList<DatabaseEntryViewModel> unseen =
		Array.Empty<DatabaseEntryViewModel>();

	private volatile bool isReloadingPinned;
	private volatile bool isReloadingVerified;
	private volatile bool isReloadingUnverified;
	private volatile bool isReloadingHidden;
	private volatile bool isReloadingUnseen;

	public IReadOnlyList<DatabaseEntryViewModel> Pinned => this.pinned;

	public IReadOnlyList<DatabaseEntryViewModel> Verified => this.verified;

	public IReadOnlyList<DatabaseEntryViewModel> Unverified => this.unverified;

	public IReadOnlyList<DatabaseEntryViewModel> Hidden => this.hidden;

	public IReadOnlyList<DatabaseEntryViewModel> Unseen => this.unseen;

	public bool IsReloadingPinned => this.isReloadingPinned;

	public bool IsReloadingVerified => this.isReloadingVerified;

	public bool IsReloadingUnverified => this.isReloadingUnverified;

	public bool IsReloadingHidden => this.isReloadingHidden;

	public bool IsReloadingUnseen => this.isReloadingUnseen;

	public void ReloadPinnedAsync() =>
		this.RunAsync(
			"Stalkers", () => this.isReloadingPinned, v => this.isReloadingPinned = v, this.ReloadPinnedCore);

	public void ReloadVerifiedAsync() =>
		this.RunAsync(
			"Verified", () => this.isReloadingVerified, v => this.isReloadingVerified = v,
			this.ReloadVerifiedCore);

	public void ReloadUnverifiedAsync() =>
		this.RunAsync(
			"Unverified", () => this.isReloadingUnverified, v => this.isReloadingUnverified = v,
			this.ReloadUnverifiedCore);

	public void ReloadHiddenAsync() =>
		this.RunAsync(
			"Hidden", () => this.isReloadingHidden, v => this.isReloadingHidden = v, this.ReloadHiddenCore);

	public void ReloadUnseenAsync() =>
		this.RunAsync(
			"Unseen", () => this.isReloadingUnseen, v => this.isReloadingUnseen = v, this.ReloadUnseenCore);

	private void RunAsync(
		string label, Func<bool> isRunning, Action<bool> setRunning, Action reloadCore)
	{
		if (isRunning())
		{
			return;
		}

		setRunning(true);
		_ = Task.Run(() =>
		{
			try
			{
				reloadCore();
			}
			catch (Exception ex)
			{
				log.Error(ex, $"Failed to reload Database {label} tab.");
			}
			finally
			{
				setRunning(false);
			}
		});
	}

	private void ReloadPinnedCore()
	{
		var stopwatch = Stopwatch.StartNew();
		var pinnedCharacters = echoStore.GetAllPinned();

		var knownByLodestoneId = new Dictionary<ulong, KnownCharacter?>();
		foreach (var pin in pinnedCharacters)
		{
			if (!knownByLodestoneId.ContainsKey(pin.LodestoneId))
			{
				knownByLodestoneId[pin.LodestoneId] =
					characterDirectory.TryGetByLodestoneId(pin.LodestoneId);
			}
		}

		var nameWorldKeys = knownByLodestoneId.Values
			.Where(known => known is { LookupState: not NearbyLookupState.Transferred })
			.Select(known => CharacterKey.Build(known!.Data.Name, known.Data.HomeWorldId))
			.Distinct()
			.ToList();
		var cachedProfilesByKey =
			lodestoneCache.GetCachedProfilesForKeys(nameWorldKeys).ToDictionary(cached => cached.Key);
		var resolvedByKey =
			lodestoneCache.GetResolvedForKeys(nameWorldKeys).ToDictionary(resolved => resolved.Key);

		var pinnedList = pinnedCharacters
			.OrderByDescending(pin => pin.PinnedAtUtc)
			.Select(pin =>
			{
				var known = knownByLodestoneId[pin.LodestoneId];
				var name = known?.Data.Name ?? pin.Name;
				var homeWorldId = known?.Data.HomeWorldId ?? pin.HomeWorldId;
				var cacheKey = CharacterKey.Build(name, homeWorldId);
				var isTransferred = known is { LookupState: NearbyLookupState.Transferred };
				var worldName = GameDataResolver.ResolveWorldName(dataManager, homeWorldId);
				var dataCenterName = GameDataResolver.ResolveDataCenterName(dataManager, homeWorldId);
				var nameHistory = known != null
					? DatabaseOverviewService.LazyNameHistory(characterDirectory, known.Data.ContentId)
					: null;

				Lazy<LodestoneProfile>? profile;
				DateTime? profileFetchedAtUtc;
				string? avatarUrlHash;
				if (isTransferred)
				{
					var snapshot = echoStore.TryGetPinnedProfileSnapshot(pin.LodestoneId);
					profile = snapshot is { } s ? new Lazy<LodestoneProfile>(() => s.Profile) : null;
					profileFetchedAtUtc = snapshot?.FetchedAtUtc;
					avatarUrlHash = snapshot?.Profile.AvatarUrlHash;
				}
				else
				{
					var cached = cachedProfilesByKey.GetValueOrDefault(cacheKey);
					var resolved = resolvedByKey.GetValueOrDefault(cacheKey);
					(profile, profileFetchedAtUtc) = LodestoneProfile.ResolveOrPartial(cached, resolved);
					avatarUrlHash = resolved?.AvatarUrlHash;
				}

				return new DatabaseEntryViewModel(
					cacheKey, name, worldName, true, profile, profileFetchedAtUtc,
					known?.Data, known?.LastSeenUtc, avatarUrlHash, dataCenterName,
					NameHistory: nameHistory, IsConflicted: isTransferred);
			}).ToArray();

		this.pinned = pinnedList;
		log.Verbose(
			$"Reloaded Database Pinned tab: {this.pinned.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private void ReloadVerifiedCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceVerified(
			characterDirectory, lodestoneCache, echoStore,
			DatabaseCapEnforcer.ResolveCap(configuration.DatabaseCacheTier));
		this.verified = this.BuildVerifiedOrUnverifiedList(wantVerified: true);
		log.Verbose(
			$"Reloaded Database Verified tab: {this.verified.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private void ReloadUnverifiedCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceUnverified(
			characterDirectory, lodestoneCache, echoStore, echoService.CurrentlyVerifyingContentId);
		this.unverified = this.BuildVerifiedOrUnverifiedList(wantVerified: false);
		log.Verbose(
			$"Reloaded Database Unverified tab: {this.unverified.Count} in " +
			$"{stopwatch.ElapsedMilliseconds} ms.");
	}

	private IReadOnlyList<DatabaseEntryViewModel> BuildVerifiedOrUnverifiedList(bool wantVerified)
	{
		var verifiedAtByLodestoneId = echoStore.GetAllVerified();
		var pinnedIds = echoStore.GetAllPinnedLodestoneIds(verifiedAtByLodestoneId.Keys.ToHashSet());

		var candidates = characterDirectory.GetFoundOrAccessRestricted()
			.Where(known =>
			{
				var isVerified = known.LodestoneId is { } id && verifiedAtByLodestoneId.ContainsKey(id);
				var isPinned = known.LodestoneId is { } pinId && pinnedIds.Contains(pinId);
				return wantVerified ? isVerified && !isPinned : !isVerified;
			})
			.ToList();

		var cacheKeys = candidates
			.Select(known => CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId))
			.ToList();
		var cachedProfilesByKey =
			lodestoneCache.GetCachedProfilesForKeys(cacheKeys).ToDictionary(cached => cached.Key);
		var resolvedByKey =
			lodestoneCache.GetResolvedForKeys(cacheKeys).ToDictionary(resolved => resolved.Key);

		var entries = candidates.Select(known =>
		{
			var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
			var worldName = GameDataResolver.ResolveWorldName(dataManager, known.Data.HomeWorldId);
			var dataCenterName =
				GameDataResolver.ResolveDataCenterName(dataManager, known.Data.HomeWorldId);
			cachedProfilesByKey.TryGetValue(cacheKey, out var cached);
			resolvedByKey.TryGetValue(cacheKey, out var resolved);
			var isPinned = known.LodestoneId is { } pinId && pinnedIds.Contains(pinId);
			var sortKey = wantVerified && known.LodestoneId is { } sortId
				? verifiedAtByLodestoneId.GetValueOrDefault(sortId)
				: known.LastSeenUtc;

			var (profile, profileFetchedAtUtc) = LodestoneProfile.ResolveOrPartial(cached, resolved);
			return (Entry: new DatabaseEntryViewModel(
				cacheKey, known.Data.Name, worldName, isPinned,
				profile, profileFetchedAtUtc, known.Data, known.LastSeenUtc,
				resolved?.AvatarUrlHash, dataCenterName,
				NameHistory: DatabaseOverviewService.LazyNameHistory(
					characterDirectory, known.Data.ContentId),
				IsVerified: wantVerified), SortKey: sortKey);
		});

		return entries.OrderByDescending(entry => entry.SortKey).Select(entry => entry.Entry).ToArray();
	}

	private void ReloadHiddenCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceHidden(
			characterDirectory, DatabaseCapEnforcer.ResolveCap(configuration.DatabaseCacheTier));

		var hiddenKnown = characterDirectory.GetHidden();
		var cacheKeys = hiddenKnown
			.Select(known => CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId))
			.ToList();
		var resolvedByKey =
			lodestoneCache.GetResolvedForKeys(cacheKeys).ToDictionary(resolved => resolved.Key);

		var hiddenList = hiddenKnown
			.OrderByDescending(known => known.LastSeenUtc)
			.Select(known =>
			{
				var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
				var worldName = GameDataResolver.ResolveWorldName(dataManager, known.Data.HomeWorldId);
				var dataCenterName =
					GameDataResolver.ResolveDataCenterName(dataManager, known.Data.HomeWorldId);
				resolvedByKey.TryGetValue(cacheKey, out var resolved);
				return new DatabaseEntryViewModel(
					cacheKey, known.Data.Name, worldName, false,
					null, null, known.Data, known.LastSeenUtc, resolved?.AvatarUrlHash, dataCenterName,
					NameHistory: DatabaseOverviewService.LazyNameHistory(
						characterDirectory, known.Data.ContentId));
			}).ToArray();

		this.hidden = hiddenList;

		foreach (var known in hiddenKnown)
		{
			foreach (var (freeCompanyId, freeCompanyName) in freeCompanyIdIndex.GetFreeCompanies(
				known.Data.FreeCompanyTag, known.Data.HomeWorldId))
			{
				_ = groupSearchService.ForceResolveFreeCompanyRosterAsync(freeCompanyId, freeCompanyName);
			}
		}

		log.Verbose(
			$"Reloaded Database Hidden tab: {this.hidden.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private void ReloadUnseenCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceUnseen(
			characterDirectory, lodestoneCache,
			DatabaseCapEnforcer.ResolveCap(configuration.DatabaseCacheTier));

		var seenNameWorldKeys = new HashSet<string>(characterDirectory.GetAllNameWorldKeys());
		var resolvedByKey = lodestoneCache.GetAllResolved().ToDictionary(resolved => resolved.Key);

		var ownNameWorldKey = playerState.IsLoaded
			? CharacterKey.Build(playerState.CharacterName, playerState.HomeWorld.RowId)
			: null;

		this.unseen = UnseenCandidates
			.Enumerate(
				lodestoneCache.GetAllCachedProfiles(), resolvedByKey, seenNameWorldKeys, ownNameWorldKey)
			.Select(this.BuildUnseenEntry)
			.OrderByDescending(item => item.Timestamp)
			.Select(item => item.Entry)
			.ToArray();
		log.Verbose(
			$"Reloaded Database Unseen tab: {this.unseen.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private (DatabaseEntryViewModel Entry, DateTime Timestamp) BuildUnseenEntry(UnseenRow row)
	{
		var worldName = GameDataResolver.ResolveWorldName(dataManager, row.HomeWorldId);
		var dataCenterName = GameDataResolver.ResolveDataCenterName(dataManager, row.HomeWorldId);

		if (row.Cached is { } cached)
		{
			var resolved = row.Resolved;
			var jobAbbreviation = resolved?.JobId is { } jobId
				? GameDataResolver.ResolveJobAbbreviation(dataManager, jobId)
				: null;
			var entry = new DatabaseEntryViewModel(
				row.Key, row.Name, worldName, false,
				cached.Profile, cached.FetchedAtUtc, null, null,
				resolved?.AvatarUrlHash, dataCenterName, jobAbbreviation,
				resolved?.Level?.ToString());
			return (entry, row.Timestamp);
		}

		var onlyResolved = row.Resolved!;
		var resolvedJobAbbreviation = onlyResolved.JobId is { } resolvedJobId
			? GameDataResolver.ResolveJobAbbreviation(dataManager, resolvedJobId)
			: null;
		var (profile, profileFetchedAtUtc) = LodestoneProfile.ResolveOrPartial(null, onlyResolved);
		var resolvedEntry = new DatabaseEntryViewModel(
			row.Key, onlyResolved.Name, worldName, false,
			profile, profileFetchedAtUtc, null, null, onlyResolved.AvatarUrlHash, dataCenterName,
			resolvedJobAbbreviation, onlyResolved.Level?.ToString());
		return (resolvedEntry, row.Timestamp);
	}

	private static Lazy<IReadOnlyList<NameHistoryEntry>> LazyNameHistory(
		CharacterDirectory characterDirectory, ulong contentId) =>
		new(() => characterDirectory.GetNameHistory(contentId));
}
