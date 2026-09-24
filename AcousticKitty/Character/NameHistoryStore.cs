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
	private readonly Func<bool> isDisposed;

	public NameHistoryStore(SQLiteConnection connection, object gate, Func<bool> isDisposed)
	{
		this.connection = connection;
		this.gate = gate;
		this.isDisposed = isDisposed;
		this.connection.CreateTable<NameHistoryRow>();
	}

	public void RecordNameHistory(
		ulong contentId, string name, string homeWorldName, DateTime seenUntilUtc) =>
		this.Write(() => this.connection.Insert(new NameHistoryRow
		{
			ContentId = (long)contentId,
			Name = name,
			HomeWorldName = homeWorldName,
			SeenUntilUtc = UtcTimestamp.Format(seenUntilUtc),
		}));

	public IReadOnlyList<string> GetHistoryWorldNames(ulong contentId) =>
		this.Read<IReadOnlyList<string>>(
			() => this.connection.Table<NameHistoryRow>()
				.Where(row => row.ContentId == (long)contentId)
				.Select(row => row.HomeWorldName)
				.ToList(),
			Array.Empty<string>());

	public IReadOnlyList<NameHistoryEntry> GetNameHistory(ulong contentId) =>
		this.Read<IReadOnlyList<NameHistoryEntry>>(
			() => this.connection.Table<NameHistoryRow>()
				.Where(row => row.ContentId == (long)contentId)
				.OrderByDescending(row => row.Id)
				.Select(row => new NameHistoryEntry(
					row.Name, row.HomeWorldName, UtcTimestamp.Parse(row.SeenUntilUtc)))
				.ToArray(),
			Array.Empty<NameHistoryEntry>());

	public void DeleteForContentIds(IReadOnlyList<long> contentIds) =>
		this.Write(() =>
			this.connection.Table<NameHistoryRow>().Delete(row => contentIds.Contains(row.ContentId)));

	private T Read<T>(Func<T> body, T whenDisposed)
	{
		lock (this.gate)
		{
			return this.isDisposed() ? whenDisposed : body();
		}
	}

	private void Write(Action body)
	{
		lock (this.gate)
		{
			if (this.isDisposed())
			{
				return;
			}

			body();
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
