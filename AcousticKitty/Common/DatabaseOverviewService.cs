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
using Dalamud.Plugin.Services;

namespace AcousticKitty.Common;

public sealed class DatabaseOverviewService(
	LodestoneCache lodestoneCache,
	CharacterDirectory characterDirectory,
	IDataManager dataManager,
	IPluginLog log)
{
	private volatile IReadOnlyList<DatabaseEntryViewModel> unverified =
		Array.Empty<DatabaseEntryViewModel>();
	private volatile IReadOnlyList<DatabaseEntryViewModel> hidden =
		Array.Empty<DatabaseEntryViewModel>();

	private volatile bool isReloadingUnverified;
	private volatile bool isReloadingHidden;

	public IReadOnlyList<DatabaseEntryViewModel> Unverified => this.unverified;

	public IReadOnlyList<DatabaseEntryViewModel> Hidden => this.hidden;

	public bool IsReloadingUnverified => this.isReloadingUnverified;

	public bool IsReloadingHidden => this.isReloadingHidden;

	public void ReloadUnverifiedAsync() =>
		DatabaseOverviewService.RunAsync(
			() => this.isReloadingUnverified, v => this.isReloadingUnverified = v, this.ReloadUnverifiedCore);

	public void ReloadHiddenAsync() =>
		DatabaseOverviewService.RunAsync(
			() => this.isReloadingHidden, v => this.isReloadingHidden = v, this.ReloadHiddenCore);

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

	private void ReloadUnverifiedCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceUnverified(characterDirectory, lodestoneCache);

		var candidates = characterDirectory.GetFoundOrAccessRestricted().ToList();
		var cacheKeys = candidates
			.Select(known => CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId))
			.ToList();
		var cachedProfilesByKey =
			lodestoneCache.GetCachedProfilesForKeys(cacheKeys).ToDictionary(cached => cached.Key);
		var resolvedByKey =
			lodestoneCache.GetResolvedForKeys(cacheKeys).ToDictionary(resolved => resolved.Key);

		this.unverified = candidates
			.OrderByDescending(known => known.LastSeenUtc)
			.Select(known =>
			{
				var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
				var worldName = GameDataResolver.ResolveWorldName(dataManager, known.Data.HomeWorldId);
				var dataCenterName =
					GameDataResolver.ResolveDataCenterName(dataManager, known.Data.HomeWorldId);
				cachedProfilesByKey.TryGetValue(cacheKey, out var cached);
				resolvedByKey.TryGetValue(cacheKey, out var resolved);
				return new DatabaseEntryViewModel(
					cacheKey, known.Data.Name, worldName, cached?.Profile, cached?.FetchedAtUtc,
					known.Data, known.LastSeenUtc, resolved?.AvatarUrlHash, dataCenterName,
					NameHistory: DatabaseOverviewService.LazyNameHistory(
						characterDirectory, known.Data.ContentId));
			}).ToArray();
		log.Verbose(
			$"Reloaded Database Unverified tab: {this.unverified.Count} in " +
			$"{stopwatch.ElapsedMilliseconds} ms.");
	}

	private void ReloadHiddenCore()
	{
		var stopwatch = Stopwatch.StartNew();
		DatabaseCapEnforcer.EnforceHidden(characterDirectory);

		var hiddenKnown = characterDirectory.GetHidden();
		var cacheKeys = hiddenKnown
			.Select(known => CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId))
			.ToList();
		var resolvedByKey =
			lodestoneCache.GetResolvedForKeys(cacheKeys).ToDictionary(resolved => resolved.Key);

		this.hidden = hiddenKnown
			.OrderByDescending(known => known.LastSeenUtc)
			.Select(known =>
			{
				var cacheKey = CharacterKey.Build(known.Data.Name, known.Data.HomeWorldId);
				var worldName = GameDataResolver.ResolveWorldName(dataManager, known.Data.HomeWorldId);
				var dataCenterName =
					GameDataResolver.ResolveDataCenterName(dataManager, known.Data.HomeWorldId);
				resolvedByKey.TryGetValue(cacheKey, out var resolved);
				return new DatabaseEntryViewModel(
					cacheKey, known.Data.Name, worldName, null, null, known.Data, known.LastSeenUtc,
					resolved?.AvatarUrlHash, dataCenterName,
					NameHistory: DatabaseOverviewService.LazyNameHistory(
						characterDirectory, known.Data.ContentId));
			}).ToArray();
		log.Verbose(
			$"Reloaded Database Hidden tab: {this.hidden.Count} in {stopwatch.ElapsedMilliseconds} ms.");
	}

	private static Lazy<IReadOnlyList<NameHistoryEntry>> LazyNameHistory(
		CharacterDirectory characterDirectory, ulong contentId) =>
		new(() => characterDirectory.GetNameHistory(contentId));
}
