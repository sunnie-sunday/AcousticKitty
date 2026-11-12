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
	EchoList echoList,
	CharacterDirectory characterDirectory,
	IDataManager dataManager,
	IPlayerState playerState,
	GroupSearchService groupSearchService,
	FreeCompanyIdIndex freeCompanyIdIndex,
	Configuration configuration,
	IPluginLog log)
{
	private volatile IReadOnlyList<DatabaseEntryViewModel> pinned =
		Array.Empty<DatabaseEntryViewModel>();
	private volatile IReadOnlyList<DatabaseEntryViewModel> matched =
		Array.Empty<DatabaseEntryViewModel>();
	private volatile IReadOnlyList<DatabaseEntryViewModel> hidden =
		Array.Empty<DatabaseEntryViewModel>();
	private volatile IReadOnlyList<DatabaseEntryViewModel> unseen =
		Array.Empty<DatabaseEntryViewModel>();

	private volatile bool isReloadingPinned;
	private volatile bool isReloadingMatched;
	private volatile bool isReloadingHidden;
	private volatile bool isReloadingUnseen;

	public IReadOnlyList<DatabaseEntryViewModel> Pinned => this.pinned;

	public IReadOnlyList<DatabaseEntryViewModel> Matched => this.matched;

	public IReadOnlyList<DatabaseEntryViewModel> Hidden => this.hidden;

	public IReadOnlyList<DatabaseEntryViewModel> Unseen => this.unseen;

	public bool IsReloadingPinned => this.isReloadingPinned;

	public bool IsReloadingMatched => this.isReloadingMatched;

	public bool IsReloadingHidden => this.isReloadingHidden;

	public bool IsReloadingUnseen => this.isReloadingUnseen;

	public void ReloadPinnedAsync() =>
		this.RunAsync(
			"Stalkers", () => this.isReloadingPinned, v => this.isReloadingPinned = v, this.ReloadPinnedCore);

	public void ReloadMatchedAsync() =>
		this.RunAsync(
			"Matched", () => this.isReloadingMatched, v => this.isReloadingMatched = v,
			this.ReloadMatchedCore);

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
		var pins = echoList.All
			.Select(pin => (Pin: pin, Known:
				(pin.ContentId is { } contentId ? characterDirectory.TryGetByContentId(contentId) : null)
				?? characterDirectory.TryGetByLodestoneId(pin.LodestoneId)))
			.ToList();

		var nameWorldKeys = pins
			.Select(row => row.Known is { LookupState: not NearbyLookupState.Transferred } known
				? CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId)
				: row.Known == null ? row.Pin.NameWorldKey : null)
			.OfType<string>()
			.Distinct()
			.ToList();
		var cachedProfilesByKey =
			lodestoneCache.GetCachedProfilesForKeys(nameWorldKeys).ToDictionary(cached => cached.Key);
		var resolvedByKey =
			lodestoneCache.GetResolvedForKeys(nameWorldKeys).ToDictionary(resolved => resolved.Key);

		var pinnedList = pins
			.OrderByDescending(row => row.Known?.LastSeenUtc ?? DateTime.MinValue)
			.ThenBy(row => row.Known?.Data.Name ?? row.Pin.Name, StringComparer.OrdinalIgnoreCase)
			.Select(row =>
			{
				var (pin, known) = row;
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
					var snapshot = lodestoneCache.TryGetPinnedProfileSnapshot(pin.LodestoneId);
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

	private void ReloadMatchedCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceMatched(
			characterDirectory, lodestoneCache, echoList,
			DatabaseCapEnforcer.ResolveCap(configuration.DatabaseCacheTier));

		var candidates = DatabaseCapEnforcer.GetMatchedCandidates(characterDirectory, echoList);
		var cacheKeys = candidates
			.Select(known => CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId))
			.ToList();
		var cachedProfilesByKey =
			lodestoneCache.GetCachedProfilesForKeys(cacheKeys).ToDictionary(cached => cached.Key);
		var resolvedByKey =
			lodestoneCache.GetResolvedForKeys(cacheKeys).ToDictionary(resolved => resolved.Key);

		this.matched = candidates
			.OrderByDescending(known => known.LastSeenUtc)
			.Select(known =>
			{
				var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
				var worldName = GameDataResolver.ResolveWorldName(dataManager, known.Data.HomeWorldId);
				var dataCenterName =
					GameDataResolver.ResolveDataCenterName(dataManager, known.Data.HomeWorldId);
				cachedProfilesByKey.TryGetValue(cacheKey, out var cached);
				resolvedByKey.TryGetValue(cacheKey, out var resolved);

				var (profile, profileFetchedAtUtc) = LodestoneProfile.ResolveOrPartial(cached, resolved);
				return new DatabaseEntryViewModel(
					cacheKey, known.Data.Name, worldName, false,
					profile, profileFetchedAtUtc, known.Data, known.LastSeenUtc,
					resolved?.AvatarUrlHash, dataCenterName,
					NameHistory: DatabaseOverviewService.LazyNameHistory(
						characterDirectory, known.Data.ContentId));
			}).ToArray();
		log.Verbose(
			$"Reloaded Database Matched tab: {this.matched.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private void ReloadHiddenCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceHidden(
			characterDirectory, echoList,
			DatabaseCapEnforcer.ResolveCap(configuration.DatabaseCacheTier));

		var hiddenKnown = DatabaseCapEnforcer.GetHiddenCandidates(characterDirectory, echoList);
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
			characterDirectory, lodestoneCache, echoList,
			DatabaseCapEnforcer.ResolveCap(configuration.DatabaseCacheTier));

		var seenNameWorldKeys = new HashSet<string>(characterDirectory.GetAllNameWorldKeys());
		var resolvedByKey = lodestoneCache.GetAllResolved().ToDictionary(resolved => resolved.Key);

		var ownNameWorldKey = playerState.IsLoaded
			? CharacterKey.Build(playerState.CharacterName, playerState.HomeWorld.RowId)
			: null;

		this.unseen = UnseenCandidates
			.Enumerate(
				lodestoneCache.GetAllCachedProfiles(), resolvedByKey, seenNameWorldKeys, ownNameWorldKey)
			.Where(row => !DatabaseCapEnforcer.IsPinnedUnseen(row, echoList))
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
