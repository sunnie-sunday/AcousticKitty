// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
	private bool disposed;

	#region Public API

	public EchoStore(string pluginConfigDirectory, IPluginLog log)
	{
		this.log = log;
		Directory.CreateDirectory(pluginConfigDirectory);
		var databasePath = Path.Combine(pluginConfigDirectory, "verification-queue.db3");

		this.connection = new SQLiteConnection(databasePath);
		this.connection.CreateTable<PinRow>();
		this.connection.CreateTable<VerifiedRow>();
		this.connection.CreateTable<PinnedProfileRow>();
		DatabaseVacuum.RunInBackground(this.connection, this.gate, () => this.disposed);
	}

	public void Pin(ulong lodestoneId, string name, uint homeWorldId) =>
		this.Write(() => this.connection.InsertOrReplace(new PinRow
		{
			Key = EchoStore.KeyFor(lodestoneId),
			Name = name,
			HomeWorldId = homeWorldId,
		}));

	public bool IsPinned(ulong lodestoneId) =>
		this.Read(() => this.connection.Find<PinRow>(EchoStore.KeyFor(lodestoneId)) != null, false);

	public bool IsVerified(ulong lodestoneId) =>
		this.Read(() => this.connection.Find<VerifiedRow>(EchoStore.KeyFor(lodestoneId)) != null, false);

	public IReadOnlyList<PinnedCharacter> GetAllPinned() =>
		this.Read<IReadOnlyList<PinnedCharacter>>(
			() =>
			{
				var stopwatch = Stopwatch.StartNew();
				var result = this.connection.Table<PinRow>()
					.Select(pin => (pin, verified: this.connection.Find<VerifiedRow>(pin.Key)))
					.Where(row => row.verified != null)
					.Select(row => new PinnedCharacter(
						EchoStore.ParseKey(row.pin.Key), row.pin.Name, (uint)row.pin.HomeWorldId, null, null,
						UtcTimestamp.Parse(row.verified!.VerifiedAtUtc)))
					.ToArray();

				this.log.Verbose(
					$"EchoStore.GetAllPinned: {result.Length} pinned character(s) in " +
					$"{stopwatch.ElapsedMilliseconds} ms.");
				return result;
			},
			Array.Empty<PinnedCharacter>());

	public void MarkVerified(ulong lodestoneId, DateTime verifiedAtUtc) =>
		this.Write(() => this.connection.InsertOrReplace(new VerifiedRow
		{
			Key = EchoStore.KeyFor(lodestoneId),
			VerifiedAtUtc = UtcTimestamp.Format(verifiedAtUtc),
		}));

	public void PinAndMarkVerified(
		ulong lodestoneId, string name, uint homeWorldId, DateTime verifiedAtUtc) =>
		this.Write(() => this.connection.RunInTransaction(() =>
		{
			var key = EchoStore.KeyFor(lodestoneId);
			this.connection.InsertOrReplace(new PinRow
			{
				Key = key,
				Name = name,
				HomeWorldId = homeWorldId,
			});
			this.connection.InsertOrReplace(new VerifiedRow
			{
				Key = key,
				VerifiedAtUtc = UtcTimestamp.Format(verifiedAtUtc),
			});
		}));

	public IReadOnlySet<ulong> GetAllVerifiedLodestoneIds() =>
		this.Read<IReadOnlySet<ulong>>(
			() => this.connection.Table<VerifiedRow>()
				.Select(row => row.Key)
				.ToList()
				.Select(EchoStore.TryParseKey)
				.Where(id => id != null)
				.Select(id => id!.Value)
				.ToHashSet(),
			new HashSet<ulong>());

	public IReadOnlySet<ulong> GetAllPinnedLodestoneIds() =>
		this.GetAllPinnedLodestoneIds(this.GetAllVerifiedLodestoneIds());

	public IReadOnlySet<ulong> GetAllPinnedLodestoneIds(IReadOnlySet<ulong> verifiedIds) =>
		this.Read<IReadOnlySet<ulong>>(
			() => this.connection.Table<PinRow>()
				.Select(row => row.Key)
				.ToList()
				.Select(EchoStore.TryParseKey)
				.Where(id => id != null && verifiedIds.Contains(id!.Value))
				.Select(id => id!.Value)
				.ToHashSet(),
			new HashSet<ulong>());

	public IReadOnlyDictionary<ulong, DateTime> GetAllVerified() =>
		this.Read<IReadOnlyDictionary<ulong, DateTime>>(
			() =>
			{
				var result = new Dictionary<ulong, DateTime>();
				foreach (var row in this.connection.Table<VerifiedRow>())
				{
					if (EchoStore.TryParseKey(row.Key) is { } lodestoneId)
					{
						result[lodestoneId] = UtcTimestamp.Parse(row.VerifiedAtUtc);
					}
				}

				return result;
			},
			new Dictionary<ulong, DateTime>());

	public void DeleteVerified(IEnumerable<ulong> lodestoneIds)
	{
		var keyList = lodestoneIds.Select(EchoStore.KeyFor).ToList();
		if (keyList.Count == 0)
		{
			return;
		}

		this.Write(() => this.connection.RunInTransaction(() =>
		{
			foreach (var chunk in keyList.Chunk(BulkQueryBatchSize))
			{
				var chunkKeys = chunk.ToList();
				this.connection.Table<VerifiedRow>().Delete(row => chunkKeys.Contains(row.Key));
			}
		}));
	}

	public void SavePinnedProfileSnapshot(
		ulong lodestoneId, LodestoneProfile profile, DateTime? fetchedAtUtc) =>
		this.Write(() => this.connection.InsertOrReplace(new PinnedProfileRow
		{
			Key = EchoStore.KeyFor(lodestoneId),
			ProfileJson = JsonSerializer.Serialize(profile),
			FetchedAtUtc = fetchedAtUtc.HasValue ? UtcTimestamp.Format(fetchedAtUtc.Value) : null,
		}));

	public PinnedProfileSnapshot? TryGetPinnedProfileSnapshot(ulong lodestoneId) =>
		this.Read<PinnedProfileSnapshot?>(
			() =>
			{
				var row = this.connection.Find<PinnedProfileRow>(EchoStore.KeyFor(lodestoneId));
				if (row == null ||
					JsonSerializer.Deserialize<LodestoneProfile>(row.ProfileJson) is not { } profile)
				{
					return null;
				}

				var fetchedAtUtc = string.IsNullOrEmpty(row.FetchedAtUtc)
					? (DateTime?)null
					: UtcTimestamp.Parse(row.FetchedAtUtc);
				return new PinnedProfileSnapshot(profile, fetchedAtUtc);
			},
			null);

	public void MigrateKeysToLodestoneId(Func<string, ulong?> resolveLodestoneId) =>
		this.Write(() => this.connection.RunInTransaction(() =>
		{
			this.MigrateVerifiedRows(resolveLodestoneId);
			this.MigratePinRows(resolveLodestoneId);
		}));

	public void Dispose()
	{
		lock (this.gate)
		{
			this.disposed = true;
			this.connection.Dispose();
		}
	}

	#endregion

	#region Migration

	private T Read<T>(Func<T> body, T whenDisposed)
	{
		lock (this.gate)
		{
			return this.disposed ? whenDisposed : body();
		}
	}

	private void Write(Action body)
	{
		lock (this.gate)
		{
			if (this.disposed)
			{
				return;
			}

			body();
		}
	}

	private void MigrateVerifiedRows(Func<string, ulong?> resolveLodestoneId)
	{
		var newestByKey = new Dictionary<string, VerifiedRow>();
		var staleKeys = new List<string>();

		foreach (var row in this.connection.Table<VerifiedRow>().ToList())
		{
			if (!row.Key.Contains('@'))
			{
				continue;
			}

			staleKeys.Add(row.Key);
			if (resolveLodestoneId(row.Key) is not { } lodestoneId)
			{
				this.log.Warning($"Echo migration: dropping unresolvable verified key '{row.Key}'.");
				continue;
			}

			var newKey = EchoStore.KeyFor(lodestoneId);
			if (!newestByKey.TryGetValue(newKey, out var existing) ||
				string.CompareOrdinal(row.VerifiedAtUtc, existing.VerifiedAtUtc) > 0)
			{
				newestByKey[newKey] = new VerifiedRow { Key = newKey, VerifiedAtUtc = row.VerifiedAtUtc };
			}
		}

		foreach (var key in staleKeys)
		{
			this.connection.Delete<VerifiedRow>(key);
		}

		foreach (var row in newestByKey.Values)
		{
			this.connection.InsertOrReplace(row);
		}
	}

	private void MigratePinRows(Func<string, ulong?> resolveLodestoneId)
	{
		var migratedByKey = new Dictionary<string, PinRow>();
		var staleKeys = new List<string>();

		foreach (var row in this.connection.Table<PinRow>().ToList())
		{
			if (!row.Key.Contains('@'))
			{
				continue;
			}

			if (resolveLodestoneId(row.Key) is not { } lodestoneId)
			{
				this.log.Warning(
					$"Echo migration: keeping unresolvable pinned key '{row.Key}' unchanged.");
				continue;
			}

			staleKeys.Add(row.Key);
			var newKey = EchoStore.KeyFor(lodestoneId);
			migratedByKey[newKey] =
				new PinRow { Key = newKey, Name = row.Name, HomeWorldId = row.HomeWorldId };
		}

		foreach (var key in staleKeys)
		{
			this.connection.Delete<PinRow>(key);
		}

		foreach (var row in migratedByKey.Values)
		{
			this.connection.InsertOrReplace(row);
		}
	}

	private static string KeyFor(ulong lodestoneId) =>
		lodestoneId.ToString(CultureInfo.InvariantCulture);

	private static ulong ParseKey(string key) => EchoStore.TryParseKey(key) ?? 0UL;

	private static ulong? TryParseKey(string key) =>
		ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var lodestoneId)
			? lodestoneId
			: null;

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

	[Table("PinnedProfiles")]
	private sealed class PinnedProfileRow
	{
		[PrimaryKey]
		public string Key { get; set; } = string.Empty;

		public string ProfileJson { get; set; } = string.Empty;

		public string? FetchedAtUtc { get; set; }
	}

	#endregion
}
