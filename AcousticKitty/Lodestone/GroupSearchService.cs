// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AcousticKitty.Character;
using AcousticKitty.Common;
using AcousticKitty.Echo;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Lodestone;

public sealed class GroupSearchService(
	ILodestoneClient client,
	LodestoneCache lodestoneCache,
	EchoList echoList,
	CharacterDirectory characterDirectory,
	IDataManager dataManager,
	IPlayerState playerState,
	AvatarTextureCache avatarCache,
	FreeCompanyIdIndex freeCompanyIdIndex,
	IPluginLog log) : IDisposable
{
	private const int MaxRosterPages = 11;

	private readonly CancellationTokenSource disposalCts = new();
	private readonly object forceSearchGate = new();
	private readonly HashSet<ulong> forceSearchedFreeCompanyIds = new();
	private readonly object activeRosterFetchesGate = new();
	private readonly Dictionary<ulong, string> activeRosterFetches = new();

	private volatile GroupSearchState state = GroupSearchState.Idle;
	private string? errorMessage;
	private IReadOnlyList<GroupSearchResult> groupMatches = Array.Empty<GroupSearchResult>();
	private volatile IReadOnlyList<GroupMemberViewModel> members = Array.Empty<GroupMemberViewModel>();
	private SocialGroupKind currentKind;
	private CancellationTokenSource? searchCts;

	#region Public API

	public GroupSearchState State => this.state;

	public string? ErrorMessage => this.errorMessage;

	public IReadOnlyList<GroupSearchResult> GroupMatches => this.groupMatches;
	public IReadOnlyList<GroupMemberViewModel> Members => this.members;

	public IReadOnlyList<(ulong FreeCompanyId, string FreeCompanyName)> ActiveFreeCompanyRosterFetches
	{
		get
		{
			lock (this.activeRosterFetchesGate)
			{
				return this.activeRosterFetches.Select(pair => (pair.Key, pair.Value)).ToArray();
			}
		}
	}

	public IReadOnlyList<NameHistoryEntry> GetNameHistory(ulong contentId) =>
		characterDirectory.GetNameHistory(contentId);

	public void StartSearch(
		SocialGroupKind kind,
		GroupSearchMode mode,
		string nameOrId,
		string? worldOrDataCenter)
	{
		this.currentKind = kind;
		this.groupMatches = Array.Empty<GroupSearchResult>();
		this.members = Array.Empty<GroupMemberViewModel>();
		this.Restart(ct => this.RunSearchAsync(kind, mode, nameOrId, worldOrDataCenter, ct));
	}

	public void SelectGroup(GroupSearchResult group) =>
		this.Restart(ct => this.RunRosterAsync(this.currentKind, group.Id, ct, group.Name));

	public void Cancel()
	{
		this.searchCts?.Cancel();
		this.state = GroupSearchState.Idle;
	}

	internal async Task ForceResolveFreeCompanyRosterAsync(ulong freeCompanyId, string freeCompanyName)
	{
		lock (this.forceSearchGate)
		{
			if (!this.forceSearchedFreeCompanyIds.Add(freeCompanyId))
			{
				return;
			}
		}

		this.AddActiveRosterFetch(freeCompanyId, freeCompanyName);
		try
		{
			await client
				.GetGroupRosterAsync(
					SocialGroupKind.FreeCompany,
					freeCompanyId.ToString(CultureInfo.InvariantCulture),
					MaxRosterPages,
					dataManager,
					(pageEntries, resolvedFreeCompanyName) => this.ResolveEntries(
						pageEntries, SocialGroupKind.FreeCompany, freeCompanyId,
						resolvedFreeCompanyName ?? freeCompanyName, fromSearchTab: false,
						this.disposalCts.Token),
					this.disposalCts.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (LodestoneNotFoundException)
		{
			log.Information(
				$"Free Company {freeCompanyName} ({freeCompanyId}) skipped after disbanding.");
		}
		catch (LodestoneUnavailableException ex)
		{
			lock (this.forceSearchGate)
			{
				this.forceSearchedFreeCompanyIds.Remove(freeCompanyId);
			}

			log.Warning(
				$"Free Company {freeCompanyName} ({freeCompanyId}) roster interrupted: {ex.Message}");
		}
		catch (Exception ex)
		{
			log.Error(ex, $"Failed to force-resolve Free Company roster {freeCompanyId}.");
		}
		finally
		{
			this.RemoveActiveRosterFetch(freeCompanyId);
		}
	}

	internal void ApplyFetchedProfile(
		ulong lodestoneId, LodestoneProfile profile, DateTime fetchedAtUtc)
	{
		foreach (var member in this.members)
		{
			if (member.Entry.CharacterId == lodestoneId)
			{
				member.Profile = profile;
				member.ProfileFetchedAtUtc = fetchedAtUtc;
			}
		}
	}

	public void Dispose()
	{
		this.searchCts?.Cancel();
		this.disposalCts.Cancel();
		this.disposalCts.Dispose();
	}

	#endregion

	#region Private Implementation

	private void Restart(Func<CancellationToken, Task> work)
	{
		this.searchCts?.Cancel();
		var cts = new CancellationTokenSource();
		this.searchCts = cts;
		this.errorMessage = null;
		_ = this.RunGuardedAsync(work, cts.Token);
	}

	private async Task RunGuardedAsync(
		Func<CancellationToken, Task> work,
		CancellationToken cancellationToken)
	{
		try
		{
			await work(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (LodestoneNotFoundException)
		{
			this.state = GroupSearchState.NotFound;
		}
		catch (Exception ex)
		{
			log.Error(ex, "Group search failed.");
			this.errorMessage = ex.Message;
			this.state = GroupSearchState.Error;
		}
	}

	private async Task RunSearchAsync(
		SocialGroupKind kind,
		GroupSearchMode mode,
		string nameOrId,
		string? worldOrDataCenter,
		CancellationToken cancellationToken)
	{
		if (mode == GroupSearchMode.Id)
		{
			await this.RunRosterAsync(kind, nameOrId.Trim(), cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		this.state = GroupSearchState.SearchingGroups;
		var matches = await client
			.SearchGroupAsync(kind, nameOrId.Trim(), worldOrDataCenter, dataManager, cancellationToken)
			.ConfigureAwait(false);

		if (matches.Count == 0)
		{
			this.state = GroupSearchState.NotFound;
			return;
		}

		if (matches.Count > 1)
		{
			this.groupMatches = matches;
			this.state = GroupSearchState.AwaitingSelection;
			return;
		}

		await this.RunRosterAsync(kind, matches[0].Id, cancellationToken, matches[0].Name)
			.ConfigureAwait(false);
	}

	private async Task RunRosterAsync(
		SocialGroupKind kind,
		string groupId,
		CancellationToken cancellationToken,
		string? knownGroupName = null)
	{
		this.state = GroupSearchState.FetchingRoster;
		this.groupMatches = Array.Empty<GroupSearchResult>();

		var knownFreeCompanyId =
			kind == SocialGroupKind.FreeCompany && ulong.TryParse(groupId, out var fcId)
				? fcId
				: (ulong?)null;

		var nameTracked = false;
		if (knownFreeCompanyId is { } upfrontId && knownGroupName != null)
		{
			this.AddActiveRosterFetch(upfrontId, knownGroupName);
			nameTracked = true;
		}

		try
		{
			await client
				.GetGroupRosterAsync(
					kind, groupId, MaxRosterPages, dataManager,
					(pageEntries, freeCompanyName) =>
					{
						if (!nameTracked && knownFreeCompanyId is { } id && freeCompanyName != null)
						{
							this.AddActiveRosterFetch(id, freeCompanyName);
							nameTracked = true;
						}

						this.AppendMembers(
							pageEntries, kind, knownFreeCompanyId, freeCompanyName ?? knownGroupName,
							cancellationToken);
					},
					cancellationToken)
				.ConfigureAwait(false);
		}
		finally
		{
			if (knownFreeCompanyId is { } idToRemove)
			{
				this.RemoveActiveRosterFetch(idToRemove);
			}
		}

		this.state = this.members.Count == 0 ? GroupSearchState.NotFound : GroupSearchState.Ready;
	}

	private void AddActiveRosterFetch(ulong freeCompanyId, string freeCompanyName)
	{
		lock (this.activeRosterFetchesGate)
		{
			this.activeRosterFetches[freeCompanyId] = freeCompanyName;
		}
	}

	private void RemoveActiveRosterFetch(ulong freeCompanyId)
	{
		lock (this.activeRosterFetchesGate)
		{
			this.activeRosterFetches.Remove(freeCompanyId);
		}
	}

	private void AppendMembers(
		IReadOnlyList<MemberListEntry> entries,
		SocialGroupKind kind,
		ulong? knownFreeCompanyId,
		string? knownFreeCompanyName,
		CancellationToken cancellationToken)
	{
		var resolved = this.ResolveEntries(
			entries, kind, knownFreeCompanyId, knownFreeCompanyName, fromSearchTab: true,
			cancellationToken);
		this.members = this.members.Concat(resolved).ToArray();
	}

	private List<GroupMemberViewModel> ResolveEntries(
		IReadOnlyList<MemberListEntry> entries,
		SocialGroupKind kind,
		ulong? knownFreeCompanyId,
		string? knownFreeCompanyName,
		bool fromSearchTab,
		CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();
		var collected = new List<GroupMemberViewModel>(entries.Count);

		var resolvedIdUpdates = new List<(
			string Key, ulong LodestoneId, string Name, uint HomeWorldId, DateTime ResolvedAtUtc,
			string? AvatarUrlHash, int? Level, uint? JobId, ulong? FreeCompanyId,
			string? FreeCompanyName)>(entries.Count);

		var ownNameWorldKey = playerState.IsLoaded
			? CharacterKey.Build(playerState.CharacterName, playerState.HomeWorld.RowId)
			: null;

		foreach (var rawEntry in entries)
		{
			var entry = knownFreeCompanyId != null
				? rawEntry with { FreeCompanyId = knownFreeCompanyId, FreeCompanyName = knownFreeCompanyName }
				: rawEntry;

			var viewModel = new GroupMemberViewModel(entry);
			var nameWorldKey = CharacterKey.Build(entry.Name, entry.HomeWorldId);

			if (ownNameWorldKey != null && nameWorldKey == ownNameWorldKey)
			{
				viewModel.Profile = LodestoneProfile.BuildPartial(
					entry.CharacterId, entry.Name, entry.HomeWorldId, entry.AvatarUrlHash,
					entry.FreeCompanyId, entry.FreeCompanyName, entry.JobId, entry.Level, isPrivate: false);
				collected.Add(viewModel);
				continue;
			}

			viewModel.KnownCharacter = characterDirectory.TryGetByNameWorldKey(nameWorldKey);

			if (viewModel.KnownCharacter == null &&
				characterDirectory.TryGetByLodestoneId(entry.CharacterId) is { } matchedByLodestoneId)
			{
				if (matchedByLodestoneId.Data.Name != entry.Name ||
					matchedByLodestoneId.Data.HomeWorldId != entry.HomeWorldId)
				{
					CharacterTransferRecorder.Record(
						characterDirectory, lodestoneCache, dataManager,
						matchedByLodestoneId.Data.ContentId, matchedByLodestoneId.Data.Name,
						matchedByLodestoneId.Data.HomeWorldId, matchedByLodestoneId.LastSeenUtc,
						entry.Name, entry.HomeWorldId, matchedByLodestoneId.LodestoneId);
					characterDirectory.UpdateNameWorld(
						matchedByLodestoneId.Data.ContentId, entry.Name, entry.HomeWorldId);
				}

				viewModel.KnownCharacter =
					characterDirectory.TryGetByContentId(matchedByLodestoneId.Data.ContentId);
			}

			var cacheKey = CharacterKey.Build(entry.Name, entry.HomeWorldId);

			resolvedIdUpdates.Add((
				cacheKey, entry.CharacterId, entry.Name, entry.HomeWorldId, DateTime.UtcNow,
				entry.AvatarUrlHash, entry.Level, entry.JobId, entry.FreeCompanyId, entry.FreeCompanyName));

			if (viewModel.KnownCharacter is { } matchedCharacter)
			{
				freeCompanyIdIndex.Record(
					matchedCharacter.Data.FreeCompanyTag, matchedCharacter.Data.HomeWorldId,
					entry.FreeCompanyId, entry.FreeCompanyName);
			}

			if (viewModel.KnownCharacter is {
				LookupState: NearbyLookupState.Pending or NearbyLookupState.NotFound
					or NearbyLookupState.Error or NearbyLookupState.Deleted,
			} unresolved)
			{
				this.ResolveMatchedMember(unresolved.Data.ContentId, entry.CharacterId, kind);
				viewModel.KnownCharacter = characterDirectory.TryGetByContentId(unresolved.Data.ContentId);
			}

			viewModel.IsPinned = echoList.IsPinnedLodestoneId(entry.CharacterId) ||
				(viewModel.KnownCharacter is { } pinCandidate &&
					echoList.IsPinned(pinCandidate.Data.ContentId, null));
			viewModel.IsConflicted =
				viewModel.KnownCharacter?.LookupState == NearbyLookupState.ConflictHold;

			if ((viewModel.IsPinned || viewModel.KnownCharacter != null) &&
				!this.TryApplyCachedProfile(viewModel, cacheKey) &&
				!(viewModel.IsPinned && this.TryApplyPinnedSnapshot(viewModel)))
			{
				viewModel.Profile = LodestoneProfile.BuildPartial(
					entry.CharacterId, entry.Name, entry.HomeWorldId, entry.AvatarUrlHash,
					entry.FreeCompanyId, entry.FreeCompanyName, entry.JobId, entry.Level,
					viewModel.KnownCharacter?.LookupState == NearbyLookupState.AccessRestricted);
			}

			collected.Add(viewModel);

			if (fromSearchTab)
			{
				var worldName = GameDataResolver.ResolveWorldName(dataManager, entry.HomeWorldId);
				if (AvatarUrlBuilder.Build(entry.AvatarUrlHash, worldName) is { } prefetchUrl)
				{
					_ = avatarCache.GetOrFetchAsync(prefetchUrl, cancellationToken);
				}
			}
		}

		lodestoneCache.SaveResolvedIds(resolvedIdUpdates);

		log.Verbose(
			$"GroupSearchService.ResolveEntries: {entries.Count} member(s) processed in " +
			$"{stopwatch.ElapsedMilliseconds} ms.");

		return collected;
	}

	private bool TryApplyCachedProfile(GroupMemberViewModel viewModel, string cacheKey)
	{
		var cached = lodestoneCache.TryGetCachedProfile(cacheKey);
		if (cached == null)
		{
			return false;
		}

		viewModel.Profile = cached.Value.Profile;
		viewModel.ProfileFetchedAtUtc = cached.Value.FetchedAtUtc;
		return true;
	}

	private bool TryApplyPinnedSnapshot(GroupMemberViewModel viewModel)
	{
		var snapshot = lodestoneCache.TryGetPinnedProfileSnapshot(viewModel.Entry.CharacterId);
		if (snapshot == null)
		{
			return false;
		}

		viewModel.Profile = snapshot.Profile;
		viewModel.ProfileFetchedAtUtc = snapshot.FetchedAtUtc;
		return true;
	}

	private void ResolveMatchedMember(ulong contentId, ulong lodestoneId, SocialGroupKind kind)
	{
		if (kind == SocialGroupKind.PvpTeam)
		{
			characterDirectory.SetLookupResult(
				contentId, lodestoneId, NearbyLookupState.Pending, null, clearOverride: false,
				priorityAtUtc: DateTime.UtcNow);
			return;
		}

		if (!characterDirectory.HoldIfLodestoneIdTaken(contentId, lodestoneId, DateTime.UtcNow))
		{
			characterDirectory.SetLookupResult(
				contentId, lodestoneId, NearbyLookupState.Found, null);
		}
	}

	#endregion
}
