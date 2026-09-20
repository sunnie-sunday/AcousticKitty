// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using AcousticKitty.Character;
using AcousticKitty.Common;
using AcousticKitty.Windows.Views;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace AcousticKitty.Windows.Tabs;

internal sealed class DatabaseTab(Plugin plugin)
{
	private const int PageSize = 50;

	private enum DatabaseSubTab
	{
		Stalkers,
		Verified,
		Unverified,
		Hidden,
		Unseen,
	}

	private string nameFilter = string.Empty;
	private int pinnedPage;
	private int verifiedPage;
	private int unverifiedPage;
	private int hiddenPage;
	private int unseenPage;

	private DatabaseSubTab? lastDrawnSubTab;

	private readonly FilterCache<DatabaseEntryViewModel> pinnedFilterCache = new();
	private readonly FilterCache<DatabaseEntryViewModel> verifiedFilterCache = new();
	private readonly FilterCache<DatabaseEntryViewModel> unverifiedFilterCache = new();
	private readonly FilterCache<DatabaseEntryViewModel> hiddenFilterCache = new();
	private readonly FilterCache<DatabaseEntryViewModel> unseenFilterCache = new();

	private readonly Dictionary<string, string> hiddenLodestoneIdInputs = new();

	public void Draw(bool justSelected)
	{
		var overview = plugin.DatabaseOverviewService;

		this.DrawFilterAndRefresh(overview);
		ImGui.Spacing();

		if (!ImGui.BeginTabBar("DatabaseSubTabs"))
		{
			return;
		}

		DatabaseSubTab? activeSubTab = null;

		if (ImGui.BeginTabItem(
			$"{this.TabLabel("Stalkers", overview.Pinned.Count, DatabaseSubTab.Stalkers)}###DatabaseStalkersTab"))
		{
			activeSubTab = DatabaseSubTab.Stalkers;
			this.DrawSection(
				overview.Pinned, this.pinnedFilterCache, ref this.pinnedPage, "DatabasePinned",
				"Nothing pinned yet - the Echo API hasn't confirmed any character as pinned.",
				statusMessage: null, overview.IsReloadingPinned);
			ImGui.EndTabItem();
		}

		if (ImGui.BeginTabItem(
			$"{this.TabLabel("Verified", overview.Verified.Count, DatabaseSubTab.Verified)}###DatabaseVerifiedTab"))
		{
			activeSubTab = DatabaseSubTab.Verified;
			this.DrawSection(
				overview.Verified, this.verifiedFilterCache, ref this.verifiedPage, "DatabaseVerified",
				"No matched character has a definitive Echo API answer yet.",
				statusMessage: null, overview.IsReloadingVerified);
			ImGui.EndTabItem();
		}

		if (ImGui.BeginTabItem(
			$"{this.TabLabel("Unverified", overview.Unverified.Count, DatabaseSubTab.Unverified)}###DatabaseUnverifiedTab"))
		{
			activeSubTab = DatabaseSubTab.Unverified;
			this.DrawSection(
				overview.Unverified, this.unverifiedFilterCache, ref this.unverifiedPage,
				"DatabaseUnverified",
				"No character has both Character Data and a matched Lodestone profile yet.",
				statusMessage: null, overview.IsReloadingUnverified);
			ImGui.EndTabItem();
		}

		if (ImGui.BeginTabItem(
			$"{this.TabLabel("Hidden", overview.Hidden.Count, DatabaseSubTab.Hidden)}###DatabaseHiddenTab"))
		{
			activeSubTab = DatabaseSubTab.Hidden;
			this.DrawSection(
				overview.Hidden, this.hiddenFilterCache, ref this.hiddenPage, "DatabaseHidden",
				"No character with Character Data has come up empty on a Lodestone search yet.",
				"This character is hidden from search results.", overview.IsReloadingHidden,
				allowOverride: true);
			ImGui.EndTabItem();
		}

		if (ImGui.BeginTabItem(
			$"{this.TabLabel("Unseen", overview.Unseen.Count, DatabaseSubTab.Unseen)}###DatabaseUnseenTab"))
		{
			activeSubTab = DatabaseSubTab.Unseen;
			this.DrawSection(
				overview.Unseen, this.unseenFilterCache, ref this.unseenPage, "DatabaseUnseen",
				"No Lodestone match is known for a character without Character Data yet.",
				statusMessage: null, overview.IsReloadingUnseen);
			ImGui.EndTabItem();
		}

		ImGui.EndTabBar();

		if (activeSubTab is { } active && (justSelected || this.lastDrawnSubTab != active))
		{
			DatabaseTab.ReloadSubTab(overview, active);
		}

		this.lastDrawnSubTab = activeSubTab ?? this.lastDrawnSubTab;
	}

	private string TabLabel(string name, int count, DatabaseSubTab tab) =>
		this.lastDrawnSubTab == tab ? $"{name} ({count})" : name;

	private static void ReloadSubTab(DatabaseOverviewService overview, DatabaseSubTab tab)
	{
		switch (tab)
		{
			case DatabaseSubTab.Stalkers:
				overview.ReloadPinnedAsync();
				break;
			case DatabaseSubTab.Verified:
				overview.ReloadVerifiedAsync();
				break;
			case DatabaseSubTab.Unverified:
				overview.ReloadUnverifiedAsync();
				break;
			case DatabaseSubTab.Hidden:
				overview.ReloadHiddenAsync();
				break;
			case DatabaseSubTab.Unseen:
				overview.ReloadUnseenAsync();
				break;
		}
	}

	private static bool IsReloadingSubTab(DatabaseOverviewService overview, DatabaseSubTab tab) => tab switch
	{
		DatabaseSubTab.Stalkers => overview.IsReloadingPinned,
		DatabaseSubTab.Verified => overview.IsReloadingVerified,
		DatabaseSubTab.Unverified => overview.IsReloadingUnverified,
		DatabaseSubTab.Hidden => overview.IsReloadingHidden,
		DatabaseSubTab.Unseen => overview.IsReloadingUnseen,
		_ => false,
	};

	private void DrawFilterAndRefresh(DatabaseOverviewService overview)
	{
		var currentSubTab = this.lastDrawnSubTab;
		var isReloading = currentSubTab is { } tab && DatabaseTab.IsReloadingSubTab(overview, tab);

		FilterAndRefreshRow.Draw(
			ref this.nameFilter,
			onFilterChanged: () =>
			{
				this.pinnedPage = 0;
				this.verifiedPage = 0;
				this.unverifiedPage = 0;
				this.hiddenPage = 0;
				this.unseenPage = 0;
			},
			refreshEnabled: !isReloading,
			onRefresh: () =>
			{
				if (currentSubTab is { } activeTab)
				{
					DatabaseTab.ReloadSubTab(overview, activeTab);
				}
			},
			refreshIdSuffix: "DatabaseRefresh",
			busyMessage: isReloading ? "Refreshing..." : null);
	}

	private void DrawSection(
		IReadOnlyList<DatabaseEntryViewModel> entries,
		FilterCache<DatabaseEntryViewModel> filterCache,
		ref int page,
		string childId,
		string emptyMessage,
		string? statusMessage,
		bool isReloading,
		bool allowOverride = false)
	{
		var (pageItems, filteredCount, totalPages) = PagedList.Apply(
			filterCache, entries, this.nameFilter, entry => entry.Name, ref page, PageSize);

		if (filterCache.IsFiltering)
		{
			ImGui.TextColored(ImGuiColors.DalamudGrey3, "Filtering...");
		}

		if (filteredCount == 0)
		{
			if (isReloading)
			{
				ImGui.TextColored(ImGuiColors.DalamudGrey3, "Loading...");
			}
			else
			{
				ImGui.TextWrapped(entries.Count == 0 ? emptyMessage : "No characters match this filter.");
			}

			return;
		}

		PagedList.DrawScrollableList(
			childId, filteredCount, pageItems, "No characters match this filter.",
			entry => this.DrawEntry(entry, statusMessage, allowOverride));

		PagedList.DrawPager(ref page, totalPages, childId);
	}

	private void DrawEntry(
		DatabaseEntryViewModel entry, string? statusMessage, bool allowOverride)
	{
		var primaryLine = ProfileView.FormatPrimaryLine(
			entry.CharacterData, entry.Profile?.Value, entry.Name, entry.World, entry.DataCenter);
		var badge = ProfileView.FormatJobLine(
			entry.CharacterData, entry.Profile?.Value, entry.JobAbbreviation, entry.LevelText);

		var (rowStartX, rowStartY, availWidth) = RowOverlay.CaptureStart();

		var showsOverride = allowOverride && entry.CharacterData != null;

		MemberRow.Draw(
			plugin,
			new CharacterRowData(
				entry.CacheKey,
				entry.IsPinned,
				ProfileView.ResolveAvatarUrl(entry.Profile?.Value, entry.AvatarUrlHash, entry.World),
				primaryLine,
				badge,
				IsBusy: false,
				statusMessage,
				entry.Profile,
				entry.CharacterData,
				entry.CharacterDataAsOfUtc,
				entry.NameHistory,
				entry.IsVerified,
				entry.ProfileFetchedAtUtc,
				showsOverride));

		var rowEndY = ImGui.GetCursorPosY();

		if (showsOverride && entry.CharacterData is { } data)
		{
			this.DrawOverride(entry, data, rowStartX, rowStartY, availWidth);
		}

		RowOverlay.RestoreBelow(rowEndY);
	}

	private void DrawOverride(
		DatabaseEntryViewModel entry, PlayerLocalData data, float rowStartX, float rowStartY,
		float availWidth) =>
		LodestoneOverride.Draw(
			this.hiddenLodestoneIdInputs, entry.CacheKey, data.Name, data.FreeCompanyTag, entry.World,
			data.ContentId, plugin.NearbyCharactersService.QueueOverride, rowStartX, rowStartY,
			availWidth);
}
