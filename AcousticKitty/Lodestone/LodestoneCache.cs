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
	private bool disposed;

	#region Public API

	public LodestoneCache(string pluginConfigDirectory)
	{
		Directory.CreateDirectory(pluginConfigDirectory);
		var databasePath = Path.Combine(pluginConfigDirectory, "lodestone-cache.db3");

		this.connection = new SQLiteConnection(databasePath);
		this.connection.CreateTable<ResolvedCharacterRow>();
		this.connection.CreateTable<CachedProfileRow>();
		DatabaseVacuum.RunInBackground(this.connection, this.gate, () => this.disposed);
	}

	public ulong? TryGetResolvedId(string key) =>
		this.Read<ulong?>(
			() =>
			{
				var row = this.connection.Find<ResolvedCharacterRow>(key);
				return row != null ? (ulong)row.LodestoneId : null;
			},
			null);

	public string? TryGetResolvedAvatarUrlHash(string key) =>
		this.Read<string?>(
			() => this.connection.Find<ResolvedCharacterRow>(key)?.AvatarUrlHash,
			null);

	public ResolvedCharacterEntry? TryGetResolved(string key) =>
		this.Read<ResolvedCharacterEntry?>(
			() =>
			{
				var row = this.connection.Find<ResolvedCharacterRow>(key);
				return row == null ? null : new ResolvedCharacterEntry(
					row.Key, row.Name, (uint)row.HomeWorldId, (ulong)row.LodestoneId,
					UtcTimestamp.Parse(row.ResolvedAtUtc), row.AvatarUrlHash, row.Level,
					row.JobId.HasValue ? (uint)row.JobId.Value : null,
					row.FreeCompanyId.HasValue ? (ulong)row.FreeCompanyId.Value : null,
					row.FreeCompanyName);
			},
			null);

	public void DeleteCharacters(IEnumerable<string> keys)
	{
		var keyList = keys.ToList();
		if (keyList.Count == 0)
		{
			return;
		}

		this.Write(() => this.connection.RunInTransaction(() =>
		{
			foreach (var chunk in keyList.Chunk(BulkQueryBatchSize))
			{
				var chunkKeys = chunk.ToList();
				this.connection.Table<ResolvedCharacterRow>().Delete(row => chunkKeys.Contains(row.Key));
				this.connection.Table<CachedProfileRow>().Delete(row => chunkKeys.Contains(row.Key));
			}
		}));
	}

	public void SaveResolvedId(
		string key,
		ulong lodestoneId,
		string name,
		uint homeWorldId,
		DateTime resolvedAtUtc,
		string? avatarUrlHash = null,
		int? level = null,
		uint? jobId = null,
		ulong? freeCompanyId = null,
		string? freeCompanyName = null) =>
		this.Write(() => this.UpsertResolvedRow(
			key, lodestoneId, name, homeWorldId, resolvedAtUtc, avatarUrlHash, level, jobId,
			freeCompanyId, freeCompanyName));

	public void SaveResolvedIds(
		IReadOnlyList<(
			string Key, ulong LodestoneId, string Name, uint HomeWorldId, DateTime ResolvedAtUtc,
			string? AvatarUrlHash, int? Level, uint? JobId, ulong? FreeCompanyId,
			string? FreeCompanyName)> updates)
	{
		if (updates.Count == 0)
		{
			return;
		}

		this.Write(() => this.connection.RunInTransaction(() =>
		{
			foreach (var update in updates)
			{
				this.UpsertResolvedRow(
					update.Key, update.LodestoneId, update.Name, update.HomeWorldId,
					update.ResolvedAtUtc, update.AvatarUrlHash, update.Level, update.JobId,
					update.FreeCompanyId, update.FreeCompanyName);
			}
		}));
	}

	private void UpsertResolvedRow(
		string key,
		ulong lodestoneId,
		string name,
		uint homeWorldId,
		DateTime resolvedAtUtc,
		string? avatarUrlHash,
		int? level,
		uint? jobId,
		ulong? freeCompanyId,
		string? freeCompanyName)
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
		row.FreeCompanyId = freeCompanyId.HasValue ? (long)freeCompanyId.Value : row.FreeCompanyId;
		row.FreeCompanyName = freeCompanyName ?? row.FreeCompanyName;

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

		this.Write(() => this.connection.RunInTransaction(() =>
		{
			KeyedRowMigration.Migrate<ResolvedCharacterRow>(this.connection, oldKey, newKey, row =>
			{
				row.Key = newKey;
				row.Name = newName;
				row.HomeWorldId = newHomeWorldId;
			});
			KeyedRowMigration.Migrate<CachedProfileRow>(
				this.connection, oldKey, newKey, row => row.Key = newKey);
		}));
	}

	public (LodestoneProfile Profile, DateTime FetchedAtUtc)? TryGetCachedProfile(string key) =>
		this.Read<(LodestoneProfile Profile, DateTime FetchedAtUtc)?>(
			() =>
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
			},
			null);

	public (LodestoneProfile? Profile, DateTime? FetchedAtUtc) ResolveProfileOrPartial(string key)
	{
		var cached = this.TryGetCachedProfile(key);
		if (cached != null)
		{
			return (cached.Value.Profile, cached.Value.FetchedAtUtc);
		}

		var resolved = this.TryGetResolved(key);
		return resolved != null
			? (LodestoneProfile.BuildPartial(
				resolved.LodestoneId, resolved.Name, resolved.HomeWorldId, resolved.AvatarUrlHash,
				resolved.FreeCompanyId, resolved.FreeCompanyName, resolved.JobId, resolved.Level), null)
			: (null, null);
	}

	public bool SaveCachedProfile(string key, LodestoneProfile profile, DateTime fetchedAtUtc) =>
		this.Read(
			() =>
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
			},
			false);

	public IReadOnlyList<CachedProfileEntry> GetAllCachedProfiles() =>
		this.Read<IReadOnlyList<CachedProfileEntry>>(
			() =>
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
			},
			Array.Empty<CachedProfileEntry>());

	public int CountResolved() =>
		this.Read(() => this.connection.Table<ResolvedCharacterRow>().Count(), 0);

	public IReadOnlyList<ResolvedCharacterEntry> GetAllResolved() =>
		this.Read<IReadOnlyList<ResolvedCharacterEntry>>(
			() => this.connection.Table<ResolvedCharacterRow>()
				.Select(row => new ResolvedCharacterEntry(
					row.Key, row.Name, (uint)row.HomeWorldId, (ulong)row.LodestoneId,
					UtcTimestamp.Parse(row.ResolvedAtUtc), row.AvatarUrlHash,
					row.Level, row.JobId.HasValue ? (uint)row.JobId.Value : null,
					row.FreeCompanyId.HasValue ? (ulong)row.FreeCompanyId.Value : null,
					row.FreeCompanyName))
				.ToArray(),
			Array.Empty<ResolvedCharacterEntry>());

	public IReadOnlyList<CachedProfileEntry> GetCachedProfilesForKeys(IEnumerable<string> keys)
	{
		var result = new List<CachedProfileEntry>();
		foreach (var chunk in keys.Distinct().ToList().Chunk(BulkQueryBatchSize))
		{
			var chunkKeys = chunk.ToList();
			lock (this.gate)
			{
				if (this.disposed)
				{
					return result;
				}

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
		foreach (var chunk in keys.Distinct().ToList().Chunk(BulkQueryBatchSize))
		{
			var chunkKeys = chunk.ToList();
			lock (this.gate)
			{
				if (this.disposed)
				{
					return result;
				}

				result.AddRange(this.connection.Table<ResolvedCharacterRow>()
					.Where(row => chunkKeys.Contains(row.Key))
					.Select(row => new ResolvedCharacterEntry(
						row.Key, row.Name, (uint)row.HomeWorldId, (ulong)row.LodestoneId,
						UtcTimestamp.Parse(row.ResolvedAtUtc), row.AvatarUrlHash,
						row.Level, row.JobId.HasValue ? (uint)row.JobId.Value : null,
						row.FreeCompanyId.HasValue ? (ulong)row.FreeCompanyId.Value : null,
						row.FreeCompanyName)));
			}
		}

		return result;
	}

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

	public void Dispose()
	{
		lock (this.gate)
		{
			this.disposed = true;
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

		public long? FreeCompanyId { get; set; }

		public string? FreeCompanyName { get; set; }
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
