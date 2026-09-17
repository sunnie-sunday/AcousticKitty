// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using AcousticKitty.Common;
using SQLite;

namespace AcousticKitty.Character;

internal sealed partial class NameHistoryStore
{
	private readonly SQLiteConnection connection;
	private readonly object gate;

	public NameHistoryStore(SQLiteConnection connection, object gate)
	{
		this.connection = connection;
		this.gate = gate;
		this.connection.CreateTable<NameHistoryRow>();
	}

	public void RecordNameHistory(
		ulong contentId, string name, string homeWorldName, DateTime seenUntilUtc)
	{
		lock (this.gate)
		{
			this.connection.Insert(new NameHistoryRow
			{
				ContentId = (long)contentId,
				Name = name,
				HomeWorldName = homeWorldName,
				SeenUntilUtc = UtcTimestamp.Format(seenUntilUtc),
			});
		}
	}

	public IReadOnlyList<string> GetHistoryWorldNames(ulong contentId)
	{
		lock (this.gate)
		{
			return this.connection.Table<NameHistoryRow>()
				.Where(row => row.ContentId == (long)contentId)
				.Select(row => row.HomeWorldName)
				.ToList();
		}
	}

	public IReadOnlyList<NameHistoryEntry> GetNameHistory(ulong contentId)
	{
		lock (this.gate)
		{
			return this.connection.Table<NameHistoryRow>()
				.Where(row => row.ContentId == (long)contentId)
				.OrderByDescending(row => row.Id)
				.Select(row => new NameHistoryEntry(
					row.Name, row.HomeWorldName, UtcTimestamp.Parse(row.SeenUntilUtc)))
				.ToArray();
		}
	}

	public void DeleteForContentIds(IReadOnlyList<long> contentIds)
	{
		lock (this.gate)
		{
			this.connection.Table<NameHistoryRow>().Delete(row => contentIds.Contains(row.ContentId));
		}
	}

	[Table("NameHistory")]
	private sealed class NameHistoryRow
	{
		[PrimaryKey]
		[AutoIncrement]
		public int Id { get; set; }

		[Indexed]
		public long ContentId { get; set; }
		public string Name { get; set; } = string.Empty;
		public string HomeWorldName { get; set; } = string.Empty;
		public string SeenUntilUtc { get; set; } = string.Empty;
	}
}
