// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using AcousticKitty.Character;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;
using AcousticKitty.Echo;
using AcousticKitty.Windows.Views;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace AcousticKitty.Windows.Tabs;

internal sealed class QueuesTab(Plugin plugin)
{
	private const int PageSize = 500;

	private string lodestoneNameFilter = string.Empty;
	private int lodestonePage;
	private string echoNameFilter = string.Empty;
	private int echoPage;

	private readonly FilterCache<NearbyMemberViewModel> lodestoneFilterCache = new();
	private readonly FilterCache<PendingMatch> echoFilterCache = new();

	public void Draw()
	{
		if (!ImGui.BeginTabBar("QueuesSubTabs"))
		{
			return;
		}

		var nearby = plugin.NearbyCharactersService;
		var queueCountLabel =
			nearby.QueueMightHaveMore ? $"{nearby.Queue.Count}+" : nearby.Queue.Count.ToString();
		if (ImGui.BeginTabItem($"Lodestone ({queueCountLabel})###QueuesLodestoneTab"))
		{
			this.DrawLodestoneQueue(nearby);
			ImGui.EndTabItem();
		}

		var echo = plugin.EchoService;
		if (ImGui.BeginTabItem($"Echo ({echo.PendingVerifyCount})###QueuesEchoTab"))
		{
			this.DrawEchoQueue(echo, echo.PendingVerifications);
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
			() => QueuesTab.DrawActiveFreeCompanyRows(plugin.GroupSearchService));
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

	private void DrawEchoQueue(
		EchoService echo, IReadOnlyList<PendingMatch> pendingVerifications)
	{
		var isManual = plugin.Configuration.EchoQueueMode == EchoQueueMode.Manual;

		switch (echo.State)
		{
			case EchoQueueState.Idle:
				ImGui.TextColored(ImGuiColors.DalamudGrey3, "Echo: nothing to verify.");
				break;
			case EchoQueueState.Verifying:
				ImGui.TextColored(ImGuiColors.SuccessForeground, "Echo: verifying...");
				break;
			case EchoQueueState.WaitingToRetry:
				ImGui.TextColored(
					ImGuiColors.ErrorForeground, echo.StatusMessage ?? "Echo: waiting to retry...");
				break;
			case EchoQueueState.Paused:
				ImGui.TextColored(ImGuiColors.ErrorForeground, "Echo queue stopped.");
				break;
		}

		ImGui.SameLine();

		if (isManual)
		{
			RightAlignedButton.Draw(
				echo.IsProcessing ? "Stop Echo queue" : "Start Echo queue",
				true,
				echo.IsProcessing ? echo.StopQueue : echo.StartQueue,
				"QueueEchoRun");
		}
		else
		{
			RightAlignedButton.Draw("Auto Echo queue", false, echo.StartQueue, "QueueEchoRun");
		}

		this.DrawQueue(
			pendingVerifications, "No Echo verifications are pending.", this.echoFilterCache,
			ref this.echoNameFilter, ref this.echoPage, verify => verify.CharacterName,
			"EchoQueue", QueuesTab.DrawEchoRow);
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

	private static void DrawEchoRow(PendingMatch verify)
	{
		var worldName = GameDataResolver.ResolveWorldName(Plugin.DataManager, verify.HomeWorldId);
		ImGui.TextUnformatted($"{verify.CharacterName} @ {worldName}");
		ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Lodestone ID {verify.LodestoneId}");
	}
}
