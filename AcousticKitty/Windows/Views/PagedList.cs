// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AcousticKitty.Windows.Views;

internal static class PagedList
{
	public static (IReadOnlyList<T> Page, int FilteredCount, int TotalPages) Apply<T>(
		FilterCache<T> cache,
		IReadOnlyList<T> entries,
		string nameFilter,
		Func<T, string> nameSelector,
		ref int page,
		int pageSize)
	{
		var filtered = cache.Filter(entries, nameFilter, nameSelector);

		var totalPages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
		page = Math.Clamp(page, 0, totalPages - 1);
		var pageItems = filtered.Skip(page * pageSize).Take(pageSize).ToArray();
		return (pageItems, filtered.Count, totalPages);
	}

	public static void DrawFilterAndPagerFooter(
		ref string filterText, ref int page, int totalPages, string idSuffix)
	{
		const float FilterBoxWidth = 200f;
		ImGui.SetNextItemWidth(FilterBoxWidth);
		if (ImGui.InputText($"Filter by name##{idSuffix}", ref filterText, 128))
		{
			page = 0;
		}

		ImGui.SameLine();
		PagedList.DrawPager(ref page, totalPages, idSuffix);
	}

	public static void DrawPager(ref int page, int totalPages, string idSuffix)
	{
		var pageLabel = $"Page {page + 1} / {totalPages}";
		var style = ImGui.GetStyle();
		var previousWidth = RightAlignedButton.MeasureButtonWidth("Previous");
		var nextWidth = RightAlignedButton.MeasureButtonWidth("Next");
		var labelWidth = ImGui.CalcTextSize(pageLabel).X;
		var totalWidth =
			previousWidth + style.ItemSpacing.X + labelWidth + style.ItemSpacing.X + nextWidth;

		var availWidth = ImGui.GetContentRegionAvail().X;
		ImGui.SetCursorPosX(
			ImGui.GetCursorPosX() + RightAlignedButton.RightAlignOffset(availWidth, totalWidth));

		using (ImRaii.Disabled(page <= 0))
		{
			if (ImGui.Button($"Previous##{idSuffix}Prev"))
			{
				page--;
			}
		}

		ImGui.SameLine();
		ImGui.TextUnformatted(pageLabel);
		ImGui.SameLine();
		using (ImRaii.Disabled(page >= totalPages - 1))
		{
			if (ImGui.Button($"Next##{idSuffix}Next"))
			{
				page++;
			}
		}
	}

	public static void DrawScrollableList<T>(
		string childId,
		int filteredCount,
		IReadOnlyList<T> pageItems,
		string emptyMessage,
		Action<T> drawItem)
	{
		var footerHeight = ImGui.GetFrameHeightWithSpacing();
		using (ImRaii.Child(childId, new Vector2(0, -footerHeight), true))
		{
			if (filteredCount == 0)
			{
				ImGui.TextWrapped(emptyMessage);
			}
			else
			{
				foreach (var item in pageItems)
				{
					drawItem(item);
					ImGui.Separator();
				}
			}
		}
	}
}

internal sealed class FilterCache<T>
{
	private const int AsyncScanThreshold = 20_000;

	private string previousFilter = string.Empty;
	private IReadOnlyList<T> previousSource = Array.Empty<T>();
	private IReadOnlyList<T> previousResult = Array.Empty<T>();
	private volatile bool isFiltering;
	private int requestVersion;

	public bool IsFiltering => this.isFiltering;

	public IReadOnlyList<T> Filter(
		IReadOnlyList<T> source, string filterText, Func<T, string> nameSelector)
	{
		if (string.IsNullOrEmpty(filterText))
		{
			this.requestVersion++;
			this.isFiltering = false;
			this.previousFilter = string.Empty;
			this.previousSource = source;
			this.previousResult = source;
			return source;
		}

		if (filterText == this.previousFilter && ReferenceEquals(source, this.previousSource))
		{
			return this.previousResult;
		}

		var extendsPrevious = !this.isFiltering &&
			ReferenceEquals(source, this.previousSource) &&
			this.previousFilter.Length > 0 &&
			filterText.StartsWith(this.previousFilter, StringComparison.OrdinalIgnoreCase);
		var searchBase = extendsPrevious ? this.previousResult : source;

		if (!extendsPrevious && searchBase.Count > AsyncScanThreshold)
		{
			this.StartAsyncScan(searchBase, source, filterText, nameSelector);
			return this.previousResult;
		}

		var filtered = FilterCache<T>.RunFilter(searchBase, filterText, nameSelector);
		this.previousFilter = filterText;
		this.previousSource = source;
		this.previousResult = filtered;
		return filtered;
	}

	private void StartAsyncScan(
		IReadOnlyList<T> searchBase, IReadOnlyList<T> source, string filterText,
		Func<T, string> nameSelector)
	{
		var version = ++this.requestVersion;
		this.isFiltering = true;
		this.previousFilter = filterText;
		this.previousSource = source;

		_ = Task.Run(() =>
		{
			var filtered = FilterCache<T>.RunFilter(searchBase, filterText, nameSelector);
			if (version == this.requestVersion)
			{
				this.previousResult = filtered;
				this.isFiltering = false;
			}
		});
	}

	private static IReadOnlyList<T> RunFilter(
		IReadOnlyList<T> searchBase, string filterText, Func<T, string> nameSelector) =>
		searchBase
			.Where(item => FilterCache<T>.MatchesEitherNamePart(nameSelector(item), filterText))
			.ToArray();

	private static bool MatchesEitherNamePart(string fullName, string filter)
	{
		if (fullName.AsSpan().StartsWith(filter, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		var spaceIndex = fullName.IndexOf(' ');
		return spaceIndex >= 0 &&
			fullName.AsSpan(spaceIndex + 1).StartsWith(filter, StringComparison.OrdinalIgnoreCase);
	}
}
