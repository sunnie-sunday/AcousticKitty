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
		DatabaseOverviewService.RunAsync(
			() => this.isReloadingPinned, v => this.isReloadingPinned = v, this.ReloadPinnedCore);

	public void ReloadVerifiedAsync() =>
		DatabaseOverviewService.RunAsync(
			() => this.isReloadingVerified, v => this.isReloadingVerified = v, this.ReloadVerifiedCore);

	public void ReloadUnverifiedAsync() =>
		DatabaseOverviewService.RunAsync(
			() => this.isReloadingUnverified, v => this.isReloadingUnverified = v, this.ReloadUnverifiedCore);

	public void ReloadHiddenAsync() =>
		DatabaseOverviewService.RunAsync(
			() => this.isReloadingHidden, v => this.isReloadingHidden = v, this.ReloadHiddenCore);

	public void ReloadUnseenAsync() =>
		DatabaseOverviewService.RunAsync(
			() => this.isReloadingUnseen, v => this.isReloadingUnseen = v, this.ReloadUnseenCore);

	private static void RunAsync(Func<bool> isRunning, Action<bool> setRunning, Action reloadCore)
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
		var pinnedKeys = pinnedCharacters.Select(pin => pin.Key).ToList();
		var cachedProfilesByKey =
			lodestoneCache.GetCachedProfilesForKeys(pinnedKeys).ToDictionary(cached => cached.Key);
		var resolvedByKey =
			lodestoneCache.GetResolvedForKeys(pinnedKeys).ToDictionary(resolved => resolved.Key);

		var pinnedList = pinnedCharacters
			.OrderByDescending(pin => pin.PinnedAtUtc)
			.Select(pin =>
			{
				var nameWorldKey = CharacterDirectory.BuildNameWorldKey(pin.Name, pin.HomeWorldId);
				var known = characterDirectory.TryGetByNameWorldKey(nameWorldKey);
				cachedProfilesByKey.TryGetValue(pin.Key, out var cached);
				resolvedByKey.TryGetValue(pin.Key, out var resolved);
				var worldName = GameDataResolver.ResolveWorldName(dataManager, pin.HomeWorldId);
				var dataCenterName = GameDataResolver.ResolveDataCenterName(dataManager, pin.HomeWorldId);
				var nameHistory = known != null
					? DatabaseOverviewService.LazyNameHistory(characterDirectory, known.Data.ContentId)
					: null;

				var (profile, profileFetchedAtUtc) = LodestoneProfile.ResolveOrPartial(cached, resolved);
				return new DatabaseEntryViewModel(
					pin.Key, pin.Name, worldName, true, profile, profileFetchedAtUtc,
					known?.Data, known?.LastSeenUtc, resolved?.AvatarUrlHash, dataCenterName,
					NameHistory: nameHistory);
			}).ToArray();

		this.pinned = pinnedList;
		log.Verbose(
			$"Reloaded Database Pinned tab: {this.pinned.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private void ReloadVerifiedCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceVerified(characterDirectory, lodestoneCache, echoStore);
		this.verified = this.BuildVerifiedOrUnverifiedList(wantVerified: true);
		log.Verbose(
			$"Reloaded Database Verified tab: {this.verified.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private void ReloadUnverifiedCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceUnverified(characterDirectory, lodestoneCache, echoStore);
		this.unverified = this.BuildVerifiedOrUnverifiedList(wantVerified: false);
		log.Verbose(
			$"Reloaded Database Unverified tab: {this.unverified.Count} in " +
			$"{stopwatch.ElapsedMilliseconds} ms.");
	}

	private IReadOnlyList<DatabaseEntryViewModel> BuildVerifiedOrUnverifiedList(bool wantVerified)
	{
		var verifiedAtByKey = echoStore.GetAllVerified();
		var pinnedKeys = new HashSet<string>(echoStore.GetAllPinned().Select(pin => pin.Key));

		var candidates = characterDirectory.GetFoundOrAccessRestricted()
			.Where(known =>
			{
				var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
				var isVerified = verifiedAtByKey.ContainsKey(cacheKey);
				return wantVerified
					? isVerified && !pinnedKeys.Contains(cacheKey)
					: !isVerified;
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
			var sortKey = wantVerified
				? verifiedAtByKey.GetValueOrDefault(cacheKey)
				: known.LastSeenUtc;

			var (profile, profileFetchedAtUtc) = LodestoneProfile.ResolveOrPartial(cached, resolved);
			return (Entry: new DatabaseEntryViewModel(
				cacheKey, known.Data.Name, worldName, pinnedKeys.Contains(cacheKey),
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
		DatabaseCapEnforcer.EnforceHidden(characterDirectory);

		var hiddenKnown = characterDirectory.GetHidden();
		var pinnedKeys = new HashSet<string>(echoStore.GetAllPinned().Select(pin => pin.Key));
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
					cacheKey, known.Data.Name, worldName, pinnedKeys.Contains(cacheKey),
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
		DatabaseCapEnforcer.EnforceUnseen(characterDirectory, lodestoneCache);

		var seenNameWorldKeys = new HashSet<string>(characterDirectory.GetAllNameWorldKeys());
		var pinnedKeys = new HashSet<string>(echoStore.GetAllPinned().Select(pin => pin.Key));
		var cachedProfilesByKey = lodestoneCache.GetAllCachedProfiles().ToDictionary(cached => cached.Key);
		var resolvedByKey = lodestoneCache.GetAllResolved().ToDictionary(resolved => resolved.Key);

		var ownNameWorldKey = playerState.IsLoaded
			? CharacterDirectory.BuildNameWorldKey(playerState.CharacterName, playerState.HomeWorld.RowId)
			: null;
		var unseenEntries = this.BuildUnseenList(
			cachedProfilesByKey, resolvedByKey, seenNameWorldKeys, pinnedKeys, ownNameWorldKey);

		this.unseen = unseenEntries
			.OrderByDescending(entry => entry.Timestamp)
			.Select(entry => entry.Entry)
			.ToArray();
		log.Verbose(
			$"Reloaded Database Unseen tab: {this.unseen.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private List<(DatabaseEntryViewModel Entry, string Key, DateTime Timestamp)> BuildUnseenList(
		Dictionary<string, CachedProfileEntry> cachedProfilesByKey,
		Dictionary<string, ResolvedCharacterEntry> resolvedByKey,
		HashSet<string> seenNameWorldKeys,
		HashSet<string> pinnedKeys,
		string? ownNameWorldKey)
	{
		var unseenEntries = new List<(DatabaseEntryViewModel Entry, string Key, DateTime Timestamp)>();
		var unseenKeys = new HashSet<string>();
		foreach (var cached in cachedProfilesByKey.Values)
		{
			var hasResolved = resolvedByKey.TryGetValue(cached.Key, out var resolvedForKey);
			var name = hasResolved ? resolvedForKey!.Name : cached.Profile.Value.Name;
			var homeWorldId = hasResolved ? resolvedForKey!.HomeWorldId : cached.Profile.Value.HomeWorldId;

			var nameWorldKey = CharacterDirectory.BuildNameWorldKey(name, homeWorldId);
			if (seenNameWorldKeys.Contains(nameWorldKey) || nameWorldKey == ownNameWorldKey)
			{
				continue;
			}

			var worldName = GameDataResolver.ResolveWorldName(dataManager, homeWorldId);
			var dataCenterName = GameDataResolver.ResolveDataCenterName(dataManager, homeWorldId);
			var jobAbbreviation = hasResolved && resolvedForKey!.JobId is { } jobId
				? GameDataResolver.ResolveJobAbbreviation(dataManager, jobId)
				: null;
			var entry = new DatabaseEntryViewModel(
				cached.Key, name, worldName, pinnedKeys.Contains(cached.Key),
				cached.Profile, cached.FetchedAtUtc, null, null,
				resolvedForKey?.AvatarUrlHash, dataCenterName, jobAbbreviation,
				resolvedForKey?.Level?.ToString());
			unseenEntries.Add((entry, cached.Key, cached.FetchedAtUtc));
			unseenKeys.Add(cached.Key);
		}

		foreach (var resolved in resolvedByKey.Values)
		{
			if (unseenKeys.Contains(resolved.Key))
			{
				continue;
			}

			var nameWorldKey = CharacterDirectory.BuildNameWorldKey(resolved.Name, resolved.HomeWorldId);
			if (seenNameWorldKeys.Contains(nameWorldKey) || nameWorldKey == ownNameWorldKey)
			{
				continue;
			}

			var worldName = GameDataResolver.ResolveWorldName(dataManager, resolved.HomeWorldId);
			var dataCenterName = GameDataResolver.ResolveDataCenterName(dataManager, resolved.HomeWorldId);
			var jobAbbreviation = resolved.JobId is { } jobId
				? GameDataResolver.ResolveJobAbbreviation(dataManager, jobId)
				: null;

			var (profile, profileFetchedAtUtc) = LodestoneProfile.ResolveOrPartial(null, resolved);
			var entry = new DatabaseEntryViewModel(
				resolved.Key, resolved.Name, worldName, pinnedKeys.Contains(resolved.Key),
				profile, profileFetchedAtUtc, null, null, resolved.AvatarUrlHash, dataCenterName,
				jobAbbreviation, resolved.Level?.ToString());
			unseenEntries.Add((entry, resolved.Key, resolved.ResolvedAtUtc));
			unseenKeys.Add(resolved.Key);
		}

		return unseenEntries;
	}

	private static Lazy<IReadOnlyList<NameHistoryEntry>> LazyNameHistory(
		CharacterDirectory characterDirectory, ulong contentId) =>
		new(() => characterDirectory.GetNameHistory(contentId));
}
