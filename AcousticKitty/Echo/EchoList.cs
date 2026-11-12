// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcousticKitty.Common;
using Dalamud.Plugin.Services;
using SQLite;

namespace AcousticKitty.Echo;

public sealed class EchoList
{
	public const string FileName = "stalkers.db3";

	private readonly IReadOnlyDictionary<ulong, PinnedCharacter> byContentId;
	private readonly IReadOnlyDictionary<ulong, PinnedCharacter> byLodestoneId;
	private readonly IReadOnlySet<string> nameWorldKeys;

	public EchoList(string assemblyDirectory, Func<string, uint?> resolveWorldId, IPluginLog log)
	{
		var pins = EchoList.Load(Path.Combine(assemblyDirectory, FileName), resolveWorldId, log);
		this.All = pins;
		this.byContentId = pins
			.Where(pin => pin.ContentId is not null)
			.GroupBy(pin => pin.ContentId!.Value)
			.ToDictionary(group => group.Key, group => group.First());
		this.byLodestoneId = pins
			.GroupBy(pin => pin.LodestoneId)
			.ToDictionary(group => group.Key, group => group.First());
		this.nameWorldKeys = pins.Select(pin => pin.NameWorldKey).ToHashSet();
		log.Information($"Loaded {pins.Count} pinned character(s) from {FileName}.");
	}

	public IReadOnlyList<PinnedCharacter> All { get; }

	public bool IsPinned(ulong contentId, ulong? lodestoneId) =>
		this.byContentId.ContainsKey(contentId) ||
		(lodestoneId is { } id && this.byLodestoneId.ContainsKey(id));

	public bool IsPinnedLodestoneId(ulong lodestoneId) =>
		this.byLodestoneId.ContainsKey(lodestoneId);

	public bool IsPinnedNameWorldKey(string nameWorldKey) =>
		this.nameWorldKeys.Contains(nameWorldKey);

	public PinnedCharacter? TryGetByContentId(ulong contentId) =>
		this.byContentId.GetValueOrDefault(contentId);

	private static IReadOnlyList<PinnedCharacter> Load(
		string databasePath, Func<string, uint?> resolveWorldId, IPluginLog log)
	{
		if (!File.Exists(databasePath))
		{
			log.Warning($"{FileName} is missing next to the plugin; no character is pinned.");
			return Array.Empty<PinnedCharacter>();
		}

		List<SunnieSundayReport.PinRow> rows;
		try
		{
			var connectionString =
				new SQLiteConnectionString(databasePath, SQLiteOpenFlags.ReadOnly, true);
			using var connection = new SQLiteConnection(connectionString);
			rows = connection.Table<SunnieSundayReport.PinRow>().ToList();
		}
		catch (Exception ex)
		{
			log.Error(ex, $"Failed to read {FileName}; no character is pinned.");
			return Array.Empty<PinnedCharacter>();
		}

		var pins = new List<PinnedCharacter>(rows.Count);
		foreach (var row in rows)
		{
			if (resolveWorldId(row.HomeWorld) is not { } homeWorldId)
			{
				log.Warning($"{FileName}: skipping {row.Name}, unknown world '{row.HomeWorld}'.");
				continue;
			}

			pins.Add(new PinnedCharacter(
				row.ContentId is { } contentId ? (ulong)contentId : null,
				(ulong)row.LodestoneId,
				CharacterKey.Build(row.Name, homeWorldId),
				row.Name,
				homeWorldId));
		}

		return pins;
	}
}
