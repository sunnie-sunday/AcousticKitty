// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using AcousticKitty.Character;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AcousticKitty.Common;

public static class GameDataResolver
{
	private static readonly Dictionary<uint, string> WorldIdToNameCache = new();
	private static readonly object WorldIdToNameGate = new();

	public static string ResolveWorldName(IDataManager dataManager, uint worldId)
	{
		lock (WorldIdToNameGate)
		{
			if (WorldIdToNameCache.TryGetValue(worldId, out var cached))
			{
				return cached;
			}

			var name =
				dataManager.GetExcelSheet<World>(ClientLanguage.English).GetRowOrDefault(worldId)?.Name
					.ToString()
				?? $"World #{worldId}";
			WorldIdToNameCache[worldId] = name;
			return name;
		}
	}

	private static readonly Dictionary<uint, string> DataCenterIdToNameCache = new();
	private static readonly object DataCenterIdToNameGate = new();

	public static string ResolveDataCenterName(IDataManager dataManager, uint worldId)
	{
		var dataCenterRowId = dataManager.GetExcelSheet<World>(ClientLanguage.English)
			.GetRowOrDefault(worldId)?.DataCenter.RowId;
		return dataCenterRowId is { } rowId
			? ResolveDataCenterNameById(dataManager, rowId)
			: $"World #{worldId}";
	}

	public static string ResolveDataCenterNameById(IDataManager dataManager, uint dataCenterId)
	{
		lock (DataCenterIdToNameGate)
		{
			if (DataCenterIdToNameCache.TryGetValue(dataCenterId, out var cached))
			{
				return cached;
			}

			var name = dataManager.GetExcelSheet<WorldDCGroupType>(ClientLanguage.English)
				.GetRowOrDefault(dataCenterId)?.Name.ToString()
				?? $"Data Center #{dataCenterId}";
			DataCenterIdToNameCache[dataCenterId] = name;
			return name;
		}
	}

	private static Dictionary<string, uint>? worldNameToIdCache;
	private static readonly object WorldNameToIdGate = new();

	public static uint? TryResolveWorldId(IDataManager dataManager, string worldName)
	{
		lock (WorldNameToIdGate)
		{
			worldNameToIdCache ??= dataManager.GetExcelSheet<World>(ClientLanguage.English)
				.Where(world => !string.IsNullOrEmpty(world.Name.ToString()))
				.GroupBy(world => world.Name.ToString())
				.ToDictionary(group => group.Key, group => group.First().RowId);

			return worldNameToIdCache.TryGetValue(worldName, out var id) ? id : null;
		}
	}

	private static Dictionary<string, uint>? dataCenterNameToIdCache;
	private static readonly object DataCenterNameToIdGate = new();

	public static uint? TryResolveDataCenterId(IDataManager dataManager, string dataCenterName)
	{
		lock (DataCenterNameToIdGate)
		{
			dataCenterNameToIdCache ??= dataManager.GetExcelSheet<WorldDCGroupType>(ClientLanguage.English)
				.Where(dataCenter => !string.IsNullOrEmpty(dataCenter.Name.ToString()))
				.GroupBy(dataCenter => dataCenter.Name.ToString())
				.ToDictionary(group => group.Key, group => group.First().RowId);

			return dataCenterNameToIdCache.TryGetValue(dataCenterName, out var id) ? id : null;
		}
	}

	private static readonly Dictionary<uint, string> JobIdToAbbreviationCache = new();
	private static readonly object JobIdToAbbreviationGate = new();

	public static string ResolveJobAbbreviation(IDataManager dataManager, uint jobId)
	{
		lock (JobIdToAbbreviationGate)
		{
			if (JobIdToAbbreviationCache.TryGetValue(jobId, out var cached))
			{
				return cached;
			}

			var abbreviation = dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English)
				.GetRowOrDefault(jobId)?.Abbreviation.ToString()
				?? $"Job #{jobId}";
			JobIdToAbbreviationCache[jobId] = abbreviation;
			return abbreviation;
		}
	}

	private static Dictionary<string, uint>? jobAbbreviationToIdCache;
	private static readonly object JobAbbreviationToIdGate = new();

	public static uint? TryResolveJobId(IDataManager dataManager, string abbreviation)
	{
		lock (JobAbbreviationToIdGate)
		{
			jobAbbreviationToIdCache ??= dataManager.GetExcelSheet<ClassJob>(ClientLanguage.English)
				.Where(job => !string.IsNullOrEmpty(job.Abbreviation.ToString()))
				.GroupBy(job => job.Abbreviation.ToString())
				.ToDictionary(group => group.Key, group => group.First().RowId);

			return jobAbbreviationToIdCache.TryGetValue(abbreviation, out var id) ? id : null;
		}
	}

	public static string? ResolveTitleName(IDataManager dataManager, uint titleId, string gender)
	{
		if (titleId == 0)
		{
			return null;
		}

		var row = dataManager.GetExcelSheet<Title>(ClientLanguage.English).GetRowOrDefault(titleId);
		if (row == null)
		{
			return null;
		}

		var text = gender == "F" ? row.Value.Feminine : row.Value.Masculine;
		return text.ToString();
	}

	private static Dictionary<(string Text, string Gender), uint>? titleGenderedNameToIdCache;
	private static Dictionary<string, uint>? titleNameToIdCache;
	private static readonly object TitleNameToIdGate = new();

	public static uint? TryResolveTitleId(IDataManager dataManager, string titleName, string gender)
	{
		lock (TitleNameToIdGate)
		{
			if (titleGenderedNameToIdCache == null)
			{
				titleGenderedNameToIdCache = new Dictionary<(string, string), uint>();
				titleNameToIdCache = new Dictionary<string, uint>();
				foreach (var row in dataManager.GetExcelSheet<Title>(ClientLanguage.English))
				{
					var masculine = row.Masculine.ToString();
					var feminine = row.Feminine.ToString();
					if (!string.IsNullOrEmpty(masculine))
					{
						titleGenderedNameToIdCache.TryAdd((masculine, "M"), row.RowId);
						titleNameToIdCache.TryAdd(masculine, row.RowId);
					}

					if (!string.IsNullOrEmpty(feminine))
					{
						titleGenderedNameToIdCache.TryAdd((feminine, "F"), row.RowId);
						titleNameToIdCache.TryAdd(feminine, row.RowId);
					}
				}
			}

			if (titleGenderedNameToIdCache.TryGetValue((titleName, gender), out var id))
			{
				return id;
			}

			return titleNameToIdCache!.TryGetValue(titleName, out var fallbackId) ? fallbackId : null;
		}
	}

	public static string ResolveTerritoryName(IDataManager dataManager, uint territoryId)
	{
		var territory =
			dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English).GetRowOrDefault(territoryId);
		var placeName = territory?.PlaceName.ValueNullable?.Name.ToString();
		return string.IsNullOrEmpty(placeName) ? $"Territory #{territoryId}" : placeName;
	}

	public static IReadOnlyList<string> GetAllPublicWorldNames(IDataManager dataManager) =>
		dataManager.GetExcelSheet<World>(ClientLanguage.English)
			.Where(world => world.IsPublic && !string.IsNullOrEmpty(world.Name.ToString()))
			.Select(world => world.Name.ToString())
			.ToArray();

	public static IReadOnlyList<string> GetAllDataCenterNames(IDataManager dataManager)
	{
		var dataCentersWithPublicWorlds = dataManager.GetExcelSheet<World>(ClientLanguage.English)
			.Where(world => world.IsPublic)
			.Select(world => world.DataCenter.RowId)
			.ToHashSet();

		return dataManager.GetExcelSheet<WorldDCGroupType>(ClientLanguage.English)
			.Where(dataCenter =>
				!dataCenter.IsCloud && dataCentersWithPublicWorlds.Contains(dataCenter.RowId))
			.Select(dataCenter => dataCenter.Name.ToString())
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}
}
