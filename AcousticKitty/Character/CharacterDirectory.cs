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
using Dalamud.Plugin.Services;
using SQLite;

namespace AcousticKitty.Character;

public sealed partial class CharacterDirectory : IDisposable
{
	private const int MaxConsecutiveLookupFailures = 5;

	private const int BulkQueryBatchSize = 500;

	private readonly SQLiteConnection connection;
	private readonly object gate = new();
	private readonly IPluginLog log;
	private readonly NameHistoryStore nameHistory;
	private bool disposed;

	#region Public API

	public CharacterDirectory(string pluginConfigDirectory, IPluginLog log)
	{
		this.log = log;
		Directory.CreateDirectory(pluginConfigDirectory);
		var databasePath = Path.Combine(pluginConfigDirectory, "character-cache.db3");

		this.connection = new SQLiteConnection(databasePath);
		this.connection.CreateTable<KnownCharacterRow>();
		this.nameHistory = new NameHistoryStore(this.connection, this.gate, () => this.disposed);
		DatabaseVacuum.RunInBackground(this.connection, this.gate, () => this.disposed);
	}

	public KnownCharacter? TryGetByContentId(ulong contentId) =>
		this.Read<KnownCharacter?>(
			() =>
			{
				var row = this.connection.Find<KnownCharacterRow>((long)contentId);
				return row == null ? null : ToKnownCharacter(row);
			},
			null);

	public KnownCharacter? TryGetByNameWorldKey(string nameWorldKey) =>
		this.Read<KnownCharacter?>(
			() =>
			{
				var row = this.connection.Table<KnownCharacterRow>()
					.FirstOrDefault(r => r.NameWorldKey == nameWorldKey &&
						r.LookupState != (int)NearbyLookupState.Transferred);
				return row == null ? null : ToKnownCharacter(row);
			},
			null);

	public KnownCharacter? TryGetActiveHolder(string nameWorldKey, ulong exceptContentId) =>
		this.Read<KnownCharacter?>(
			() =>
			{
				var except = (long)exceptContentId;
				var row = this.connection.Table<KnownCharacterRow>()
					.FirstOrDefault(r => r.NameWorldKey == nameWorldKey && r.ContentId != except &&
						r.LookupState != (int)NearbyLookupState.Transferred);
				return row == null ? null : ToKnownCharacter(row);
			},
			null);

	public void PromoteMatureConflictHolds(DateTime nowUtc) =>
		this.Write(() => this.connection.Execute(
			"UPDATE KnownCharacters SET LookupState = ?, PriorityAtUtc = NULL " +
			"WHERE LookupState = ? AND PriorityAtUtc IS NOT NULL AND PriorityAtUtc <= ?",
			(int)NearbyLookupState.Pending, (int)NearbyLookupState.ConflictHold,
			UtcTimestamp.Format(nowUtc)));

	public KnownCharacter? TryGetByLodestoneId(ulong lodestoneId) =>
		this.Read<KnownCharacter?>(
			() =>
			{
				var row = this.connection.Table<KnownCharacterRow>()
					.FirstOrDefault(r => r.LodestoneId == (long)lodestoneId);
				return row == null ? null : ToKnownCharacter(row);
			},
			null);

	public void UpdateNameWorld(ulong contentId, string newName, uint newHomeWorldId) =>
		this.Write(() =>
		{
			var row = this.connection.Find<KnownCharacterRow>((long)contentId);
			if (row == null)
			{
				return;
			}

			row.Name = newName;
			row.HomeWorldId = newHomeWorldId;
			row.NameWorldKey = CharacterKey.Build(newName, newHomeWorldId);
			this.connection.Update(row);
		});

	public void UpsertSnapshots(
		IReadOnlyList<(ulong ContentId, string NameWorldKey, PlayerLocalData Data, DateTime LastSeenUtc)> snapshots)
	{
		if (snapshots.Count == 0)
		{
			return;
		}

		this.Write(() => this.connection.RunInTransaction(() =>
		{
			foreach (var snapshot in snapshots)
			{
				this.UpsertSnapshotRow(
					snapshot.ContentId, snapshot.NameWorldKey, snapshot.Data, snapshot.LastSeenUtc);
			}
		}));
	}

	public void SetLookupResult(
		ulong contentId, ulong? lodestoneId, NearbyLookupState state, string? errorMessage,
		bool clearOverride = true, DateTime? priorityAtUtc = null) =>
		this.Write(() =>
		{
			var row = this.connection.Find<KnownCharacterRow>((long)contentId);
			if (row == null)
			{
				return;
			}

			var isRetriableFailure = state == NearbyLookupState.Pending && errorMessage != null;
			row.ConsecutiveFailureCount = isRetriableFailure ? row.ConsecutiveFailureCount + 1 : 0;

			row.LodestoneId = lodestoneId.HasValue ? (long)lodestoneId.Value : null;
			row.LookupState = isRetriableFailure &&
				row.ConsecutiveFailureCount >= MaxConsecutiveLookupFailures
					? (int)NearbyLookupState.Error
					: (int)state;
			row.LookupErrorMessage = errorMessage;
			if (priorityAtUtc.HasValue)
			{
				row.PriorityAtUtc = UtcTimestamp.Format(priorityAtUtc.Value);
			}

			if (clearOverride)
			{
				row.OverrideLodestoneId = null;
			}

			this.connection.Update(row);
		});

	public void QueueOverride(ulong contentId, ulong lodestoneId, DateTime priorityAtUtc) =>
		this.Write(() =>
		{
			var row = this.connection.Find<KnownCharacterRow>((long)contentId);
			if (row == null)
			{
				return;
			}

			row.LookupState = (int)NearbyLookupState.Pending;
			row.LookupErrorMessage = null;
			row.PriorityAtUtc = UtcTimestamp.Format(priorityAtUtc);
			row.OverrideLodestoneId = (long)lodestoneId;
			this.connection.Update(row);
		});

	public IReadOnlyList<KnownCharacter> GetPending() => this.GetPending(int.MaxValue);

	public IReadOnlyList<KnownCharacter> GetPending(int limit) =>
		this.Read<IReadOnlyList<KnownCharacter>>(
			() =>
			{
				var prioritized = this.connection.Table<KnownCharacterRow>()
					.Where(row =>
						row.LookupState == (int)NearbyLookupState.Pending && row.PriorityAtUtc != null)
					.OrderBy(row => row.PriorityAtUtc)
					.Take(limit)
					.Select(ToKnownCharacter)
					.ToArray();

				var remainingLimit = limit - prioritized.Length;
				if (remainingLimit <= 0)
				{
					return prioritized;
				}

				var remainder = this.connection.Table<KnownCharacterRow>()
					.Where(row =>
						row.LookupState == (int)NearbyLookupState.Pending && row.PriorityAtUtc == null)
					.OrderBy(row => row.LastSeenUtc)
					.Take(remainingLimit)
					.Select(ToKnownCharacter)
					.ToArray();

				return prioritized.Concat(remainder).ToArray();
			},
			Array.Empty<KnownCharacter>());

	public IReadOnlyList<KnownCharacter> GetFoundOrAccessRestricted() =>
		this.Read<IReadOnlyList<KnownCharacter>>(
			() => this.connection.Table<KnownCharacterRow>()
				.Where(row =>
					row.LookupState == (int)NearbyLookupState.Found ||
					row.LookupState == (int)NearbyLookupState.AccessRestricted)
				.OrderBy(row => row.LastSeenUtc)
				.Select(ToKnownCharacter)
				.ToArray(),
			Array.Empty<KnownCharacter>());

	public IReadOnlyList<KnownCharacter> GetHidden() =>
		this.Read<IReadOnlyList<KnownCharacter>>(
			() => this.connection.Table<KnownCharacterRow>()
				.Where(row => row.LookupState == (int)NearbyLookupState.NotFound)
				.OrderBy(row => row.LastSeenUtc)
				.Select(ToKnownCharacter)
				.ToArray(),
			Array.Empty<KnownCharacter>());

	public IReadOnlyList<KnownCharacter> GetAllWithFreeCompanyTag() =>
		this.Read<IReadOnlyList<KnownCharacter>>(
			() => this.connection.Table<KnownCharacterRow>()
				.Where(row => row.FreeCompanyTag != null && row.FreeCompanyTag != "")
				.Select(ToKnownCharacter)
				.ToArray(),
			Array.Empty<KnownCharacter>());

	public IReadOnlyList<string> GetAllNameWorldKeys() =>
		this.Read<IReadOnlyList<string>>(
			() => this.connection.Query<NameWorldKeyRow>("SELECT NameWorldKey FROM KnownCharacters")
				.Select(row => row.NameWorldKey)
				.ToArray(),
			Array.Empty<string>());

	public void DeleteMany(IEnumerable<ulong> contentIds)
	{
		var ids = contentIds.Select(id => (long)id).ToList();
		if (ids.Count == 0)
		{
			return;
		}

		this.Write(() => this.connection.RunInTransaction(() =>
		{
			foreach (var chunk in ids.Chunk(BulkQueryBatchSize))
			{
				var chunkIds = chunk.ToList();
				this.connection.Table<KnownCharacterRow>().Delete(row => chunkIds.Contains(row.ContentId));
				this.nameHistory.DeleteForContentIds(chunkIds);
			}
		}));
	}

	public void RecordNameHistory(ulong contentId, string name, string homeWorldName, DateTime seenUntilUtc) =>
		this.nameHistory.RecordNameHistory(contentId, name, homeWorldName, seenUntilUtc);

	public IReadOnlyList<string> GetHistoryWorldNames(ulong contentId) =>
		this.nameHistory.GetHistoryWorldNames(contentId);

	public IReadOnlyList<NameHistoryEntry> GetNameHistory(ulong contentId) =>
		this.nameHistory.GetNameHistory(contentId);

	public void Dispose()
	{
		lock (this.gate)
		{
			this.disposed = true;
			this.connection.Dispose();
		}
	}

	#endregion

	#region Private Implementation

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

	private void UpsertSnapshotRow(
		ulong contentId, string nameWorldKey, PlayerLocalData data, DateTime lastSeenUtc)
	{
		var existing = this.connection.Find<KnownCharacterRow>((long)contentId);
		var row = existing ?? new KnownCharacterRow
		{
			ContentId = (long)contentId,
			LookupState = (int)NearbyLookupState.Pending,
		};

		row.NameWorldKey = nameWorldKey;
		row.Name = data.Name;
		row.HomeWorldId = data.HomeWorldId;
		row.CurrentWorldId = data.CurrentWorldId;
		row.JobId = data.JobId;
		row.Level = data.Level;
		row.FreeCompanyTag = data.FreeCompanyTag;
		row.TitleId = data.TitleId;
		row.TerritoryId = data.TerritoryId;
		row.Gender = data.Gender;
		row.LastSeenUtc = UtcTimestamp.Format(lastSeenUtc);

		if (existing == null)
		{
			this.connection.Insert(row);
		}
		else
		{
			this.connection.Update(row);
		}
	}

	private static KnownCharacter ToKnownCharacter(KnownCharacterRow row) => new(
		new PlayerLocalData(
			row.Name,
			(uint)row.HomeWorldId,
			(ulong)row.ContentId,
			(uint)row.JobId,
			row.Level,
			row.FreeCompanyTag,
			(uint)row.TitleId,
			(uint)row.TerritoryId,
			(uint)row.CurrentWorldId,
			row.Gender),
		UtcTimestamp.Parse(row.LastSeenUtc),
		row.LodestoneId.HasValue ? (ulong)row.LodestoneId.Value : null,
		(NearbyLookupState)row.LookupState,
		row.LookupErrorMessage,
		row.PriorityAtUtc != null ? UtcTimestamp.Parse(row.PriorityAtUtc) : null,
		row.OverrideLodestoneId.HasValue ? (ulong)row.OverrideLodestoneId.Value : null);

	#endregion

	#region Row Types

	[Table("KnownCharacters")]
	private sealed class KnownCharacterRow
	{
		[PrimaryKey]
		public long ContentId { get; set; }

		[Indexed]
		public string NameWorldKey { get; set; } = string.Empty;
		public string Name { get; set; } = string.Empty;
		public long HomeWorldId { get; set; }
		public long CurrentWorldId { get; set; }
		public long JobId { get; set; }
		public int Level { get; set; }
		public string? FreeCompanyTag { get; set; }
		public long TitleId { get; set; }
		public long TerritoryId { get; set; }
		public string Gender { get; set; } = string.Empty;
		public string LastSeenUtc { get; set; } = string.Empty;

		[Indexed]
		public long? LodestoneId { get; set; }

		[Indexed]
		public int LookupState { get; set; } = (int)NearbyLookupState.Pending;
		public string? LookupErrorMessage { get; set; }
		public string? PriorityAtUtc { get; set; }
		public long? OverrideLodestoneId { get; set; }
		public int ConsecutiveFailureCount { get; set; }
	}

	private sealed class NameWorldKeyRow
	{
		public string NameWorldKey { get; set; } = string.Empty;
	}

	#endregion
}
