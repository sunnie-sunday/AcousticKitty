// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AcousticKitty.Character;
using AcousticKitty.Common;
using AcousticKitty.Lodestone;
using AcousticKitty.Windows.Views;
using Dalamud.Plugin.Services;

namespace AcousticKitty.Echo;

public enum EchoQueueState
{
	Idle,
	Verifying,
	WaitingToRetry,
	Paused,
}

public sealed class EchoService : IDisposable
{
	private static readonly TimeSpan SessionToVerifyDelayMin = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan SessionToVerifyDelayMax = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan MaxCharacterBackoff = TimeSpan.FromMinutes(30);
	private static readonly TimeSpan NoEligibleMatchPollInterval = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan SnapshotRebuildInterval = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan AutoStartCheckInterval = TimeSpan.FromSeconds(5);

	private readonly IFramework framework;
	private readonly IEchoClientFactory clientFactory;
	private readonly EchoStore echoStore;
	private readonly CharacterDirectory characterDirectory;
	private readonly LodestoneCache lodestoneCache;
	private readonly AvatarWorldHistoryService avatarWorldHistory;
	private readonly IDataManager dataManager;
	private readonly Configuration configuration;
	private readonly IPluginLog log;
	private readonly AlreadyPinnedLogWriter alreadyPinnedLogWriter;
	private readonly CancellationTokenSource disposalCts = new();

	private DateTime lastAutoStartCheckUtc = DateTime.MinValue;

	private readonly ConcurrentDictionary<ulong, DateTime> nextEligibleAtUtc = new();
	private readonly ConcurrentDictionary<ulong, int> consecutiveFailures = new();
	private int isProcessingFlag;
	private volatile EchoQueueState state = EchoQueueState.Idle;
	private volatile string? statusReason;
	private CancellationTokenSource? processingStopCts;

	private DateTime nextRetryUtc;
	private DateTime lastSnapshotRebuildUtc = DateTime.MinValue;
	private volatile IReadOnlyList<PendingMatch> pendingSnapshot = Array.Empty<PendingMatch>();

	private long currentlyVerifyingContentId = -1;

	#region Public API

	public EchoService(
		IFramework framework,
		IEchoClientFactory clientFactory,
		EchoStore echoStore,
		CharacterDirectory characterDirectory,
		LodestoneCache lodestoneCache,
		AvatarWorldHistoryService avatarWorldHistory,
		IDataManager dataManager,
		Configuration configuration,
		IPluginLog log,
		string pluginConfigDirectory)
	{
		this.framework = framework;
		this.clientFactory = clientFactory;
		this.echoStore = echoStore;
		this.characterDirectory = characterDirectory;
		this.lodestoneCache = lodestoneCache;
		this.avatarWorldHistory = avatarWorldHistory;
		this.dataManager = dataManager;
		this.configuration = configuration;
		this.log = log;
		this.alreadyPinnedLogWriter = new AlreadyPinnedLogWriter(pluginConfigDirectory);

		this.framework.Update += this.OnFrameworkUpdate;

		this.RebuildPendingSnapshot();
		if (this.pendingSnapshot.Count > 0 && this.configuration.EchoQueueMode == EchoQueueMode.Auto)
		{
			_ = this.ProcessQueueAsync();
		}
	}

	public EchoQueueState State => this.state;

	public string? StatusMessage
	{
		get
		{
			var reason = this.statusReason;
			if (reason == null)
			{
				return null;
			}

			var remaining = this.nextRetryUtc - DateTime.UtcNow;
			if (remaining < TimeSpan.Zero)
			{
				remaining = TimeSpan.Zero;
			}

			return $"{reason} - retrying in {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}.";
		}
	}

	public IReadOnlyList<PendingMatch> PendingVerifications
	{
		get
		{
			this.RebuildPendingSnapshot();
			return this.pendingSnapshot;
		}
	}

	public ulong? CurrentlyVerifyingContentId
	{
		get
		{
			var value = Interlocked.Read(ref this.currentlyVerifyingContentId);
			return value < 0 ? null : unchecked((ulong)value);
		}
	}

	public int PendingVerifyCount => this.PendingVerifications.Count;

	public bool IsProcessing => this.isProcessingFlag != 0;

	public void NotifyMatchConfirmed()
	{
		DatabaseCapEnforcer.EnforceUnverified(
			this.characterDirectory, this.lodestoneCache, this.echoStore,
			this.CurrentlyVerifyingContentId);

		if (this.isProcessingFlag == 0 && this.configuration.EchoQueueMode == EchoQueueMode.Auto)
		{
			_ = this.ProcessQueueAsync();
		}
	}

	public void StartQueue() => _ = this.ProcessQueueAsync();

	public void StopQueue() => this.processingStopCts?.Cancel();

	public void Dispose()
	{
		this.framework.Update -= this.OnFrameworkUpdate;
		this.disposalCts.Cancel();
		this.disposalCts.Dispose();
	}

	#endregion

	#region Private Implementation

	private void OnFrameworkUpdate(IFramework unused)
	{
		var now = DateTime.UtcNow;
		if (now - this.lastAutoStartCheckUtc < AutoStartCheckInterval)
		{
			return;
		}

		this.lastAutoStartCheckUtc = now;

		if (this.isProcessingFlag == 0 && this.configuration.EchoQueueMode == EchoQueueMode.Auto)
		{
			_ = this.ProcessQueueAsync();
		}
	}

	private void RebuildPendingSnapshot()
	{
		var now = DateTime.UtcNow;
		if (now - this.lastSnapshotRebuildUtc < SnapshotRebuildInterval)
		{
			return;
		}

		this.lastSnapshotRebuildUtc = now;
		this.pendingSnapshot = this.GetUnverifiedMatches();
	}

	private IReadOnlyList<PendingMatch> GetUnverifiedMatches() =>
		DatabaseCapEnforcer.GetUnverifiedCandidates(this.characterDirectory, this.echoStore)
			.Select(known => new PendingMatch(
				known.Data.ContentId, known.Data.Name, known.Data.HomeWorldId, known.LodestoneId!.Value))
			.ToArray();

	private async Task ProcessQueueAsync()
	{
		if (Interlocked.CompareExchange(ref this.isProcessingFlag, 1, 0) != 0)
		{
			return;
		}

		var stopCts = CancellationTokenSource.CreateLinkedTokenSource(this.disposalCts.Token);
		this.processingStopCts = stopCts;
		try
		{
			while (true)
			{
				IReadOnlyList<PendingMatch> candidates;
				try
				{
					candidates = this.GetUnverifiedMatches();
				}
				catch (Exception ex)
				{
					this.log.Error(ex, "Failed to read verification candidates; retrying shortly.");
					await Task.Delay(NoEligibleMatchPollInterval, stopCts.Token).ConfigureAwait(false);
					continue;
				}

				if (candidates.Count == 0)
				{
					this.state = EchoQueueState.Idle;
					this.statusReason = null;
					this.nextEligibleAtUtc.Clear();
					this.consecutiveFailures.Clear();
					await Task.Delay(NoEligibleMatchPollInterval, stopCts.Token).ConfigureAwait(false);
					continue;
				}

				var match = candidates.FirstOrDefault(candidate =>
					this.nextEligibleAtUtc.GetValueOrDefault(candidate.ContentId, DateTime.MinValue) <=
					DateTime.UtcNow);
				if (match == null)
				{
					this.state = EchoQueueState.WaitingToRetry;
					await Task.Delay(NoEligibleMatchPollInterval, stopCts.Token).ConfigureAwait(false);
					continue;
				}

				this.state = EchoQueueState.Verifying;

				Interlocked.Exchange(ref this.currentlyVerifyingContentId, unchecked((long)match.ContentId));
				TimeSpan? retryAfter;
				try
				{
					retryAfter = await this.TrySendAsync(match, stopCts.Token).ConfigureAwait(false);
				}
				finally
				{
					Interlocked.Exchange(ref this.currentlyVerifyingContentId, -1);
				}

				if (retryAfter is { } delay)
				{
					this.nextEligibleAtUtc[match.ContentId] = DateTime.UtcNow + delay;
					this.state = EchoQueueState.WaitingToRetry;
					continue;
				}

				this.nextEligibleAtUtc.TryRemove(match.ContentId, out _);
				this.consecutiveFailures.TryRemove(match.ContentId, out _);
				this.statusReason = null;
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			this.isProcessingFlag = 0;
			this.processingStopCts = null;
			this.state = EchoQueueState.Paused;
			stopCts.Dispose();
		}
	}

	private async Task<TimeSpan?> TrySendAsync(PendingMatch match, CancellationToken cancellationToken)
	{
		try
		{
			var registerTranscript = new StringBuilder();
			var verifyTranscript = new StringBuilder();

			using var attemptClient = this.clientFactory.Create();

			var registration = await attemptClient.RegisterAsync(cancellationToken, registerTranscript)
				.ConfigureAwait(false);

			await attemptClient.CreateSessionAsync(registration, cancellationToken).ConfigureAwait(false);

			var sessionToVerifyDelay = TimeSpan.FromMilliseconds(Random.Shared.Next(
				(int)SessionToVerifyDelayMin.TotalMilliseconds, (int)SessionToVerifyDelayMax.TotalMilliseconds));
			await Task.Delay(sessionToVerifyDelay, cancellationToken).ConfigureAwait(false);

			var homeWorldName = GameDataResolver.ResolveWorldName(this.dataManager, match.HomeWorldId);

			var outcome = await attemptClient.VerifySavedAsync(
				registration,
				match.ContentId,
				match.CharacterName,
				homeWorldName,
				match.LodestoneId,
				cancellationToken,
				verifyTranscript).ConfigureAwait(false);

			var cacheKey = CharacterKey.Build(match.CharacterName, match.HomeWorldId);

			if (outcome == EchoSaveOutcome.AlreadyPinned)
			{
				this.echoStore.Pin(match.LodestoneId, match.CharacterName, match.HomeWorldId);

				await this.avatarWorldHistory.DiscoverWorldHistoryAsync(
					match.ContentId, this.lodestoneCache.TryGetResolvedAvatarUrlHash(cacheKey),
					homeWorldName, cancellationToken).ConfigureAwait(false);

				this.alreadyPinnedLogWriter.Write(
					this.BuildAlreadyPinnedLogDetails(match, homeWorldName, cacheKey),
					registerTranscript.ToString(),
					verifyTranscript.ToString());
			}

			this.echoStore.MarkVerified(match.LodestoneId, DateTime.UtcNow);

			return null;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (EchoRateLimitedException ex)
		{
			this.statusReason = "Rate-limited by the Echo API";
			var delay = this.RecordFailureAndGetBackoff(match.ContentId, DefaultRetryAfter);
			this.nextRetryUtc = DateTime.UtcNow + delay;
			this.log.Debug($"Echo API rate-limited verifying {match.CharacterName}: {ex.GetType()}: {ex.Message}");
			return delay;
		}
		catch (EchoException ex)
		{
			this.statusReason = "Rejected by the Echo API";
			var delay = this.RecordFailureAndGetBackoff(match.ContentId, DefaultRetryAfter);
			this.nextRetryUtc = DateTime.UtcNow + delay;
			this.log.Warning($"Echo API rejected verifying {match.CharacterName}: {ex.GetType()}: {ex.Message}");
			return delay;
		}
		catch (Exception ex)
		{
			this.statusReason = "The Echo API is unreachable";
			var delay = this.RecordFailureAndGetBackoff(match.ContentId, DefaultRetryAfter);
			this.nextRetryUtc = DateTime.UtcNow + delay;
			this.log.Warning($"Echo API unreachable verifying {match.CharacterName}: {ex.GetType()}: {ex.Message}");
			return delay;
		}
	}

	private TimeSpan RecordFailureAndGetBackoff(ulong contentId, TimeSpan baseDelay)
	{
		var failures = this.consecutiveFailures.GetValueOrDefault(contentId, 0);
		this.consecutiveFailures[contentId] = failures + 1;

		var scaledTicks = baseDelay.Ticks * Math.Pow(2, failures);
		var cappedTicks = Math.Min(scaledTicks, MaxCharacterBackoff.Ticks);
		return TimeSpan.FromTicks((long)cappedTicks);
	}

	private AlreadyPinnedLogDetails BuildAlreadyPinnedLogDetails(
		PendingMatch match, string homeWorldName, string cacheKey)
	{
		var known = this.characterDirectory.TryGetByContentId(match.ContentId)!;
		var data = known.Data;

		var profile = this.lodestoneCache.TryGetCachedProfile(cacheKey)?.Profile;
		var resolved = this.lodestoneCache.TryGetResolved(cacheKey);
		var avatarUrl = ProfileView.ResolveAvatarUrl(profile, resolved?.AvatarUrlHash, homeWorldName)!;

		var history = this.characterDirectory.GetNameHistory(match.ContentId);
		var akaEntries = history
			.Where(entry => !string.IsNullOrEmpty(entry.Name))
			.Select(entry => $"{entry.Name}@{entry.HomeWorldName}")
			.ToArray();
		var worlds = history
			.Select(entry => entry.HomeWorldName)
			.Append(homeWorldName)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(world => world, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var seen =
			$"{GameDataResolver.ResolveTerritoryName(this.dataManager, data.TerritoryId)}@" +
			GameDataResolver.ResolveWorldName(this.dataManager, data.CurrentWorldId);

		return new AlreadyPinnedLogDetails(
			data.Name,
			homeWorldName,
			avatarUrl,
			seen,
			known.LastSeenUtc,
			GameDataResolver.ResolveTitleName(this.dataManager, data.TitleId, data.Gender),
			GameDataResolver.ResolveJobAbbreviation(this.dataManager, data.JobId),
			data.Level,
			match.LodestoneId,
			profile?.FreeCompanyName ?? resolved?.FreeCompanyName,
			profile?.FreeCompanyId ?? resolved?.FreeCompanyId,
			akaEntries.Length > 0 ? string.Join(", ", akaEntries) : null,
			worlds.Length > 1 ? string.Join(", ", worlds) : null);
	}

	#endregion
}

#region Already-Pinned Log Writer

internal sealed record AlreadyPinnedLogDetails(
	string Name,
	string World,
	string AvatarUrl,
	string Seen,
	DateTime SeenUtc,
	string? Title,
	string Job,
	int Level,
	ulong LodestoneId,
	string? FreeCompanyName,
	ulong? FreeCompanyId,
	string? Aka,
	string? Transfers);

internal sealed class AlreadyPinnedLogWriter
{
	private readonly string directory;

	public AlreadyPinnedLogWriter(string pluginConfigDirectory)
	{
		this.directory = Path.Combine(pluginConfigDirectory, "already_claimed");
		Directory.CreateDirectory(this.directory);
	}

	public void Write(AlreadyPinnedLogDetails details, string registerTranscript, string verifyTranscript)
	{
		var fileName = $"{details.LodestoneId}.md";
		var content = AlreadyPinnedLogWriter.BuildFrontmatter(details) +
			"```http\n" + registerTranscript + "```\n" +
			"```http\n" + verifyTranscript + "```\n";
		File.WriteAllText(Path.Combine(this.directory, fileName), content, Encoding.UTF8);
	}

	private static string BuildFrontmatter(AlreadyPinnedLogDetails details)
	{
		var sb = new StringBuilder();
		sb.Append("---\n");
		AlreadyPinnedLogWriter.AppendField(sb, "name", details.Name);
		AlreadyPinnedLogWriter.AppendField(sb, "world", details.World);
		AlreadyPinnedLogWriter.AppendField(sb, "avatar_url", details.AvatarUrl);
		AlreadyPinnedLogWriter.AppendField(sb, "seen", details.Seen);
		AlreadyPinnedLogWriter.AppendField(sb, "utc", UtcTimestamp.Format(details.SeenUtc));
		AlreadyPinnedLogWriter.AppendField(sb, "title", details.Title);
		AlreadyPinnedLogWriter.AppendField(sb, "job", details.Job);
		sb.Append("level: ").Append(details.Level).Append('\n');
		AlreadyPinnedLogWriter.AppendField(sb, "lodestone_id", details.LodestoneId.ToString());
		AlreadyPinnedLogWriter.AppendField(sb, "fc_name", details.FreeCompanyName);
		AlreadyPinnedLogWriter.AppendField(sb, "fc_lodestone_id", details.FreeCompanyId?.ToString());
		AlreadyPinnedLogWriter.AppendField(sb, "aka", details.Aka);
		AlreadyPinnedLogWriter.AppendField(sb, "transfers", details.Transfers);
		sb.Append("---\n");
		return sb.ToString();
	}

	private static void AppendField(StringBuilder sb, string key, string? value)
	{
		if (value != null)
		{
			sb.Append(key).Append(": \"").Append(value).Append("\"\n");
		}
	}
}

#endregion
