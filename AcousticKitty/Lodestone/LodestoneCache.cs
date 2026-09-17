// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AcousticKitty.Common;
using SQLite;

namespace AcousticKitty.Lodestone;

public sealed partial class LodestoneCache : IDisposable
{
	private const int BulkQueryBatchSize = 500;

	private readonly SQLiteConnection connection;
	private readonly object gate = new();

	#region Public API

	public LodestoneCache(string pluginConfigDirectory)
	{
		Directory.CreateDirectory(pluginConfigDirectory);
		var databasePath = Path.Combine(pluginConfigDirectory, "lodestone-cache.db3");

		this.connection = new SQLiteConnection(databasePath);
		this.connection.CreateTable<ResolvedCharacterRow>();
		this.connection.CreateTable<CachedProfileRow>();
		DatabaseVacuum.RunInBackground(this.connection, this.gate);
	}

	public ulong? TryGetResolvedId(string key)
	{
		lock (this.gate)
		{
			var row = this.connection.Find<ResolvedCharacterRow>(key);
			return row != null ? (ulong)row.LodestoneId : null;
		}
	}

	public string? TryGetResolvedAvatarUrlHash(string key)
	{
		lock (this.gate)
		{
			return this.connection.Find<ResolvedCharacterRow>(key)?.AvatarUrlHash;
		}
	}

	public void DeleteCharacters(IEnumerable<string> keys)
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
					this.connection.Table<ResolvedCharacterRow>().Delete(row => chunkKeys.Contains(row.Key));
					this.connection.Table<CachedProfileRow>().Delete(row => chunkKeys.Contains(row.Key));
				}
			});
		}
	}

	public void SaveResolvedId(
		string key,
		ulong lodestoneId,
		string name,
		uint homeWorldId,
		DateTime resolvedAtUtc,
		string? avatarUrlHash = null,
		int? level = null,
		uint? jobId = null)
	{
		lock (this.gate)
		{
			this.UpsertResolvedRow(
				key, lodestoneId, name, homeWorldId, resolvedAtUtc, avatarUrlHash, level, jobId);
		}
	}

	public void SaveResolvedIds(
		IReadOnlyList<(
			string Key, ulong LodestoneId, string Name, uint HomeWorldId, DateTime ResolvedAtUtc,
			string? AvatarUrlHash, int? Level, uint? JobId)> updates)
	{
		if (updates.Count == 0)
		{
			return;
		}

		lock (this.gate)
		{
			this.connection.RunInTransaction(() =>
			{
				foreach (var update in updates)
				{
					this.UpsertResolvedRow(
						update.Key, update.LodestoneId, update.Name, update.HomeWorldId,
						update.ResolvedAtUtc, update.AvatarUrlHash, update.Level, update.JobId);
				}
			});
		}
	}

	private void UpsertResolvedRow(
		string key,
		ulong lodestoneId,
		string name,
		uint homeWorldId,
		DateTime resolvedAtUtc,
		string? avatarUrlHash,
		int? level,
		uint? jobId)
	{
		var existing = this.connection.Find<ResolvedCharacterRow>(key);
		var row = existing ?? new ResolvedCharacterRow { Key = key };
		row.LodestoneId = (long)lodestoneId;
		row.Name = name;
		row.HomeWorldId = homeWorldId;
		row.ResolvedAtUtc = UtcTimestamp.Format(resolvedAtUtc);
		row.AvatarUrlHash = avatarUrlHash ?? row.AvatarUrlHash;
		row.Level = level ?? row.Level;
		row.JobId = jobId.HasValue ? (long)jobId.Value : row.JobId;

		if (existing == null)
		{
			this.connection.Insert(row);
		}
		else
		{
			this.connection.Update(row);
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
				KeyedRowMigration.Migrate<ResolvedCharacterRow>(this.connection, oldKey, newKey, row =>
				{
					row.Key = newKey;
					row.Name = newName;
					row.HomeWorldId = newHomeWorldId;
				});
				KeyedRowMigration.Migrate<CachedProfileRow>(
					this.connection, oldKey, newKey, row => row.Key = newKey);
			});
		}
	}

	public (LodestoneProfile Profile, DateTime FetchedAtUtc)? TryGetCachedProfile(string key)
	{
		lock (this.gate)
		{
			var row = this.connection.Find<CachedProfileRow>(key);
			if (row == null)
			{
				return null;
			}

			var profile = JsonSerializer.Deserialize<LodestoneProfile>(row.ProfileJson);
			if (profile == null)
			{
				return null;
			}

			return (profile, UtcTimestamp.Parse(row.FetchedAtUtc));
		}
	}

	public bool SaveCachedProfile(string key, LodestoneProfile profile, DateTime fetchedAtUtc)
	{
		lock (this.gate)
		{
			var existing = this.connection.Find<CachedProfileRow>(key);
			LodestoneProfile? previous = null;
			if (existing != null)
			{
				previous = JsonSerializer.Deserialize<LodestoneProfile>(existing.ProfileJson);
				if (previous != null && ProfileRegressionGuard.IsRegression(profile, previous))
				{
					return false;
				}
			}

			if (profile.JobId == null && previous?.JobId != null)
			{
				profile = profile with { JobId = previous.JobId, Level = previous.Level };
			}

			this.connection.InsertOrReplace(new CachedProfileRow
			{
				Key = key,
				ProfileJson = JsonSerializer.Serialize(profile),
				FetchedAtUtc = UtcTimestamp.Format(fetchedAtUtc),
			});
			return true;
		}
	}

	public IReadOnlyList<CachedProfileEntry> GetAllCachedProfiles()
	{
		lock (this.gate)
		{
			var result = new List<CachedProfileEntry>();
			foreach (var row in this.connection.Table<CachedProfileRow>())
			{
				var json = row.ProfileJson;
				var profile = new Lazy<LodestoneProfile>(
					() => JsonSerializer.Deserialize<LodestoneProfile>(json)!, LazyThreadSafetyMode.None);
				result.Add(new CachedProfileEntry(row.Key, profile, UtcTimestamp.Parse(row.FetchedAtUtc)));
			}

			return result;
		}
	}

	public IReadOnlyList<ResolvedCharacterEntry> GetAllResolved()
	{
		lock (this.gate)
		{
			return this.connection.Table<ResolvedCharacterRow>()
				.Select(row => new ResolvedCharacterEntry(
					row.Key, row.Name, (uint)row.HomeWorldId, (ulong)row.LodestoneId,
					UtcTimestamp.Parse(row.ResolvedAtUtc), row.AvatarUrlHash,
					row.Level, row.JobId.HasValue ? (uint)row.JobId.Value : null))
				.ToArray();
		}
	}

	public IReadOnlyList<CachedProfileEntry> GetCachedProfilesForKeys(IEnumerable<string> keys)
	{
		var result = new List<CachedProfileEntry>();
		foreach (var chunk in keys.ToList().Chunk(BulkQueryBatchSize))
		{
			var chunkKeys = chunk.ToList();
			lock (this.gate)
			{
				foreach (var row in this.connection.Table<CachedProfileRow>()
					.Where(row => chunkKeys.Contains(row.Key)))
				{
					var json = row.ProfileJson;
					var profile = new Lazy<LodestoneProfile>(
						() => JsonSerializer.Deserialize<LodestoneProfile>(json)!, LazyThreadSafetyMode.None);
					result.Add(new CachedProfileEntry(row.Key, profile, UtcTimestamp.Parse(row.FetchedAtUtc)));
				}
			}
		}

		return result;
	}

	public IReadOnlyList<ResolvedCharacterEntry> GetResolvedForKeys(IEnumerable<string> keys)
	{
		var result = new List<ResolvedCharacterEntry>();
		foreach (var chunk in keys.ToList().Chunk(BulkQueryBatchSize))
		{
			var chunkKeys = chunk.ToList();
			lock (this.gate)
			{
				result.AddRange(this.connection.Table<ResolvedCharacterRow>()
					.Where(row => chunkKeys.Contains(row.Key))
					.Select(row => new ResolvedCharacterEntry(
						row.Key, row.Name, (uint)row.HomeWorldId, (ulong)row.LodestoneId,
						UtcTimestamp.Parse(row.ResolvedAtUtc), row.AvatarUrlHash,
						row.Level, row.JobId.HasValue ? (uint)row.JobId.Value : null)));
			}
		}

		return result;
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

	[Table("ResolvedCharacters")]
	private sealed class ResolvedCharacterRow
	{
		[PrimaryKey]
		public string Key { get; set; } = string.Empty;

		public long LodestoneId { get; set; }

		public string Name { get; set; } = string.Empty;

		public long HomeWorldId { get; set; }

		public string ResolvedAtUtc { get; set; } = string.Empty;

		public string? AvatarUrlHash { get; set; }

		public int? Level { get; set; }

		public long? JobId { get; set; }
	}

	[Table("CachedProfiles")]
	private sealed class CachedProfileRow
	{
		[PrimaryKey]
		public string Key { get; set; } = string.Empty;

		public string ProfileJson { get; set; } = string.Empty;

		public string FetchedAtUtc { get; set; } = string.Empty;
	}

	#endregion
}
