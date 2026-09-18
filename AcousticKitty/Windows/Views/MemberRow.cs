// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Numerics;
using AcousticKitty.Character;
using AcousticKitty.Lodestone;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;

namespace AcousticKitty.Windows.Views;

internal sealed record CharacterRowData(
	string Id,
	bool IsPinned,
	string? AvatarUrl,
	string PrimaryLine,
	string? BadgeLine,
	bool IsBusy,
	string? StatusMessage,
	Lazy<LodestoneProfile>? Profile,
	PlayerLocalData? CharacterData,
	DateTime? CharacterDataAsOfUtc,
	Lazy<IReadOnlyList<NameHistoryEntry>>? NameHistory = null,
	bool IsVerified = false,
	DateTime? ProfileAsOfUtc = null);

internal static class MemberRow
{
	private static readonly Vector4 VerifiedBackground = new(57f / 255f, 70f / 255f, 52f / 255f, 0.4f);

	public static void Draw(Plugin plugin, CharacterRowData row)
	{
		var drawList = ImGui.GetWindowDrawList();
		var rowStart = ImGui.GetCursorScreenPos();
		var rowWidth = ImGui.GetContentRegionAvail().X;

		var background = row.IsPinned ? ImGuiColors.ErrorBackground
			: row.IsVerified ? MemberRow.VerifiedBackground
			: (Vector4?)null;

		if (background != null)
		{
			drawList.ChannelsSplit(2);
			drawList.ChannelsSetCurrent(1);
		}

		ProfileView.DrawAvatar(plugin, row.AvatarUrl, 64f);

		ImGui.SameLine();
		ImGui.BeginGroup();
		ImGui.TextUnformatted(row.PrimaryLine);
		if (row.BadgeLine != null)
		{
			ImGui.TextColored(ImGuiColors.DalamudGrey3, row.BadgeLine);
		}

		ImGui.EndGroup();

		if (row.IsBusy)
		{
			ImGui.TextColored(ImGuiColors.DalamudGrey3, "Fetching...");
		}
		else if (row.StatusMessage != null)
		{
			ImGui.TextColored(ImGuiColors.ErrorForeground, row.StatusMessage);
		}

		if (row.CharacterData != null && ImGui.CollapsingHeader($"Character Data##{row.Id}"))
		{
			using (ImRaii.PushIndent(20f))
			{
				ImGui.Spacing();
				CharacterDataView.Draw(
					row.CharacterData, row.CharacterDataAsOfUtc, row.Profile?.Value, row.NameHistory);
			}
		}

		if (row.Profile != null && ImGui.CollapsingHeader($"Lodestone Profile##{row.Id}"))
		{
			using (ImRaii.PushIndent(20f))
			{
				ImGui.Spacing();
				ProfileView.DrawCollapsedBody(
				row.Profile.Value, row.CharacterData, row.NameHistory, row.ProfileAsOfUtc);
			}
		}

		if (background is { } color)
		{
			var rowEnd = new Vector2(rowStart.X + rowWidth, ImGui.GetCursorScreenPos().Y);
			drawList.ChannelsSetCurrent(0);
			drawList.AddRectFilled(rowStart, rowEnd, ImGui.ColorConvertFloat4ToU32(color));
			drawList.ChannelsMerge();
		}
	}
}
