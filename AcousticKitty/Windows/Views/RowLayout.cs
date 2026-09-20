// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;

namespace AcousticKitty.Windows.Views;

internal static class RightAlignedButton
{
	public static void Draw(string label, bool enabled, Action onClick, string idSuffix)
	{
		var availWidth = ImGui.GetContentRegionAvail().X;
		var buttonWidth = RightAlignedButton.MeasureButtonWidth(label);
		ImGui.SetCursorPosX(
			ImGui.GetCursorPosX() + RightAlignedButton.RightAlignOffset(availWidth, buttonWidth));
		using (ImRaii.Disabled(!enabled))
		{
			if (ImGui.Button($"{label}##{idSuffix}"))
			{
				onClick();
			}
		}
	}

	public static float MeasureButtonWidth(string label) =>
		ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2);

	public static float RightAlignOffset(float availWidth, float contentWidth) =>
		MathF.Max(0f, availWidth - contentWidth);
}

internal static class StatusBadge
{
	private readonly record struct BadgeStyle(string Label, Vector4 Background);

	public static void DrawOverlay(
		string id, bool isPinned, bool isVerified, bool isQueued, Vector2 rowStart, float rowWidth)
	{
		if (StatusBadge.Resolve(isPinned, isVerified, isQueued) is not { } style)
		{
			return;
		}

		var restore = ImGui.GetCursorScreenPos();
		var width = RightAlignedButton.MeasureButtonWidth(style.Label);
		ImGui.SetCursorScreenPos(new Vector2(rowStart.X + rowWidth - width, rowStart.Y));

		using (ImRaii.PushColor(ImGuiCol.Button, style.Background)
			.Push(ImGuiCol.ButtonHovered, style.Background)
			.Push(ImGuiCol.ButtonActive, style.Background))
		{
			ImGui.Button($"{style.Label}##status{id}");
		}

		ImGui.SetCursorScreenPos(restore);
	}

	private static BadgeStyle? Resolve(bool isPinned, bool isVerified, bool isQueued) =>
		isPinned ? new BadgeStyle("Echo contributor", ImGuiColors.ErrorBackground)
		: isVerified ? new BadgeStyle("Audit passed", ImGuiColors.SuccessBackground)
		: isQueued ? new BadgeStyle("Queued", ImGuiColors.InfoBackground)
		: null;
}

internal static class RowOverlay
{
	public static (float StartX, float StartY, float AvailWidth) CaptureStart() =>
		(ImGui.GetCursorPosX(), ImGui.GetCursorPosY(), ImGui.GetContentRegionAvail().X);

	public static void MoveToRightEdge(float startX, float y, float availWidth, float width) =>
		ImGui.SetCursorPos(new Vector2(startX + availWidth - width, y));

	public static void RestoreBelow(float rowEndY) =>
		ImGui.SetCursorPosY(MathF.Max(rowEndY, ImGui.GetCursorPosY()));
}

internal static class FilterAndRefreshRow
{
	public static void Draw(
		ref string filterText,
		Action? onFilterChanged,
		bool refreshEnabled,
		Action onRefresh,
		string refreshIdSuffix,
		string? busyMessage,
		(string Label, Action OnClick)? extraButton = null)
	{
		const float FilterBoxWidth = 200f;
		const float ButtonSpacing = 8f;

		ImGui.SetNextItemWidth(FilterBoxWidth);
		if (ImGui.InputText("Filter by name", ref filterText, 128))
		{
			onFilterChanged?.Invoke();
		}

		ImGui.SameLine();

		var availWidth = ImGui.GetContentRegionAvail().X;
		var refreshWidth = RightAlignedButton.MeasureButtonWidth("Refresh");
		var totalWidth = refreshWidth;
		if (extraButton is { } extra)
		{
			totalWidth += RightAlignedButton.MeasureButtonWidth(extra.Label) + ButtonSpacing;
		}

		ImGui.SetCursorPosX(
			ImGui.GetCursorPosX() + RightAlignedButton.RightAlignOffset(availWidth, totalWidth));

		if (extraButton is { } extraToDraw)
		{
			if (ImGui.Button($"{extraToDraw.Label}##{refreshIdSuffix}Extra"))
			{
				extraToDraw.OnClick();
			}

			ImGui.SameLine();
		}

		using (ImRaii.Disabled(!refreshEnabled))
		{
			if (ImGui.Button($"Refresh##{refreshIdSuffix}"))
			{
				onRefresh();
			}
		}

		if (busyMessage != null)
		{
			ImGui.TextColored(ImGuiColors.DalamudGrey3, busyMessage);
		}
	}
}
