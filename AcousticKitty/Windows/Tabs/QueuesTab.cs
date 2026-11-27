// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;
using AcousticKitty.Windows.Views;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace AcousticKitty.Windows.Tabs;

internal sealed class QueuesTab(Plugin plugin)
{
	private const int PageSize = 100;

	private string profileNameFilter = string.Empty;
	private int profilePage;
	private string rosterNameFilter = string.Empty;
	private int rosterPage;

	private readonly FilterCache<ProfileQueueRow> profileFilterCache = new();
	private readonly FilterCache<(ulong FreeCompanyId, string FreeCompanyName)> rosterFilterCache =
		new();

	private IReadOnlyList<NearbyMemberViewModel>? profileRowsQueue;
	private int profileRowsPinCount = -1;
	private IReadOnlyList<ProfileQueueRow> profileRows = Array.Empty<ProfileQueueRow>();

	public void Draw()
	{
		if (!ImGui.BeginTabBar("QueuesSubTabs"))
		{
			return;
		}

		var nearby = plugin.NearbyCharactersService;
		var profileCount = nearby.Queue.Count + plugin.ProfileRefresh.PendingCount;
		var profileCountLabel =
			nearby.QueueMightHaveMore ? $"{profileCount}+" : profileCount.ToString();
		if (ImGui.BeginTabItem($"Profiles ({profileCountLabel})###QueuesProfilesTab"))
		{
			this.DrawProfileQueue(nearby);
			ImGui.EndTabItem();
		}

		var rosterFetches = plugin.GroupSearchService.ActiveFreeCompanyRosterFetches;
		if (ImGui.BeginTabItem($"Rosters ({rosterFetches.Count})###QueuesRostersTab"))
		{
			QueuesTab.DrawQueue(
				rosterFetches, "No roster is being fetched.", this.rosterFilterCache,
				ref this.rosterNameFilter, ref this.rosterPage, fetch => fetch.FreeCompanyName,
				"RosterQueue", QueuesTab.DrawRosterRow);
			ImGui.EndTabItem();
		}

		ImGui.EndTabBar();
	}

	private void DrawProfileQueue(NearbyCharactersService nearby)
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

		QueuesTab.DrawQueue(
			this.GetProfileRows(nearby), "The Lodestone profile queue is empty.",
			this.profileFilterCache, ref this.profileNameFilter, ref this.profilePage,
			row => row.Name, "ProfileQueue", QueuesTab.DrawProfileRow);
	}

	private IReadOnlyList<ProfileQueueRow> GetProfileRows(NearbyCharactersService nearby)
	{
		var queue = nearby.Queue;
		var pinCount = plugin.ProfileRefresh.PendingCount;
		if (ReferenceEquals(queue, this.profileRowsQueue) && pinCount == this.profileRowsPinCount)
		{
			return this.profileRows;
		}

		this.profileRows = plugin.ProfileRefresh.Pending
			.Select(pending => new ProfileQueueRow(
				pending.Pin.Name, pending.WorldName, "Lodestone ID", pending.Pin.LodestoneId))
			.Concat(queue.Select(member => new ProfileQueueRow(
				member.KnownCharacter.Data.Name, member.WorldName, "Content ID",
				member.KnownCharacter.Data.ContentId)))
			.Take(NearbyCharactersService.MaxQueueSnapshotSize)
			.ToArray();
		this.profileRowsQueue = queue;
		this.profileRowsPinCount = pinCount;
		return this.profileRows;
	}

	private static void DrawQueue<T>(
		IReadOnlyList<T> items,
		string emptyMessage,
		FilterCache<T> filterCache,
		ref string nameFilter,
		ref int page,
		Func<T, string> nameSelector,
		string idSuffix,
		Action<T> drawRow)
	{
		ImGui.Spacing();

		if (items.Count == 0)
		{
			ImGui.TextWrapped(emptyMessage);
			return;
		}

		var (pageItems, filteredCount, totalPages) =
			PagedList.Apply(filterCache, items, nameFilter, nameSelector, ref page, PageSize);

		PagedList.DrawScrollableList(
			idSuffix, filteredCount, pageItems, "No entries match this filter.", drawRow,
			virtualizeUniformRows: true);

		PagedList.DrawFilterAndPagerFooter(ref nameFilter, ref page, totalPages, idSuffix);
	}

	private static void DrawProfileRow(ProfileQueueRow row)
	{
		ImGui.TextUnformatted($"{row.Name} @ {row.WorldName}");
		ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{row.IdLabel} {row.Id}");
	}

	private static void DrawRosterRow((ulong FreeCompanyId, string FreeCompanyName) fetch)
	{
		ImGui.TextUnformatted(fetch.FreeCompanyName);
		ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Free Company ID: {fetch.FreeCompanyId}");
	}

	private sealed record ProfileQueueRow(string Name, string WorldName, string IdLabel, ulong Id);
}
