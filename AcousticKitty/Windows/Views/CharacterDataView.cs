// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using AcousticKitty.Character;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;

namespace AcousticKitty.Windows.Views;

internal static class CharacterDataView
{
	public static void Draw(
		PlayerLocalData data, DateTime? lastSeenOrFetchedUtc, LodestoneProfile? compareProfile = null,
		Lazy<IReadOnlyList<NameHistoryEntry>>? nameHistory = null)
	{
		var dataManager = Plugin.DataManager;

		ProfileView.DrawField("Content ID", data.ContentId.ToString());
		ProfileView.DrawField(
			"Name", data.Name,
			compareProfile != null && ProfileView.NameDrifted(data, compareProfile));
		ProfileView.DrawField(
			"Home World", GameDataResolver.ResolveWorldName(dataManager, data.HomeWorldId),
			compareProfile != null && ProfileView.HomeWorldDrifted(data, compareProfile));

		if (nameHistory != null)
		{
			var otherNames = nameHistory.Value
				.Where(entry => !string.IsNullOrEmpty(entry.Name))
				.Select(entry => $"{entry.Name}@{entry.HomeWorldName}")
				.ToArray();
			if (otherNames.Length > 0)
			{
				ProfileView.DrawField("Also known as", string.Join(", ", otherNames));
			}
		}
		ProfileView.DrawField(
			"Location seen", GameDataResolver.ResolveTerritoryName(dataManager, data.TerritoryId));
		ProfileView.DrawField(
			"World seen", GameDataResolver.ResolveWorldName(dataManager, data.CurrentWorldId));

		if (lastSeenOrFetchedUtc is { } timestamp)
		{
			ProfileView.DrawField("Time seen", DisplayFormat.Timestamp(timestamp));
		}

		if (GameDataResolver.ResolveTitleName(dataManager, data.TitleId, data.Gender) is { } title)
		{
			ProfileView.DrawField("Title", $"<{title}>");
		}

		ProfileView.DrawField(
			"Job",
			$"{GameDataResolver.ResolveJobAbbreviation(dataManager, data.JobId)} (Lv. {data.Level})",
			compareProfile != null && ProfileView.JobDrifted(data, compareProfile));

		if (data.FreeCompanyTag is { } tag)
		{
			ProfileView.DrawField("Free Company Tag", $"«{tag}»");
		}
	}
}
