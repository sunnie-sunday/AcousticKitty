// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using SQLite;

namespace AcousticKitty.Echo;

public sealed class EchoList
{
	public const string FileName = "echo.db3";

	private readonly IReadOnlyDictionary<ulong, PinnedCharacter> byContentId;
	private readonly IReadOnlyDictionary<ulong, PinnedCharacter> byLodestoneId;
	private readonly IReadOnlySet<string> nameWorldKeys;

	public EchoList(string assemblyDirectory, IPluginLog log)
	{
		var pins = EchoList.Load(Path.Combine(assemblyDirectory, FileName), log);
		this.All = pins;
		this.byContentId = pins
			.GroupBy(pin => pin.ContentId)
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

	public bool IsPinnedLodestoneId(ulong lodestoneId) => this.byLodestoneId.ContainsKey(lodestoneId);

	public bool IsPinnedNameWorldKey(string nameWorldKey) => this.nameWorldKeys.Contains(nameWorldKey);

	public PinnedCharacter? TryGetByContentId(ulong contentId) =>
		this.byContentId.GetValueOrDefault(contentId);

	private static IReadOnlyList<PinnedCharacter> Load(string databasePath, IPluginLog log)
	{
		if (!File.Exists(databasePath))
		{
			log.Warning($"{FileName} is missing next to the plugin; no character is pinned.");
			return Array.Empty<PinnedCharacter>();
		}

		List<PinRow> rows;
		try
		{
			var connectionString =
				new SQLiteConnectionString(databasePath, SQLiteOpenFlags.ReadOnly, true);
			using var connection = new SQLiteConnection(connectionString);
			rows = connection.Table<PinRow>().ToList();
		}
		catch (Exception ex)
		{
			log.Error(ex, $"Failed to read {FileName}; no character is pinned.");
			return Array.Empty<PinnedCharacter>();
		}

		var pins = new List<PinnedCharacter>(rows.Count);
		foreach (var row in rows)
		{
			var separator = row.NameWorldKey.LastIndexOf('@');
			if (separator <= 0 ||
				!uint.TryParse(
					row.NameWorldKey.AsSpan(separator + 1), NumberStyles.None,
					CultureInfo.InvariantCulture, out var homeWorldId))
			{
				log.Warning($"{FileName}: skipping malformed key '{row.NameWorldKey}'.");
				continue;
			}

			pins.Add(new PinnedCharacter(
				(ulong)row.ContentId, (ulong)row.LodestoneId, row.NameWorldKey,
				row.NameWorldKey[..separator], homeWorldId));
		}

		return pins;
	}

	[Table("Pins")]
	private sealed class PinRow
	{
		[PrimaryKey]
		public long ContentId { get; set; }

		public string NameWorldKey { get; set; } = string.Empty;

		[Indexed]
		public long LodestoneId { get; set; }
	}
}
