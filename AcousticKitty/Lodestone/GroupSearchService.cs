// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
	EchoStore echoStore,
	CharacterDirectory characterDirectory,
	IDataManager dataManager,
	AvatarTextureCache avatarCache,
	EchoService echo,
	IPluginLog log) : IDisposable
{
	private const int MaxRosterPages = 11;

	private readonly CancellationTokenSource disposalCts = new();

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
		this.Restart(ct => this.RunRosterAsync(this.currentKind, group.Id, ct));

	public void Cancel()
	{
		this.searchCts?.Cancel();
		this.state = GroupSearchState.Idle;
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

		await this.RunRosterAsync(kind, matches[0].Id, cancellationToken).ConfigureAwait(false);
	}

	private async Task RunRosterAsync(
		SocialGroupKind kind,
		string groupId,
		CancellationToken cancellationToken)
	{
		this.state = GroupSearchState.FetchingRoster;
		this.groupMatches = Array.Empty<GroupSearchResult>();

		var knownFreeCompanyId =
			kind == SocialGroupKind.FreeCompany && ulong.TryParse(groupId, out var fcId)
				? fcId
				: (ulong?)null;

		await client
			.GetGroupRosterAsync(
				kind, groupId, MaxRosterPages, dataManager,
				(pageEntries, freeCompanyName) =>
					this.AppendMembers(pageEntries, knownFreeCompanyId, freeCompanyName, cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);

		this.state = this.members.Count == 0 ? GroupSearchState.NotFound : GroupSearchState.Ready;
	}

	private void AppendMembers(
		IReadOnlyList<MemberListEntry> entries,
		ulong? knownFreeCompanyId,
		string? knownFreeCompanyName,
		CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();
		var collected = new List<GroupMemberViewModel>(this.members.Count + entries.Count);
		collected.AddRange(this.members);

		var resolvedIdUpdates = new List<(
			string Key, ulong LodestoneId, string Name, uint HomeWorldId, DateTime ResolvedAtUtc,
			string? AvatarUrlHash, int? Level, uint? JobId)>(entries.Count);

		foreach (var rawEntry in entries)
		{
			var entry = knownFreeCompanyId != null
				? rawEntry with { FreeCompanyId = knownFreeCompanyId, FreeCompanyName = knownFreeCompanyName }
				: rawEntry;

			var viewModel = new GroupMemberViewModel(entry);
			var nameWorldKey = CharacterDirectory.BuildNameWorldKey(entry.Name, entry.HomeWorldId);
			viewModel.KnownCharacter = characterDirectory.TryGetByNameWorldKey(nameWorldKey);

			if (viewModel.KnownCharacter == null &&
				characterDirectory.TryGetByLodestoneId(entry.CharacterId) is { } matchedByLodestoneId)
			{
				if (matchedByLodestoneId.Data.Name != entry.Name ||
					matchedByLodestoneId.Data.HomeWorldId != entry.HomeWorldId)
				{
					CharacterTransferRecorder.Record(
						characterDirectory, lodestoneCache, echoStore, dataManager,
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
				entry.AvatarUrlHash, entry.Level, entry.JobId));

			viewModel.IsVerified = echoStore.IsVerified(cacheKey);
			viewModel.IsPinned = echoStore.IsPinned(cacheKey);

			if (viewModel.IsPinned ||
				viewModel.KnownCharacter is
					{ LookupState: NearbyLookupState.Found or NearbyLookupState.AccessRestricted })
			{
				var cached = lodestoneCache.TryGetCachedProfile(cacheKey);
				if (cached != null)
				{
					viewModel.Profile = cached.Value.Profile;
					viewModel.ProfileFetchedAtUtc = cached.Value.FetchedAtUtc;
					viewModel.ProfileState = MemberProfileState.Loaded;
				}
				else if (viewModel.IsPinned)
				{
					viewModel.ProfileState = MemberProfileState.Fetching;
					_ = this.FetchMemberProfileAsync(viewModel, cacheKey);
				}
			}
			else if (viewModel.KnownCharacter is
				{ LookupState: NearbyLookupState.Pending or NearbyLookupState.NotFound })
			{
				viewModel.ProfileState = MemberProfileState.Fetching;
				_ = this.FetchMemberProfileAsync(viewModel, cacheKey);
			}

			collected.Add(viewModel);

			var worldName = GameDataResolver.ResolveWorldName(dataManager, entry.HomeWorldId);
			if (AvatarUrlBuilder.Build(entry.AvatarUrlHash, worldName) is { } prefetchUrl)
			{
				_ = avatarCache.GetOrFetchAsync(prefetchUrl, cancellationToken);
			}
		}

		lodestoneCache.SaveResolvedIds(resolvedIdUpdates);

		this.members = collected.ToArray();
		log.Verbose(
			$"GroupSearchService.AppendMembers: {entries.Count} member(s) processed in " +
			$"{stopwatch.ElapsedMilliseconds} ms.");
	}

	private async Task FetchMemberProfileAsync(GroupMemberViewModel member, string cacheKey)
	{
		try
		{
			var profile = await client
				.GetProfileAsync(member.Entry.CharacterId, dataManager, this.disposalCts.Token)
				.ConfigureAwait(false);

			profile = profile with
			{
				JobId = member.Entry.JobId,
				Level = member.Entry.Level,
				FreeCompanyId = member.Entry.FreeCompanyId ?? profile.FreeCompanyId,
				FreeCompanyName = member.Entry.FreeCompanyName ?? profile.FreeCompanyName,
			};

			var fetchedAtUtc = DateTime.UtcNow;
			var saved = lodestoneCache.SaveCachedProfile(cacheKey, profile, fetchedAtUtc);

			if (saved)
			{
				member.Profile = profile;
				member.ProfileFetchedAtUtc = fetchedAtUtc;
			}
			else
			{
				log.Warning(ProfileRegressionGuard.LogMessage(member.Entry.Name));
			}

			this.LinkKnownCharacter(member);

			member.ProfileState = MemberProfileState.Loaded;
		}
		catch (OperationCanceledException)
		{
			member.ProfileState = MemberProfileState.NotPinned;
		}
		catch (LodestoneNotFoundException)
		{
			member.ProfileState = MemberProfileState.NotFound;
		}
		catch (LodestoneAccessRestrictedException)
		{
			this.LinkKnownCharacter(member);
			member.ProfileState = MemberProfileState.AccessRestricted;
		}
		catch (Exception ex)
		{
			log.Error(ex, "Failed to fetch a Search tab member's profile.");
			member.ErrorMessage = ex.Message;
			member.ProfileState = MemberProfileState.Error;
		}
	}

	private void LinkKnownCharacter(GroupMemberViewModel member)
	{
		if (member.KnownCharacter is not { } known)
		{
			return;
		}

		characterDirectory.SetLookupResult(
			known.Data.ContentId, member.Entry.CharacterId, NearbyLookupState.Found, null);
		echo.NotifyMatchConfirmed();
	}

	#endregion
}
