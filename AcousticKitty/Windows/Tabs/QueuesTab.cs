// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Character;
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
		var nearby = plugin.NearbyCharactersService;
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

		this.DrawQueue(nearby);
	}

	private void DrawQueue(NearbyCharactersService nearby)
	{
		ImGui.Spacing();

		if (nearby.Queue.Count == 0)
		{
			ImGui.TextWrapped("The Lodestone search queue is empty.");
			return;
		}

		var (pageItems, filteredCount, totalPages) = PagedList.Apply(
			this.lodestoneFilterCache, nearby.Queue, this.lodestoneNameFilter,
			member => member.KnownCharacter.Data.Name, ref this.lodestonePage, PageSize);

		PagedList.DrawScrollableList(
			"LodestoneQueue", filteredCount, pageItems, "No characters match this filter.",
			QueuesTab.DrawLodestoneRow);

		PagedList.DrawFilterAndPagerFooter(
			ref this.lodestoneNameFilter, ref this.lodestonePage, totalPages, "LodestoneQueue");
	}

	private static void DrawLodestoneRow(NearbyMemberViewModel member)
	{
		var data = member.KnownCharacter.Data;
		ImGui.TextUnformatted($"{data.Name} @ {member.WorldName}");
		ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Content ID {data.ContentId}");
	}
}
