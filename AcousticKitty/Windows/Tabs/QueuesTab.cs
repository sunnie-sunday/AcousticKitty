// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using AcousticKitty.Character;
using AcousticKitty.Echo;
using AcousticKitty.Lodestone;
using AcousticKitty.Windows.Views;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace AcousticKitty.Windows.Tabs;

internal sealed class QueuesTab(Plugin plugin)
{
	private const int PageSize = 500;

	private string lodestoneNameFilter = string.Empty;
	private int lodestonePage;

	private readonly FilterCache<NearbyMemberViewModel> lodestoneFilterCache = new();

	public void Draw()
	{
		if (!ImGui.BeginTabBar("QueuesSubTabs"))
		{
			return;
		}

		var nearby = plugin.NearbyCharactersService;
		var queueCount = nearby.Queue.Count + plugin.ProfileRefresh.PendingCount;
		var queueCountLabel = nearby.QueueMightHaveMore ? $"{queueCount}+" : queueCount.ToString();
		if (ImGui.BeginTabItem($"Lodestone ({queueCountLabel})###QueuesLodestoneTab"))
		{
			this.DrawLodestoneQueue(nearby);
			ImGui.EndTabItem();
		}

		ImGui.EndTabBar();
	}

	private void DrawLodestoneQueue(NearbyCharactersService nearby)
	{
		var isManual = plugin.Configuration.NearbySearchMode == NearbySearchMode.Manual;
		var isDraining = nearby.IsDraining;

		if (isDraining)
		{
			ImGui.TextColored(ImGuiColors.SuccessForeground, "Lodestone search active...");
		}
		else
		{
			ImGui.TextColored(ImGuiColors.ErrorForeground, "Lodestone search stopped.");
		}

		ImGui.SameLine();

		if (isManual)
		{
			RightAlignedButton.Draw(
				isDraining ? "Stop Lodestone queue" : "Start Lodestone queue",
				true,
				isDraining ? nearby.StopSearch : nearby.RunSearchOnce,
				"QueueRunSearch");
		}
		else
		{
			RightAlignedButton.Draw(
				"Auto Lodestone queue", false, nearby.RunSearchOnce, "QueueRunSearch");
		}

		this.DrawQueue(
			nearby.Queue, "The Lodestone search queue is empty.", this.lodestoneFilterCache,
			ref this.lodestoneNameFilter, ref this.lodestonePage,
			member => member.KnownCharacter.Data.Name, "LodestoneQueue", QueuesTab.DrawLodestoneRow,
			() =>
			{
				QueuesTab.DrawActiveFreeCompanyRows(plugin.GroupSearchService);
				QueuesTab.DrawProfileRefreshRows(plugin.ProfileRefresh);
			});
	}

	private static void DrawProfileRefreshRows(ProfileRefresh profileRefresh)
	{
		foreach (var (pin, worldName) in profileRefresh.Pending)
		{
			ImGui.TextUnformatted($"{pin.Name} @ {worldName}");
			ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Lodestone ID {pin.LodestoneId}");
			ImGui.Separator();
		}
	}

	private static void DrawActiveFreeCompanyRows(GroupSearchService groupSearchService)
	{
		foreach (var entry in groupSearchService.ActiveFreeCompanyRosterFetches)
		{
			ImGui.TextUnformatted(entry.FreeCompanyName);
			ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Free Company ID: {entry.FreeCompanyId}");
			ImGui.Separator();
		}
	}

	private void DrawQueue<T>(
		IReadOnlyList<T> items,
		string emptyMessage,
		FilterCache<T> filterCache,
		ref string nameFilter,
		ref int page,
		Func<T, string> nameSelector,
		string idSuffix,
		Action<T> drawRow,
		Action? drawPinnedRows = null)
	{
		ImGui.Spacing();

		if (items.Count == 0 && drawPinnedRows == null)
		{
			ImGui.TextWrapped(emptyMessage);
			return;
		}

		var (pageItems, filteredCount, totalPages) =
			PagedList.Apply(filterCache, items, nameFilter, nameSelector, ref page, PageSize);

		PagedList.DrawScrollableList(
			idSuffix, filteredCount, pageItems, "No characters match this filter.", drawRow, drawPinnedRows,
			virtualizeUniformRows: true);

		PagedList.DrawFilterAndPagerFooter(ref nameFilter, ref page, totalPages, idSuffix);
	}

	private static void DrawLodestoneRow(NearbyMemberViewModel member)
	{
		var data = member.KnownCharacter.Data;
		ImGui.TextUnformatted($"{data.Name} @ {member.WorldName}");
		ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Content ID {data.ContentId}");
	}
}
