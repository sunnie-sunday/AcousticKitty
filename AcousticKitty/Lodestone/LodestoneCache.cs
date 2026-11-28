// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Globalization;
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
		this.connection.CreateTable<PinnedProfileRow>();
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
					UtcTimestamp.Parse(row.ResolvedAtUtc), row.AvatarUrlHash,
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
		ulong? freeCompanyId = null,
		string? freeCompanyName = null,
		bool freeCompanyKnown = false) =>
		this.Write(() => this.UpsertResolvedRow(
			key, lodestoneId, name, homeWorldId, resolvedAtUtc, avatarUrlHash, freeCompanyId,
			freeCompanyName, freeCompanyKnown));

	public void SaveResolvedIds(
		IReadOnlyList<(
			string Key, ulong LodestoneId, string Name, uint HomeWorldId, DateTime ResolvedAtUtc,
			string? AvatarUrlHash, ulong? FreeCompanyId, string? FreeCompanyName)> updates,
		bool freeCompanyKnown)
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
					update.ResolvedAtUtc, update.AvatarUrlHash, update.FreeCompanyId,
					update.FreeCompanyName, freeCompanyKnown);
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
		ulong? freeCompanyId,
		string? freeCompanyName,
		bool freeCompanyKnown)
	{
		var existing = this.connection.Find<ResolvedCharacterRow>(key);
		var row = existing ?? new ResolvedCharacterRow { Key = key };
		row.LodestoneId = (long)lodestoneId;
		row.Name = name;
		row.HomeWorldId = homeWorldId;
		row.ResolvedAtUtc = UtcTimestamp.Format(resolvedAtUtc);
		row.AvatarUrlHash = avatarUrlHash ?? row.AvatarUrlHash;
		var freeCompanyOnWorld = freeCompanyId is not { } incomingId ||
			FreeCompanyIds.HomeWorldId(incomingId) == homeWorldId;
		if (freeCompanyKnown && freeCompanyId == null)
		{
			row.FreeCompanyId = null;
			row.FreeCompanyName = null;
		}
		else if (freeCompanyOnWorld)
		{
			row.FreeCompanyId =
				freeCompanyId.HasValue ? (long)freeCompanyId.Value : row.FreeCompanyId;
			row.FreeCompanyName = freeCompanyName ?? row.FreeCompanyName;
		}

		LodestoneCache.ClearFreeCompanyIfOffWorld(row, homeWorldId);

		if (existing == null)
		{
			this.connection.Insert(row);
		}
		else
		{
			this.connection.Update(row);
		}
	}

	private static void ClearFreeCompanyIfOffWorld(ResolvedCharacterRow row, uint homeWorldId)
	{
		if (row.FreeCompanyId is { } freeCompanyId &&
			FreeCompanyIds.HomeWorldId((ulong)freeCompanyId) != homeWorldId)
		{
			row.FreeCompanyId = null;
			row.FreeCompanyName = null;
		}
	}

	public void RenameCharacter(
		string oldKey, string newKey, string newName, uint newHomeWorldId, ulong lodestoneId)
	{
		if (oldKey == newKey)
		{
			return;
		}

		this.Write(() => this.connection.RunInTransaction(() =>
		{
			KeyedRowMigration.Migrate<ResolvedCharacterRow>(
				this.connection, oldKey, newKey, row => row.LodestoneId == (long)lodestoneId,
				row =>
				{
					row.Key = newKey;
					row.Name = newName;
					row.HomeWorldId = newHomeWorldId;
					LodestoneCache.ClearFreeCompanyIfOffWorld(row, newHomeWorldId);
				});
			KeyedRowMigration.Migrate<CachedProfileRow>(
				this.connection, oldKey, newKey,
				row => JsonSerializer.Deserialize<LodestoneProfile>(row.ProfileJson)?.LodestoneId ==
					lodestoneId,
				row => row.Key = newKey);
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

	public (LodestoneProfile? Profile, DateTime? FetchedAtUtc) ResolveProfileOrPartial(string key, bool isPrivate)
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
				resolved.FreeCompanyId, resolved.FreeCompanyName, isPrivate), null)
			: (null, null);
	}

	public bool SaveCachedProfile(string key, LodestoneProfile profile, DateTime fetchedAtUtc) =>
		this.Read(
			() =>
			{
				var existing = this.connection.Find<CachedProfileRow>(key);
				var previous = existing != null
					? JsonSerializer.Deserialize<LodestoneProfile>(existing.ProfileJson)
					: null;
				if (previous != null && ProfileRegressionGuard.IsRegression(profile, previous))
				{
					return false;
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
						row.FreeCompanyId.HasValue ? (ulong)row.FreeCompanyId.Value : null,
						row.FreeCompanyName)));
			}
		}

		return result;
	}

	public IReadOnlyList<ResolvedCharacterEntry> GetResolvedForLodestoneIds(
		IEnumerable<ulong> lodestoneIds)
	{
		var result = new List<ResolvedCharacterEntry>();
		var ids = lodestoneIds.Distinct().Select(id => (long)id).ToList();
		foreach (var chunk in ids.Chunk(BulkQueryBatchSize))
		{
			var chunkIds = chunk.ToList();
			lock (this.gate)
			{
				if (this.disposed)
				{
					return result;
				}

				result.AddRange(this.connection.Table<ResolvedCharacterRow>()
					.Where(row => chunkIds.Contains(row.LodestoneId))
					.Select(row => new ResolvedCharacterEntry(
						row.Key, row.Name, (uint)row.HomeWorldId, (ulong)row.LodestoneId,
						UtcTimestamp.Parse(row.ResolvedAtUtc), row.AvatarUrlHash,
						row.FreeCompanyId.HasValue ? (ulong)row.FreeCompanyId.Value : null,
						row.FreeCompanyName)));
			}
		}

		return result;
	}

	public void SavePinnedProfileSnapshot(ulong lodestoneId, string cacheKey, bool isPrivate)
	{
		var (profile, fetchedAtUtc) = this.ResolveProfileOrPartial(cacheKey, isPrivate);
		if (profile != null)
		{
			this.SavePinnedProfileSnapshot(lodestoneId, profile, fetchedAtUtc);
		}
	}

	public void SavePinnedProfileSnapshot(
		ulong lodestoneId, LodestoneProfile profile, DateTime? fetchedAtUtc)
	{
		if (profile.LodestoneId != lodestoneId)
		{
			return;
		}

		this.Write(() => this.connection.InsertOrReplace(new PinnedProfileRow
		{
			Key = lodestoneId.ToString(CultureInfo.InvariantCulture),
			ProfileJson = JsonSerializer.Serialize(profile),
			FetchedAtUtc = fetchedAtUtc.HasValue ? UtcTimestamp.Format(fetchedAtUtc.Value) : null,
		}));
	}

	public PinnedProfileSnapshot? TryGetPinnedProfileSnapshot(ulong lodestoneId) =>
		this.Read<PinnedProfileSnapshot?>(
			() =>
			{
				var row = this.connection.Find<PinnedProfileRow>(
					lodestoneId.ToString(CultureInfo.InvariantCulture));
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
