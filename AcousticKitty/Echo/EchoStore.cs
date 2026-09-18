// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;
using Dalamud.Plugin.Services;
using SQLite;

namespace AcousticKitty.Echo;

public sealed class EchoStore : IDisposable
{
	private const int BulkQueryBatchSize = 500;

	private readonly SQLiteConnection connection;
	private readonly object gate = new();
	private readonly IPluginLog log;

	#region Public API

	public EchoStore(string pluginConfigDirectory, IPluginLog log)
	{
		this.log = log;
		Directory.CreateDirectory(pluginConfigDirectory);
		var databasePath = Path.Combine(pluginConfigDirectory, "verification-queue.db3");

		this.connection = new SQLiteConnection(databasePath);
		this.connection.CreateTable<PinRow>();
		this.connection.CreateTable<VerifiedRow>();
		DatabaseVacuum.RunInBackground(this.connection, this.gate);
	}

	public void Pin(string key, string name, uint homeWorldId)
	{
		lock (this.gate)
		{
			this.connection.InsertOrReplace(new PinRow
			{
				Key = key,
				Name = name,
				HomeWorldId = homeWorldId,
			});
		}
	}

	public bool IsPinned(string key)
	{
		lock (this.gate)
		{
			return this.connection.Find<PinRow>(key) != null;
		}
	}

	public bool IsVerified(string key)
	{
		lock (this.gate)
		{
			return this.connection.Find<VerifiedRow>(key) != null;
		}
	}

	public IReadOnlyList<PinnedCharacter> GetAllPinned()
	{
		lock (this.gate)
		{
			var stopwatch = Stopwatch.StartNew();
			var result = this.connection.Table<PinRow>()
				.Select(pin => (pin, verified: this.connection.Find<VerifiedRow>(pin.Key)))
				.Where(row => row.verified != null)
				.Select(row => new PinnedCharacter(
					row.pin.Key, row.pin.Name, (uint)row.pin.HomeWorldId, null, null,
					UtcTimestamp.Parse(row.verified!.VerifiedAtUtc)))
				.ToArray();

			this.log.Verbose(
				$"EchoStore.GetAllPinned: {result.Length} pinned character(s) in " +
				$"{stopwatch.ElapsedMilliseconds} ms.");
			return result;
		}
	}

	public void MarkVerified(string key, DateTime verifiedAtUtc)
	{
		lock (this.gate)
		{
			this.connection.InsertOrReplace(new VerifiedRow
			{
				Key = key,
				VerifiedAtUtc = UtcTimestamp.Format(verifiedAtUtc),
			});
		}
	}

	public IReadOnlySet<string> GetAllVerifiedKeys()
	{
		lock (this.gate)
		{
			return this.connection.Table<VerifiedRow>().Select(row => row.Key).ToHashSet();
		}
	}

	public IReadOnlyDictionary<string, DateTime> GetAllVerified()
	{
		lock (this.gate)
		{
			return this.connection.Table<VerifiedRow>()
				.ToDictionary(row => row.Key, row => UtcTimestamp.Parse(row.VerifiedAtUtc));
		}
	}

	public void DeleteVerified(IEnumerable<string> keys)
	{
		var keyList = keys.ToList();
		if (keyList.Count == 0)
		{
			return;
		}

		lock (this.gate)
		{
			this.connection.RunInTransaction(() =>
			{
				foreach (var chunk in keyList.Chunk(BulkQueryBatchSize))
				{
					var chunkKeys = chunk.ToList();
					this.connection.Table<VerifiedRow>().Delete(row => chunkKeys.Contains(row.Key));
				}
			});
		}
	}

	public void RenameCharacter(string oldKey, string newKey, string newName, uint newHomeWorldId)
	{
		if (oldKey == newKey)
		{
			return;
		}

		lock (this.gate)
		{
			this.connection.RunInTransaction(() =>
			{
				KeyedRowMigration.Migrate<PinRow>(this.connection, oldKey, newKey, row =>
				{
					row.Key = newKey;
					row.Name = newName;
					row.HomeWorldId = newHomeWorldId;
				});
				KeyedRowMigration.Migrate<VerifiedRow>(
					this.connection, oldKey, newKey, row => row.Key = newKey);
			});
		}
	}

	public void Dispose()
	{
		lock (this.gate)
		{
			this.connection.Dispose();
		}
	}

	#endregion

	#region Row Types

	[Table("Pins")]
	private sealed class PinRow
	{
		[PrimaryKey]
		public string Key { get; set; } = string.Empty;

		public string Name { get; set; } = string.Empty;

		public long HomeWorldId { get; set; }
	}

	[Table("Verified")]
	private sealed class VerifiedRow
	{
		[PrimaryKey]
		public string Key { get; set; } = string.Empty;

		public string VerifiedAtUtc { get; set; } = string.Empty;
	}

	#endregion
}
