// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Character;
using AcousticKitty.Common;
using Dalamud.Bindings.ImGui;

namespace AcousticKitty.Windows.Tabs;

internal sealed class SettingsTab(Plugin plugin)
{
	private static readonly DatabaseCacheTier[] CacheSizeTiers =
		[DatabaseCacheTier.Small, DatabaseCacheTier.Medium, DatabaseCacheTier.Large];
	private static readonly string[] CacheSizeTierNames =
		["12,500 (Small)", "25,000 (Medium)", "50,000 (Large)"];

	private int cacheSizeTierIndex =
		Array.IndexOf(CacheSizeTiers, plugin.Configuration.DatabaseCacheTier);

	public void Draw()
	{
		var configuration = plugin.Configuration;

		ImGui.TextUnformatted("Cache size");
		if (ImGui.Combo(
			"##DatabaseCacheTier", ref this.cacheSizeTierIndex, CacheSizeTierNames,
			CacheSizeTierNames.Length))
		{
			configuration.DatabaseCacheTier = CacheSizeTiers[this.cacheSizeTierIndex];
			configuration.Save();
		}

		ImGui.BeginDisabled();
		ImGui.TextWrapped(
			"How many characters the Database tabs will cache before evicting the oldest.");
		ImGui.EndDisabled();

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextUnformatted("Lodestone queue mode");
		ImGui.Spacing();

		if (ImGui.RadioButton(
			"Manual", configuration.NearbySearchMode == NearbySearchMode.Manual))
		{
			configuration.NearbySearchMode = NearbySearchMode.Manual;
			configuration.Save();
		}

		ImGui.SameLine();
		if (ImGui.RadioButton("Auto", configuration.NearbySearchMode == NearbySearchMode.Auto))
		{
			configuration.NearbySearchMode = NearbySearchMode.Auto;
			configuration.Save();
			plugin.NearbyCharactersService.RunSearchOnce();
		}

		ImGui.BeginDisabled();
		ImGui.TextWrapped(
			"Manual looks up characters when using \"Start Lodestone queue\" on the Queue tab.");
		ImGui.TextWrapped("Auto looks up every nearby player passively in the background.");
		ImGui.EndDisabled();
	}
}
