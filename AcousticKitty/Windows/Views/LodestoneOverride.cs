// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

namespace AcousticKitty.Windows.Views;

internal static class LodestoneOverride
{
	public static void Draw(
		Dictionary<string, string> inputs,
		string inputKey,
		string characterName,
		string? freeCompanyTag,
		string worldName,
		ulong contentId,
		Action<ulong, ulong> queueWithLodestoneId,
		float rowStartX,
		float rowStartY,
		float availWidth)
	{
		if (!inputs.TryGetValue(inputKey, out var idInput))
		{
			idInput = string.Empty;
		}

		const float InputWidth = 100f;
		const string QueueLabel = "Queue";
		var buttonWidth = ImGui.CalcTextSize(QueueLabel).X + (ImGui.GetStyle().FramePadding.X * 2);
		var totalWidth = InputWidth + ImGui.GetStyle().ItemSpacing.X + buttonWidth;

		RowOverlay.MoveToRightEdge(rowStartX, rowStartY, availWidth, totalWidth);
		if (ImGui.Button($"Google web search##{inputKey}", new Vector2(totalWidth, 0)))
		{
			var query =
				Uri.EscapeDataString($"\"{characterName}\" site:finalfantasyxiv.com/lodestone/");
			Util.OpenLink($"https://www.google.com/search?q={query}");

			if (!string.IsNullOrEmpty(freeCompanyTag))
			{
				var fcQuery = Uri.EscapeDataString(
					$"site:finalfantasyxiv.com \"«{freeCompanyTag}»\" \"{worldName}\"");
				Util.OpenLink($"https://www.google.com/search?q={fcQuery}");
			}
		}

		var queueRowY = ImGui.GetCursorPosY();
		RowOverlay.MoveToRightEdge(rowStartX, queueRowY, availWidth, totalWidth);

		ImGui.SetNextItemWidth(InputWidth);
		ImGui.InputTextWithHint(
			$"##LodestoneId{inputKey}", "Lodestone ID", ref idInput, 32,
			ImGuiInputTextFlags.CharsDecimal);
		inputs[inputKey] = idInput;

		ImGui.SameLine();
		var canQueue = ulong.TryParse(idInput, out var lodestoneId) && lodestoneId != 0;
		using (ImRaii.Disabled(!canQueue))
		{
			if (ImGui.Button($"{QueueLabel}##{inputKey}"))
			{
				queueWithLodestoneId(contentId, lodestoneId);
				inputs.Remove(inputKey);
			}
		}
	}
}
