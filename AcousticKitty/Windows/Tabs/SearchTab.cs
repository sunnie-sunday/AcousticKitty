// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcousticKitty.Character;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;
using AcousticKitty.Windows.Views;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace AcousticKitty.Windows.Tabs;

internal sealed class SearchTab(Plugin plugin)
{
	private const int PageSize = 50;

	private static readonly SocialGroupKind[] GroupKinds =
	[
		SocialGroupKind.FreeCompany,
		SocialGroupKind.Linkshell,
		SocialGroupKind.CrossWorldLinkshell,
		SocialGroupKind.PvpTeam,
	];

	private static readonly string[] GroupKindNames =
		["Free Company", "Linkshell", "Cross-world Linkshell", "PvP Team"];
	private static readonly string[] SearchModeNames = ["Name", "ID"];

	private readonly string[] dataCenterOptions =
		["(Any)", .. GameDataResolver.GetAllDataCenterNames(Plugin.DataManager)];

	private int groupKindIndex;
	private int searchModeIndex;
	private string nameInput = string.Empty;
	private int dataCenterIndex;
	private string idInput = string.Empty;
	private string membersNameFilter = string.Empty;
	private int membersPage;
	private readonly FilterCache<GroupMemberViewModel> membersFilterCache = new();

	public void Draw()
	{
		var search = plugin.GroupSearchService;
		var busy = search.State is GroupSearchState.SearchingGroups or GroupSearchState.FetchingRoster;

		ImGui.Combo("Group Type", ref this.groupKindIndex, GroupKindNames, GroupKindNames.Length);
		ImGui.Combo("Search By", ref this.searchModeIndex, SearchModeNames, SearchModeNames.Length);

		if (this.searchModeIndex == (int)GroupSearchMode.Name)
		{
			ImGui.InputText("Name", ref this.nameInput, 128);
			ImGui.Combo(
				"Data Center (optional)", ref this.dataCenterIndex, this.dataCenterOptions,
				this.dataCenterOptions.Length);
		}
		else
		{
			ImGui.InputText("ID", ref this.idInput, 64);
		}

		ImGui.BeginDisabled(busy);
		if (ImGui.Button("Search"))
		{
			var kind = GroupKinds[this.groupKindIndex];
			var mode = (GroupSearchMode)this.searchModeIndex;
			var nameOrId = mode == GroupSearchMode.Name ? this.nameInput.Trim() : this.idInput.Trim();
			var worldOrDataCenter = this.dataCenterIndex == 0
				? null
				: this.dataCenterOptions[this.dataCenterIndex];
			if (!string.IsNullOrEmpty(nameOrId))
			{
				search.StartSearch(kind, mode, nameOrId, worldOrDataCenter);
			}
		}

		ImGui.EndDisabled();

		if (busy)
		{
			ImGui.SameLine();
			if (ImGui.Button("Cancel"))
			{
				search.Cancel();
			}
		}

		ImGui.Separator();
		ImGui.Spacing();

		switch (search.State)
		{
			case GroupSearchState.SearchingGroups:
				ImGui.TextWrapped("Searching...");
				return;
			case GroupSearchState.NotFound:
				ImGui.TextColored(ImGuiColors.ErrorForeground, "No matching group found.");
				return;
			case GroupSearchState.Error:
				ImGui.TextColored(ImGuiColors.ErrorForeground, "Something went wrong:");
				ImGui.TextWrapped(search.ErrorMessage ?? "Unknown error.");
				return;
			case GroupSearchState.AwaitingSelection:
				ImGui.TextWrapped("Multiple groups matched - pick one:");
				ImGui.Spacing();

				foreach (var match in search.GroupMatches.Take(50))
				{
					this.DrawGroupMatchRow(match);
				}

				return;
			case GroupSearchState.FetchingRoster:
				ImGui.TextWrapped($"Fetching roster... ({search.Members.Count} members so far)");
				break;
		}

		if (search.Members.Count == 0)
		{
			return;
		}

		var (pageItems, filteredCount, totalPages) = PagedList.Apply(
			this.membersFilterCache, search.Members, this.membersNameFilter, member => member.Entry.Name,
			ref this.membersPage, PageSize);

		PagedList.DrawScrollableList(
			"SearchResults", filteredCount, pageItems, "No members match this filter.",
			this.DrawMemberRow);

		PagedList.DrawFilterAndPagerFooter(
			ref this.membersNameFilter, ref this.membersPage, totalPages, "SearchResults");
	}

	private void DrawGroupMatchRow(GroupSearchResult match)
	{
		var dataManager = Plugin.DataManager;
		var dataCenter = GameDataResolver.ResolveDataCenterNameById(dataManager, match.DataCenterId);
		var worldLine = match.WorldId is { } worldId
			? $"{GameDataResolver.ResolveWorldName(dataManager, worldId)} [{dataCenter}]"
			: $"[{dataCenter}]";

		var rowStart = ImGui.GetCursorScreenPos();
		var rowWidth = ImGui.GetContentRegionAvail().X;

		ProfileView.DrawLayeredIcon(plugin, match.IconUrls, 64f);
		ImGui.SameLine();
		ImGui.BeginGroup();
		ImGui.TextUnformatted(match.Name);
		ImGui.TextColored(ImGuiColors.DalamudGrey3, worldLine);
		ImGui.EndGroup();

		var rowHeight = ImGui.GetCursorScreenPos().Y - rowStart.Y;
		ImGui.SetCursorScreenPos(rowStart);
		var rowSize = new Vector2(rowWidth, rowHeight);
		if (ImGui.Selectable($"##{match.Id}", false, ImGuiSelectableFlags.None, rowSize))
		{
			plugin.GroupSearchService.SelectGroup(match);
		}

		ImGui.Separator();
	}

	private void DrawMemberRow(GroupMemberViewModel member)
	{
		var dataManager = Plugin.DataManager;
		var fallbackWorld = GameDataResolver.ResolveWorldName(dataManager, member.Entry.HomeWorldId);
		var fallbackDataCenter =
			GameDataResolver.ResolveDataCenterName(dataManager, member.Entry.HomeWorldId);
		var primaryLine = ProfileView.FormatPrimaryLine(
			member.KnownCharacter?.Data, member.Profile, member.Entry.Name, fallbackWorld,
			fallbackDataCenter);

		var fallbackJobAbbreviation = member.Entry.JobId is { } jobId
			? GameDataResolver.ResolveJobAbbreviation(dataManager, jobId)
			: null;
		var badge = ProfileView.FormatJobLine(
			member.KnownCharacter?.Data, null, fallbackJobAbbreviation, member.Entry.Level?.ToString());

		string? statusMessage = member.ProfileState switch
		{
			MemberProfileState.NotFound =>
				"This character is hidden from search results.",
			MemberProfileState.AccessRestricted =>
				"This character's profile is private.",
			MemberProfileState.Error =>
				$"Failed to fetch profile: {member.ErrorMessage ?? "Unknown error."}",
			_ => null,
		};

		MemberRow.Draw(
			plugin,
			new CharacterRowData(
				member.Entry.CharacterId.ToString(),
				member.IsPinned,
				ProfileView.ResolveAvatarUrl(member.Profile, member.Entry.AvatarUrlHash, fallbackWorld),
				primaryLine,
				badge,
				IsBusy: member.ProfileState == MemberProfileState.Fetching,
				statusMessage,
				member.Profile is { } profile ? new Lazy<LodestoneProfile>(() => profile) : null,
				member.KnownCharacter?.Data,
				member.KnownCharacter?.LastSeenUtc,
				member.KnownCharacter is { } known
					? new Lazy<IReadOnlyList<NameHistoryEntry>>(
						() => plugin.GroupSearchService.GetNameHistory(known.Data.ContentId))
					: null,
				member.IsVerified,
				member.ProfileFetchedAtUtc));
	}
}
