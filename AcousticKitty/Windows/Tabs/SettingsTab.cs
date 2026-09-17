// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using AcousticKitty.Character;
using Dalamud.Bindings.ImGui;

namespace AcousticKitty.Windows.Tabs;

internal sealed class SettingsTab(Plugin plugin)
{
	public void Draw()
	{
		var configuration = plugin.Configuration;

		ImGui.TextUnformatted("Lodestone connection");

		var userAgentBuffer = configuration.LodestoneUserAgent;
		if (ImGui.InputText("User-Agent", ref userAgentBuffer, 256))
		{
			configuration.LodestoneUserAgent = userAgentBuffer;
			configuration.Save();
		}

		ImGui.BeginDisabled();
		ImGui.TextWrapped(
			$"Left blank, falls back to \"{Configuration.DefaultLodestoneUserAgent}\".");
		ImGui.EndDisabled();

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextWrapped("Choose how the Queue tab resolves nearby players on the Lodestone.");
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
		}

		ImGui.BeginDisabled();
		ImGui.TextWrapped(
			"Manual looks up characters when using \"Start Lodestone queue\" on the Queue tab.");
		ImGui.TextWrapped("Auto looks up every nearby player passively in the background.");
		ImGui.EndDisabled();
	}
}
